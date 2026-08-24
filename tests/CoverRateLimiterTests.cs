using System.Net;
using Cove.TorrentMetadata;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// Covers the pacing promised to the tracker's staff.
///
/// These assert numbers that were quoted to a third party, which makes them a different kind of test
/// from most here: a change that turns one red is a change to something someone was told, not just a
/// regression. The reply to staff names the interval, the burst, the per-host concurrency and the
/// breaker threshold, so each has a test whose name says what it is.
///
/// Nothing sleeps. <see cref="FakeClock"/> stands in for both the clock and the delay, so a wait is
/// recorded and the clock jumps — a suite that spent the seconds it is asserting would take minutes
/// and would be the first thing anyone disabled.
/// </summary>
public class CoverRateLimiterTests
{
    private const string Host = "images.example.invalid";
    private const string OtherHost = "cdn.other.invalid";

    /// <summary>A distinct host name for the host-map cap tests below, which need many of them.</summary>
    private static string HostNamed(int i) => $"host-{i}.example.invalid";

    // ---------------------------------------------------------------------
    // The bucket
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Lets_a_burst_through_before_it_starts_metering()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        for (var i = 0; i < CoverRateLimiter.Burst; i++)
            (await limiter.AcquireAsync(Host)).Dispose();

        // A browser opening a page fetches a handful of images at once. Refusing to do that would be
        // less like a person browsing, not more, which is the criterion staff actually gave.
        Assert.Empty(clock.Waits);

        (await limiter.AcquireAsync(Host)).Dispose();

        Assert.Equal([CoverRateLimiter.MinimumInterval], clock.Waits);
    }

    [Fact]
    public async Task Meters_each_host_on_its_own_budget()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        for (var i = 0; i < CoverRateLimiter.Burst; i++)
            (await limiter.AcquireAsync(Host)).Dispose();

        (await limiter.AcquireAsync(OtherHost)).Dispose();

        // A third-party image host and the tracker's own host have nothing to do with each other. A
        // global budget would let a slow image host throttle requests to the tracker, which is both
        // useless to the tracker and worse for the user.
        Assert.Empty(clock.Waits);
    }

    [Fact]
    public async Task Refills_the_bucket_as_time_passes()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        for (var i = 0; i < CoverRateLimiter.Burst; i++)
            (await limiter.AcquireAsync(Host)).Dispose();

        clock.Advance(TimeSpan.FromSeconds(10));
        for (var i = 0; i < CoverRateLimiter.Burst; i++)
            (await limiter.AcquireAsync(Host)).Dispose();

        // Ten idle seconds earn ten tokens but the bucket holds three, so an idle importer does not
        // bank an unbounded right to flood on its next run.
        Assert.Empty(clock.Waits);

        (await limiter.AcquireAsync(Host)).Dispose();
        Assert.Equal([CoverRateLimiter.MinimumInterval], clock.Waits);
    }

    [Fact]
    public async Task Allows_only_one_request_per_host_at_a_time()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        var held = await limiter.AcquireAsync(Host);
        var queued = limiter.AcquireAsync(Host);

        // Tokens are available, so nothing but the concurrency gate can be holding this back.
        Assert.False(queued.IsCompleted);

        held.Dispose();
        (await queued).Dispose();
    }

    // ---------------------------------------------------------------------
    // Backoff
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Waits_out_the_retry_after_the_host_asked_for()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        limiter.RecordFailure(Host, TimeSpan.FromSeconds(5));
        (await limiter.AcquireAsync(Host)).Dispose();

        // Honoured as given rather than folded into our own schedule. It is the host saying what it
        // wants, and second-guessing that is the behaviour that gets an extension blocked.
        Assert.Equal([TimeSpan.FromSeconds(5)], clock.Waits);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    public async Task Doubles_the_wait_for_each_consecutive_failure_with_no_retry_after(
        int failures, int expectedSeconds)
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        for (var i = 0; i < failures; i++)
            limiter.RecordFailure(Host);

        (await limiter.AcquireAsync(Host)).Dispose();

        // Without this a host that is merely down is polled at the full rate all night, which is the
        // load pattern most likely to look deliberate from the far end.
        Assert.Equal([TimeSpan.FromSeconds(expectedSeconds)], clock.Waits);
    }

    [Fact]
    public async Task Forgets_the_backoff_once_a_request_succeeds()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        limiter.RecordFailure(Host);
        limiter.RecordFailure(Host);
        limiter.RecordSuccess(Host);

        (await limiter.AcquireAsync(Host)).Dispose();

        // A single success closes it out. A host that recovered must not keep paying for a blip, or
        // the backoff becomes a permanent tax on a working import.
        Assert.Empty(clock.Waits);
    }

    [Fact]
    public async Task Refuses_rather_than_waiting_longer_than_a_request_can_afford()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        limiter.RecordFailure(Host, TimeSpan.FromMinutes(5));

        var refused = await Assert.ThrowsAsync<CoverThrottledException>(() => limiter.AcquireAsync(Host));

        // The wait is spent inside the caller's HTTP timeout, so waiting out five minutes would be
        // cancelled and would have achieved nothing but a slow failure. Not sending at all is the
        // more polite outcome anyway.
        Assert.Empty(clock.Waits);
        Assert.Contains(Host, refused.Reason);
        Assert.Contains("longer than this request can wait", refused.Reason);
    }

    // ---------------------------------------------------------------------
    // The deadline: maxWait bounds the whole gate, not each step alone
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Bounds_the_total_time_at_the_gate_by_maxWait_not_each_wait_separately()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);
        var maxWait = TimeSpan.FromSeconds(2);

        // Hold the one concurrency slot so the next request has to queue at the semaphore first —
        // exactly the shape of the defect: a preview promised a 2s bound spends most of it just getting
        // past the gate, and the old code re-applied the full 2s again for what came after.
        var held = await limiter.AcquireAsync(Host, maxWait);

        // Once past the semaphore, the request would still owe a 1.1s backoff wait — on its own,
        // well under the 2s ceiling.
        limiter.RecordFailure(Host, TimeSpan.FromSeconds(3));

        var queued = limiter.AcquireAsync(Host, maxWait);
        Assert.False(queued.IsCompleted);

        // Most of the budget is spent simply waiting for the slot: 1.9s of the 2s ceiling gone before
        // the backoff wait is even reached.
        clock.Advance(TimeSpan.FromSeconds(1.9));
        held.Dispose();

        var refused = await Assert.ThrowsAsync<CoverThrottledException>(() => queued);

        // Only 0.1s of the 2s budget is left when the backoff is checked, nowhere near the 1.1s still
        // owed — so the request is refused rather than sleeping past the ceiling its caller was
        // promised. A limiter that measured only the token-bucket step against maxWait, as before,
        // would happily sleep the full 1.1s here and let the total run to ~3s.
        Assert.Empty(clock.Waits);
        Assert.Contains(Host, refused.Reason);

        // RetryAfter still reports what the host actually asked for (1.1s), not the sliver of budget
        // that was left — a caller told to come back in 0.1s would hammer a host still mid-backoff.
        Assert.Equal(TimeSpan.FromSeconds(1.1), refused.RetryAfter);
    }

    [Fact]
    public async Task Still_succeeds_when_the_remaining_budget_covers_the_wait()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);
        var maxWait = TimeSpan.FromSeconds(2);

        var held = await limiter.AcquireAsync(Host, maxWait);
        limiter.RecordFailure(Host, TimeSpan.FromSeconds(1.5));

        var queued = limiter.AcquireAsync(Host, maxWait);

        // Only a little of the budget spent queuing this time — plenty left for what remains of the
        // 1.5s backoff (1s, by the time the semaphore is released).
        clock.Advance(TimeSpan.FromSeconds(0.5));
        held.Dispose();

        (await queued).Dispose();

        // The remaining backoff is honoured in full: the deadline bounds the total, it does not
        // shrink an individual wait that still comfortably fits inside what is left of it.
        Assert.Equal([TimeSpan.FromSeconds(1)], clock.Waits);
    }

    // ---------------------------------------------------------------------
    // The circuit breaker
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Stops_asking_a_host_that_has_failed_five_times_in_a_row()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        for (var i = 0; i < CoverRateLimiter.BreakerThreshold; i++)
            limiter.RecordFailure(Host);

        var refused = await Assert.ThrowsAsync<CoverThrottledException>(() => limiter.AcquireAsync(Host));

        // Refused outright rather than queued behind a backoff: the point of the breaker is to stop
        // a bad night from looking like a deliberate hammering, and a queue would still be requests.
        //
        // The reason is asserted, not just the exception type. Both refusals throw the same type, and
        // a backoff long enough to exceed MaxWait would satisfy a type-only assertion while the
        // breaker did nothing at all.
        Assert.Empty(clock.Waits);
        Assert.Contains(Host, refused.Reason);
        Assert.Contains("in a row", refused.Reason);
    }

    [Fact]
    public async Task Leaves_the_other_hosts_alone_when_one_trips_the_breaker()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        for (var i = 0; i < CoverRateLimiter.BreakerThreshold; i++)
            limiter.RecordFailure(Host);

        (await limiter.AcquireAsync(OtherHost)).Dispose();

        // One dead image host must not stop covers coming from anywhere else — most releases carry
        // their cover on a different host from the tracker's own.
        Assert.Empty(clock.Waits);
    }

    [Fact]
    public async Task Tries_one_request_again_after_the_cooldown_and_closes_on_success()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        for (var i = 0; i < CoverRateLimiter.BreakerThreshold; i++)
            limiter.RecordFailure(Host);

        clock.Advance(CoverRateLimiter.BreakerCooldown + TimeSpan.FromSeconds(1));

        // Half-open: the concurrency gate is one per host, so "a single trial" needs no extra state.
        (await limiter.AcquireAsync(Host)).Dispose();
        Assert.Empty(clock.Waits);

        limiter.RecordSuccess(Host);
        (await limiter.AcquireAsync(Host)).Dispose();

        // Closed again. Without this the breaker is a one-way door and a host that came back stays
        // unreachable until a restart.
        Assert.Empty(clock.Waits);
    }

    [Fact]
    public async Task Re_opens_the_breaker_when_the_trial_request_fails_too()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        for (var i = 0; i < CoverRateLimiter.BreakerThreshold; i++)
            limiter.RecordFailure(Host);

        clock.Advance(CoverRateLimiter.BreakerCooldown + TimeSpan.FromSeconds(1));
        (await limiter.AcquireAsync(Host)).Dispose();
        limiter.RecordFailure(Host);

        // A host that is still down gets another full cooldown rather than one trial per request.
        // Asserted on the reason: without that this passes on the backoff being longer than a
        // request can wait, which would leave the breaker itself untested past the first trip.
        var refused = await Assert.ThrowsAsync<CoverThrottledException>(() => limiter.AcquireAsync(Host));
        Assert.Contains("in a row", refused.Reason);
    }

    // ---------------------------------------------------------------------
    // Bounding the host map: _hosts is keyed by strings out of an untrusted .torrent's cover
    // URLs, and a `*.host` allowlist entry admits an unbounded number of distinct subdomains
    // under it — so the map needs its own cap, separate from the pacing numbers above.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Caps_the_host_map_and_evicts_the_least_recently_touched_host_when_none_is_idle()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        // Every one of MaxHosts distinct hosts is put into an active, long backoff — none of them is
        // idle, so once the map is at its cap the only tier that can supply a victim is "the
        // least-recently-touched host with nothing in flight". That is the shape the bound has to cover: a
        // torrent naming MaxHosts-plus distinct hostnames, all mid-throttle at once.
        for (var i = 0; i < CoverRateLimiter.MaxHosts; i++)
        {
            limiter.RecordFailure(HostNamed(i), TimeSpan.FromMinutes(30));

            // A tick each, purely to give every host a distinct, orderable LastTouch — negligible
            // against the 30-minute backoff being asserted below.
            clock.Advance(TimeSpan.FromTicks(1));
        }

        // One more distinct host pushes the map over its cap.
        limiter.RecordFailure("overflow.example.invalid", TimeSpan.FromMinutes(30));

        // Host 1 is checked first, and deliberately before host 0 below: host 1 was already tracked,
        // so asking for it is a lookup and touches nothing new, whereas host 0 was just evicted and
        // asking for it recreates its entry — itself a fresh addition that would legitimately evict
        // some *other* host to stay at the cap. Checking in this order keeps that second, unrelated
        // eviction from landing on the host this assertion cares about.
        //
        // Host 1 is still tracked and still owes most of its 30-minute wait — the cap did not evict
        // it just because the map happened to be full.
        var refused = await Assert.ThrowsAsync<CoverThrottledException>(() => limiter.AcquireAsync(HostNamed(1)));
        Assert.True(refused.RetryAfter > TimeSpan.FromMinutes(29));

        // Host 0 was the least-recently-touched tracked host at the moment the overflow host pushed
        // the map over its cap, so it is the one that was sacrificed then — and losing its state
        // means losing its 30-minute backoff with it. A fresh AcquireAsync on it succeeds immediately
        // rather than waiting out what it was told.
        (await limiter.AcquireAsync(HostNamed(0))).Dispose();
        Assert.Empty(clock.Waits);
    }

    [Fact]
    public async Task Evicts_an_idle_host_before_touching_one_that_is_still_mid_backoff()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        // The one host this test is protecting: a live 30-minute backoff, touched before any of the
        // filler hosts below.
        limiter.RecordFailure(Host, TimeSpan.FromMinutes(30));

        // Fill the rest of the cap with hosts that recovered — failed once, then closed via
        // RecordSuccess, which is exactly what a blip followed by a working request looks like. Not
        // one of them ever calls AcquireAsync, so none of them ever spends a token: they are back to
        // a full, untouched bucket, indistinguishable from a host never seen at all.
        for (var i = 0; i < CoverRateLimiter.MaxHosts - 1; i++)
        {
            var idleHost = HostNamed(i);
            limiter.RecordFailure(idleHost);
            limiter.RecordSuccess(idleHost);
        }

        // One more distinct host pushes the map to MaxHosts + 1.
        limiter.RecordFailure("overflow.example.invalid", TimeSpan.FromMinutes(30));

        // Host is the oldest-touched entry overall — a plain LRU with no idle preference would have
        // picked it. Instead an idle filler paid for the new arrival, because the bound requires a host
        // that is actually being paced to never lose that state while an idle one is available to
        // sacrifice for free.
        var refused = await Assert.ThrowsAsync<CoverThrottledException>(() => limiter.AcquireAsync(Host));
        Assert.True(refused.RetryAfter > TimeSpan.FromMinutes(29));
    }

    [Fact]
    public async Task Never_evicts_a_host_with_a_request_currently_in_flight()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        // Hold the one concurrency slot on Host, exactly like a real fetch in progress.
        var held = await limiter.AcquireAsync(Host);

        // Fill the rest of the cap with idle hosts, so the idle tier always has a victim and never
        // has to look past Host.
        for (var i = 0; i < CoverRateLimiter.MaxHosts - 1; i++)
        {
            var idleHost = HostNamed(i);
            limiter.RecordFailure(idleHost);
            limiter.RecordSuccess(idleHost);
        }

        // One more distinct host pushes the map over its cap while Host's request is still in flight.
        limiter.RecordFailure("overflow.example.invalid", TimeSpan.FromMinutes(30));

        // If Host's state had been evicted and silently recreated, a second AcquireAsync would see a
        // fresh gate and return immediately — a second request in flight on a host whose
        // MaxConcurrentPerHost is 1. Instead it has to queue behind the request already holding the
        // slot, which proves the original HostState, and therefore its semaphore, survived.
        var queued = limiter.AcquireAsync(Host);
        Assert.False(queued.IsCompleted);

        held.Dispose();
        (await queued).Dispose();
    }

    // ---------------------------------------------------------------------
    // The handler, which is what feeds the limiter
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Reads_retry_after_off_a_429_and_makes_the_next_request_wait_for_it()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));

        await SendAsync(limiter, response);
        (await limiter.AcquireAsync(Host)).Dispose();

        Assert.Equal([TimeSpan.FromSeconds(7)], clock.Waits);
    }

    [Fact]
    public async Task Backs_off_from_a_503_that_names_no_delay()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        await SendAsync(limiter, new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        (await limiter.AcquireAsync(Host)).Dispose();

        Assert.Equal([CoverRateLimiter.MinimumInterval], clock.Waits);
    }

    [Fact]
    public async Task Counts_an_ordinary_failure_toward_the_breaker()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        // A 404 carries no Retry-After and is not a "slow down" — but five dead covers in a row still
        // says something about the host, so it counts.
        for (var i = 0; i < CoverRateLimiter.BreakerThreshold; i++)
        {
            await SendAsync(limiter, new HttpResponseMessage(HttpStatusCode.NotFound));
            clock.Advance(CoverRateLimiter.MaxBackoff);
        }

        var refused = await Assert.ThrowsAsync<CoverThrottledException>(() => limiter.AcquireAsync(Host));
        Assert.Contains("in a row", refused.Reason);
    }

    [Fact]
    public async Task Clears_the_failure_history_when_a_request_succeeds()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        // Enough failures to trip the breaker, but never five *in a row*. That word is the whole
        // promise: a host that fails one cover in ten all evening is not a host to stop talking to,
        // and a run over a mixed folder hits plenty of dead covers without the host being unwell.
        for (var i = 0; i < CoverRateLimiter.BreakerThreshold; i++)
        {
            await SendAsync(limiter, new HttpResponseMessage(HttpStatusCode.NotFound));
            clock.Advance(CoverRateLimiter.MaxBackoff);
            await SendAsync(limiter, new HttpResponseMessage(HttpStatusCode.OK));
        }

        // Asserted as "does not throw" rather than on the wait, because the wait is the same either
        // way once the backoff deadline has passed — the count is what a lost success corrupts.
        (await limiter.AcquireAsync(Host)).Dispose();
    }

    // ---------------------------------------------------------------------
    // The permit's lifetime: held through the whole transfer, not just to headers
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Holds_the_permit_until_the_body_is_actually_read()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);
        var gated = new GatedStream("cover-bytes"u8.ToArray());

        using var handler = new CoverRateLimitHandler(limiter)
        {
            InnerHandler = new SequencedInner(
                () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(gated) },
                () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }),
        };
        using var invoker = new HttpMessageInvoker(handler);

        using var firstResponse = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/cover.jpg"), default);

        // Headers are back, but nothing has read a single byte of the body yet. Under the bug this
        // fixes, the permit was already released at exactly this point — SendAsync returning is where
        // the old `using var permit` expired. MaxConcurrentPerHost is 1, so a second request queued
        // here has nothing else that could be holding it back.
        var second = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/cover.jpg"), default);
        Assert.False(second.IsCompleted);

        gated.Release();
        await DrainAsync(firstResponse.Content);

        // Only once the first body is fully drained does the second request's own AcquireAsync unblock.
        using var secondResponse = await second;
    }

    [Fact]
    public async Task A_200_whose_body_then_fails_still_counts_toward_the_breaker()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        using var handler = new CoverRateLimitHandler(limiter)
        {
            InnerHandler = new SequencedInner(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FailingBodyStream("partial"u8.ToArray())),
            }),
        };
        using var invoker = new HttpMessageInvoker(handler);

        for (var i = 0; i < CoverRateLimiter.BreakerThreshold; i++)
        {
            using var response = await invoker.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/cover.jpg"), default);

            // Headers say 200. Under the bug this fixes, that alone was enough to call RecordSuccess
            // and clear every earlier failure — the real outcome only shows up once the body is read,
            // and here it is an I/O failure partway through.
            await Assert.ThrowsAsync<IOException>(() => DrainAsync(response.Content));
            clock.Advance(CoverRateLimiter.MaxBackoff);
        }

        // Five transfers that each answered 200 and then broke mid-body is five failures in a row, not
        // zero — the breaker trips exactly as it would for five ordinary 404s
        // (Counts_an_ordinary_failure_toward_the_breaker). Before the fix, RecordSuccess firing at
        // headers on every one of these meant ConsecutiveFailures never left zero and this breaker
        // could never trip at all.
        var refused = await Assert.ThrowsAsync<CoverThrottledException>(() => limiter.AcquireAsync(Host));
        Assert.Contains("in a row", refused.Reason);
    }

    [Fact]
    public async Task Disposing_the_response_without_reading_the_body_still_releases_the_permit()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        using var handler = new CoverRateLimitHandler(limiter)
        {
            InnerHandler = new SequencedInner(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GatedStream("cover"u8.ToArray())),
            }),
        };
        using var invoker = new HttpMessageInvoker(handler);

        var first = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/cover.jpg"), default);
        first.Dispose(); // Never read a single byte — the caller simply abandoned it.

        // A leaked permit here blocks this host forever: MaxConcurrentPerHost is 1 and the semaphore
        // never refills on its own. Guarded by a real timeout rather than trusting the suite to hang
        // visibly if this regresses.
        var secondTask = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/cover.jpg"), default);
        var winner = await Task.WhenAny(secondTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(secondTask, winner);
        using var second = await secondTask;
    }

    [Fact]
    public async Task An_exception_while_reading_the_body_still_releases_the_permit()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        using var handler = new CoverRateLimitHandler(limiter)
        {
            InnerHandler = new SequencedInner(
                () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingBodyStream("partial"u8.ToArray())) },
                () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }),
        };
        using var invoker = new HttpMessageInvoker(handler);

        using (var first = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/cover.jpg"), default))
        {
            await Assert.ThrowsAsync<IOException>(() => DrainAsync(first.Content));
        }

        var secondTask = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/cover.jpg"), default);
        var winner = await Task.WhenAny(secondTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(secondTask, winner);
        using var second = await secondTask;
    }

    [Fact]
    public async Task A_three_hop_redirect_chain_completes_without_deadlocking()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        using var handler = new CoverRateLimitHandler(limiter) { InnerHandler = new RedirectChainInner(hops: 3) };
        using var invoker = new HttpMessageInvoker(handler);

        // Each hop acquires its own fresh permit, and the previous hop's response — and with it its
        // permit — is disposed at the end of that loop iteration before the next hop's AcquireAsync
        // ever runs (FollowRedirectsAsync mirrors CoverFetcher's own `using`-in-loop shape for exactly
        // this reason). MaxConcurrentPerHost is 1: if a hop's permit were still held while the next hop
        // asked for one, this would deadlock rather than finish, which is why the assertion is guarded
        // by a real timeout instead of trusting an infinite wait to fail loudly.
        var chain = FollowRedirectsAsync(invoker, maxHops: 3);
        var winner = await Task.WhenAny(chain, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(chain, winner);

        Assert.Equal(3, await chain);
    }

    // ---------------------------------------------------------------------
    // Redirect hops: neutral to the breaker, neither a failure nor a success
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Redirect_hops_do_not_open_the_breaker(HttpStatusCode redirectStatus)
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        // Five hops of a status the cover fetcher actually follows by hand — the exact shape the defect
        // describes: five requests that are one well-behaved redirecting cover apiece, not five
        // problems. Before the fix each one counted as an ordinary failure and tripped the breaker.
        // (The ordinary token-bucket pacing still applies to each hop — this is only asserting that
        // none of them was recorded as a failure, not that they were free.)
        for (var i = 0; i < CoverRateLimiter.BreakerThreshold; i++)
            await SendAsync(limiter, new HttpResponseMessage(redirectStatus));

        // Still open for business: no failure was ever recorded, so there is nothing to back off from
        // and no breaker to trip. If any hop had counted, five of them would already be enough to open
        // it outright.
        (await limiter.AcquireAsync(Host)).Dispose();
    }

    [Fact]
    public async Task A_3xx_the_fetcher_does_not_follow_still_counts_as_an_ordinary_failure()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        // 304 is not reachable from a cover GET (no conditional headers are ever sent) and is not one
        // of the statuses the fetcher follows by hand — it stops there without the cover it asked for,
        // so treating it as neutral would hide a real run of failures behind the same "it's just a
        // 3xx" reasoning at issue, for the statuses that *are* followed.
        for (var i = 0; i < CoverRateLimiter.BreakerThreshold; i++)
        {
            await SendAsync(limiter, new HttpResponseMessage(HttpStatusCode.NotModified));
            clock.Advance(CoverRateLimiter.MaxBackoff);
        }

        var refused = await Assert.ThrowsAsync<CoverThrottledException>(() => limiter.AcquireAsync(Host));
        Assert.Contains("in a row", refused.Reason);
    }

    [Fact]
    public async Task A_redirect_hop_in_the_half_open_trial_does_not_close_the_breaker()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);

        // Same setup as Tries_one_request_again_after_the_cooldown_and_closes_on_success /
        // Re_opens_the_breaker_when_the_trial_request_fails_too — open the breaker, then let the
        // cooldown pass so the gate allows a single half-open trial through.
        for (var i = 0; i < CoverRateLimiter.BreakerThreshold; i++)
            limiter.RecordFailure(Host);

        clock.Advance(CoverRateLimiter.BreakerCooldown + TimeSpan.FromSeconds(1));

        // The trial comes back a redirect. A 3xx proves the host answered, not that the request it was
        // actually asked for succeeded — treating it as a success here would clear the failure history
        // outright (RecordSuccess does), on nothing more than reachability.
        await SendAsync(limiter, new HttpResponseMessage(HttpStatusCode.Found));

        limiter.RecordFailure(Host);

        // Still failing "in a row": the redirect was neutral, so this is the sixth failure of one run
        // rather than the first of a new one, and the breaker trips again immediately — no second
        // trial, no waiting out a further cooldown. Had the redirect been read as a success, this
        // single failure would not have reached the threshold and the call below would succeed.
        var refused = await Assert.ThrowsAsync<CoverThrottledException>(() => limiter.AcquireAsync(Host));
        Assert.Contains("in a row", refused.Reason);
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads a body exactly the way <see cref="CoverFetcher"/> does: <c>ReadAsStreamAsync</c> then a
    /// manual read loop, rather than a convenience method like <c>ReadAsByteArrayAsync</c>. That choice
    /// is load-bearing for these tests, not stylistic — <c>ReadAsByteArrayAsync</c> buffers through
    /// <c>HttpContent.LoadIntoBufferAsync</c>, which catches whatever <c>SerializeToStreamAsync</c>
    /// throws and rethrows it wrapped in an <see cref="HttpRequestException"/>. CoverFetcher never goes
    /// through that path, so a test asserting <see cref="IOException"/> straight off a failing read has
    /// to avoid it too, or it would be pinning .NET's wrapping behaviour instead of this handler's.
    /// </summary>
    private static async Task DrainAsync(HttpContent content)
    {
        await using var source = await content.ReadAsStreamAsync();
        var buffer = new byte[8192];
        while (await source.ReadAsync(buffer) > 0)
        {
        }
    }

    private static async Task SendAsync(CoverRateLimiter limiter, HttpResponseMessage response)
    {
        using var handler = new CoverRateLimitHandler(limiter) { InnerHandler = new StubInner(response) };
        using var invoker = new HttpMessageInvoker(handler);
        using var sent = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/cover.jpg"), default);

        // Mirrors what CoverFetcher actually does with a 2xx: reads the body to the end. A 2xx's
        // success is no longer known at headers, so a helper that disposed `sent` without ever reading
        // it would never see RecordSuccess fire — that used to be the bug, and a helper reproducing it
        // by omission would just hide the fix behind a test that no longer exercises it. Draining a
        // non-2xx/redirect response's body is harmless and realistic too: CoverFetcher never reads
        // those bodies either, so nothing here changes what those statuses record.
        await DrainAsync(sent.Content);
    }

    private sealed class StubInner(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(response);
    }

    /// <summary>
    /// Follows redirects the same shape <see cref="CoverFetcher"/> does — each hop's response disposed
    /// by a <c>using</c> declaration scoped to a single loop iteration — without pulling in the
    /// allowlist, content-type or blob-store concerns that live in that class and are out of scope for
    /// the handler/limiter contract this file is about.
    /// </summary>
    private static async Task<int> FollowRedirectsAsync(HttpMessageInvoker invoker, int maxHops)
    {
        var uri = new Uri($"https://{Host}/hop0");
        for (var hop = 0; hop <= maxHops; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await invoker.SendAsync(request, default);

            if (response.StatusCode == HttpStatusCode.Found)
            {
                uri = response.Headers.Location!;
                continue;
            }

            await DrainAsync(response.Content);
            return hop;
        }

        throw new InvalidOperationException("Too many redirects — the chain never resolved.");
    }

    /// <summary>Returns each factory's response in turn, repeating the last one once the list runs out.</summary>
    private sealed class SequencedInner(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var factory = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return Task.FromResult(factory());
        }
    }

    /// <summary>Redirects on the first <paramref name="hops"/> calls, then answers 200.</summary>
    private sealed class RedirectChainInner(int hops) : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _calls++;
            if (_calls <= hops)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri($"https://{Host}/hop{_calls}") },
                };
                return Task.FromResult(redirect);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("cover") });
        }
    }

    /// <summary>
    /// A response body whose first read blocks until <see cref="Release"/> is called — the way a real
    /// slow upstream transfer would, without a test actually waiting on one. Proves the permit is held
    /// across the read rather than just up to the point <c>SendAsync</c> returns: a second request
    /// queued behind this one has nothing but the gate to be stuck on.
    /// </summary>
    private sealed class GatedStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _inner = new(payload);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var read = await ReadAsync(buffer.AsMemory(offset, count), ct);
            return read;
        }

        // Overridden explicitly rather than relied on via the byte[] overload above: Stream's default
        // CopyToAsync (what SerializeToStreamAsync drives this through) calls this Memory<byte> overload
        // directly, and leaving it to the base class's own array-backed forwarding would make the gate
        // depend on BCL plumbing this test has no business assuming.
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await _gate.Task;
            return await _inner.ReadAsync(buffer, ct);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A response body that hands back <paramref name="firstChunk"/> and then throws — a connection
    /// dropping partway through a transfer, which is the shape <see cref="CoverRateLimitHandler"/> must
    /// record as a failure rather than as the success its 200 status line promised.
    /// </summary>
    private sealed class FailingBodyStream(byte[] firstChunk) : Stream
    {
        private bool _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Task.FromResult(ReadCore(buffer.AsSpan(offset, count)));

        // See GatedStream's override of the same overload: Stream's default CopyToAsync — what
        // SerializeToStreamAsync drives this through — reads via this Memory<byte> overload directly,
        // so the throw has to live here rather than only on the byte[] overload above.
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromResult(ReadCore(buffer.Span));

        private int ReadCore(Span<byte> destination)
        {
            if (!_served)
            {
                _served = true;
                var n = Math.Min(destination.Length, firstChunk.Length);
                firstChunk.AsSpan(0, n).CopyTo(destination);
                return n;
            }

            throw new IOException("Connection reset mid-transfer.");
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
