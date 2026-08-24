using System.Collections.Concurrent;

namespace Cove.TorrentMetadata;

/// <summary>Raised when a request must not be sent at all, rather than merely waited for.</summary>
public sealed class CoverThrottledException(string reason, TimeSpan retryAfter) : Exception(reason)
{
    /// <summary>Phrased for the reviewer, so it can be reported instead of a generic failure.</summary>
    public string Reason { get; } = reason;

    /// <summary>
    /// How long the caller should leave it before asking again.
    ///
    /// Carried on the exception rather than recomputed by whoever catches it: the throw site is the
    /// only place that knows *which* limit bit — a breaker cooldown, a backoff deadline, or a
    /// contended concurrency slot are three different answers. The preview proxy turns this into a
    /// <c>Retry-After</c> header, which is the only way the browser can be told to come back rather
    /// than treating a paced cover as a broken image.
    /// </summary>
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Paces cover fetches so the load looks like a person browsing rather than a script.
///
/// The last of the three measures the tracker's staff conditioned clearance on, and the one
/// with an explicit acceptance criterion attached: "as long as the load is similar to a regular user
/// browsing the site, we see no issue with this". The numbers below are the answer to that sentence,
/// and they are quoted verbatim in the reply to staff — changing one changes a promise, not
/// just a constant.
///
/// Per host, not global. A third-party image host and the tracker's own host have nothing to do with
/// each other, and a global budget would make a slow image host throttle requests to the tracker.
///
/// Hand-rolled rather than Polly: <c>scripts/package.sh</c> refuses to ship anything the host does
/// not already provide, and a token bucket over a clock is less code than the packaging exception
/// would be.
/// </summary>
public sealed class CoverRateLimiter(
    TimeProvider? time = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>Minimum spacing between requests to one host, once the burst is spent.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Requests allowed back-to-back before the interval starts biting. Three because opening a page
    /// fetches a handful of images at once — refusing to do that would be *less* like a browser, not
    /// more.
    /// </summary>
    public const int Burst = 3;

    /// <summary>One in flight per host. A browser pipelines; a batch importer has no reason to.</summary>
    public const int MaxConcurrentPerHost = 1;

    /// <summary>Consecutive failures on one host before it is left alone entirely.</summary>
    public const int BreakerThreshold = 5;

    /// <summary>How long the breaker stays open before a single trial request is allowed through.</summary>
    public static readonly TimeSpan BreakerCooldown = TimeSpan.FromSeconds(60);

    /// <summary>Ceiling on exponential backoff, so one bad night cannot park a host for an hour.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest this will hold a request waiting rather than refusing it.
    ///
    /// Bounded because the wait is spent inside the caller's HTTP timeout: a gate that waited out a
    /// three-minute <c>Retry-After</c> would be cancelled by the client and would have achieved
    /// nothing except a slow failure. Refusing without sending is the more polite outcome anyway, and
    /// the cover cache remembers it so the rest of the batch does not queue up behind it either.
    /// </summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The ceiling a *preview* request waits under, rather than <see cref="MaxWait"/>.
    ///
    /// The number predates the current cover queue and survives it, but its reason moved. It used to be about the
    /// browser: covers were fetched by <c>&lt;img&gt;</c> tags, and a browser allows around six
    /// connections per origin, so an image parked for twenty seconds starved the extension's own API
    /// calls. Covers now arrive through the client's serial page-wide queue, one request at a time —
    /// so what a parked request stalls today is every cover behind it in that line, on a page the
    /// user is looking at. A preview that cannot go promptly is better refused than held: the
    /// refusal is answered from this side without a request, and the retry it schedules with its
    /// <c>Retry-After</c> is served by the cache the finished fetch has warmed by then, or goes out
    /// as the same single request that waiting would have sent. So this still weakens no promise
    /// made to the tracker — the numbers above bound how fast requests go out, not how long one may
    /// sit in a queue.
    ///
    /// It bounds *queueing* only: time at the gate, and time spent behind another caller's in-flight
    /// fetch of the same URL. The caller that starts a fetch waits its own transfer out regardless
    /// — reusing this as a ceiling on the transfer refused every cover slower than two
    /// seconds, which a 5 MB animated GIF ordinarily is.
    /// </summary>
    public static readonly TimeSpan PreviewMaxWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Upper bound on how many distinct hosts are tracked at once.
    ///
    /// A new bound, not one of the numbers quoted to staff above — those govern the *rate* to one
    /// host, this governs how many hosts are remembered at all. Subdomains are opt-in via a
    /// `*.host` allowlist entry rather than every subdomain being admitted automatically, but
    /// a wildcard entry still lets a single torrent's cover URLs name an unbounded number of
    /// distinct hostnames under it, and each one gets its own <see cref="HostState"/>. A real
    /// allowlist names a handful of trackers and image CDNs; this sits far above any honest count so
    /// it only bites a torrent engineered to mint hostnames.
    /// </summary>
    public const int MaxHosts = 1_000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly ConcurrentDictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Waits until a request to <paramref name="host"/> may be sent, and returns the permit that
    /// releases its concurrency slot.
    ///
    /// Throws <see cref="CoverThrottledException"/> rather than waiting when the breaker is open or
    /// the wait would exceed <see cref="MaxWait"/> — those are cases where sending later is better
    /// than sending slowly, and the caller has a reason worth showing the user.
    /// </summary>
    public Task<IDisposable> AcquireAsync(string host, CancellationToken ct = default) =>
        AcquireAsync(host, MaxWait, ct);

    /// <summary>
    /// The same gate under a caller-chosen ceiling, for a caller that cannot afford
    /// <see cref="MaxWait"/> — see <see cref="PreviewMaxWait"/>.
    ///
    /// An overload rather than a smaller <see cref="MaxWait"/>: the twenty seconds are what an import
    /// is willing to spend to *not* lose a cover the user ticked, and a preview refusing early must
    /// not shorten that. Both the concurrency slot and the token bucket are bounded by it, because
    /// one request in flight per host means a queued preview waits behind whatever the importer is
    /// doing, and an unbounded wait there would put the ceiling back.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string host, TimeSpan maxWait, CancellationToken ct = default)
    {
        var state = GetOrAddHost(host, _time.GetUtcNow());

        // One deadline for the whole gate, not one per step. The semaphore wait below and
        // every sleep in the token-bucket loop after it all draw down the same allowance, so what
        // maxWait bounds is their sum, not just whichever one is largest. Derived from _time rather
        // than DateTimeOffset.UtcNow so a test's FakeClock drives it exactly like everything else here.
        var deadline = _time.GetUtcNow() + maxWait;

        // WaitAsync's own timeout rather than a raced delay: it either takes the slot or does not,
        // with no window in which a slot is acquired after the caller has given up on it and is
        // never released. Given the budget remaining under the deadline rather than maxWait again —
        // the two happen to be equal here, since nothing has spent any of the budget yet, but the
        // deadline is what stays authoritative as the gate is crossed.
        var semaphoreBudget = deadline - _time.GetUtcNow();
        if (semaphoreBudget < TimeSpan.Zero)
            semaphoreBudget = TimeSpan.Zero;

        if (!await state.Gate.WaitAsync(semaphoreBudget, ct))
        {
            throw new CoverThrottledException(
                $"Cover skipped: a request to {host} is already in flight and did not finish in "
                + $"{Math.Round(maxWait.TotalSeconds)}s.",
                MinimumInterval);
        }

        try
        {
            while (true)
            {
                TimeSpan wait;
                DateTimeOffset now;
                lock (state)
                {
                    now = _time.GetUtcNow();

                    // The breaker is checked before the bucket: an open breaker means "do not send",
                    // and waiting for a token first would only delay saying so.
                    if (state.ConsecutiveFailures >= BreakerThreshold && now < state.OpenUntil)
                    {
                        throw new CoverThrottledException(
                            $"Cover skipped: {host} has failed {state.ConsecutiveFailures} times in a row, "
                            + "so requests to it are paused for a minute.",
                            state.OpenUntil - now);
                    }

                    if (state.NotBefore > now)
                    {
                        wait = state.NotBefore - now;
                    }
                    else
                    {
                        Refill(state, now);
                        if (state.Tokens >= 1)
                        {
                            state.Tokens -= 1;
                            return new Permit(state.Gate);
                        }

                        wait = MinimumInterval * (1 - state.Tokens);
                    }
                }

                // The budget still left under the one deadline set at entry, not maxWait measured
                // afresh. Time already spent queued at the semaphore above, or sleeping in an earlier
                // pass of this same loop, both count against it — that shrinking budget is what keeps
                // the *total* time at the gate bounded, rather than only ever the current sleep.
                var budget = deadline - now;
                if (wait > budget)
                {
                    // RetryAfter reports what the host actually asked for, not what was left of the
                    // caller's budget — a caller that comes back sooner than the host's own wait is
                    // exactly the behaviour the backoff and Retry-After handling exist to prevent.
                    throw new CoverThrottledException(
                        $"Cover skipped: {host} asked to be left alone for "
                        + $"{Math.Round(wait.TotalSeconds)}s, which is longer than this request can wait.",
                        wait);
                }

                await _delay(wait, ct);
            }
        }
        catch
        {
            // The permit is only handed out on the success path, so every other exit owns the release.
            state.Gate.Release();
            throw;
        }
    }

    /// <summary>Clears a host's failure history. A single success closes the breaker outright.</summary>
    public void RecordSuccess(string host)
    {
        if (!_hosts.TryGetValue(host, out var state))
            return;

        lock (state)
        {
            state.ConsecutiveFailures = 0;
            state.NotBefore = default;
            state.OpenUntil = default;
            state.LastTouch = _time.GetUtcNow();
        }
    }

    /// <summary>
    /// Records a failed request and how long to leave the host alone.
    ///
    /// <paramref name="retryAfter"/> is honoured as given when the server sent one — it is the host
    /// telling us what it wants, and second-guessing it is exactly the behaviour that gets an
    /// extension blocked. Without one the delay doubles per consecutive failure, which is the part
    /// that stops a host that is merely down from being polled at full rate all night.
    /// </summary>
    public void RecordFailure(string host, TimeSpan? retryAfter = null)
    {
        var now = _time.GetUtcNow();
        var state = GetOrAddHost(host, now);

        lock (state)
        {
            state.ConsecutiveFailures++;

            var backoff = retryAfter ?? Backoff(state.ConsecutiveFailures);
            state.NotBefore = now + backoff;

            if (state.ConsecutiveFailures >= BreakerThreshold)
                state.OpenUntil = now + BreakerCooldown;
        }
    }

    /// <summary>
    /// The host's state, creating it if this is the first time <paramref name="host"/> has been
    /// seen and, if that pushes <see cref="_hosts"/> over <see cref="MaxHosts"/>, evicting one other
    /// host to make room. See <see cref="EvictHost"/> for what makes a host safe to evict.
    /// </summary>
    private HostState GetOrAddHost(string host, DateTimeOffset now)
    {
        var state = _hosts.GetOrAdd(host, _ => new HostState(now));

        lock (state)
            state.LastTouch = now;

        if (_hosts.Count > MaxHosts)
            EvictHost(exclude: host, now);

        return state;
    }

    /// <summary>
    /// Picks one tracked host other than <paramref name="exclude"/> (the one just touched, which
    /// must never be its own victim) to drop, so the map does not grow past <see cref="MaxHosts"/>.
    ///
    /// Three tiers, tried in order, because not every eviction is equally free:
    ///
    /// <list type="number">
    /// <item>A host that is functionally identical to one never seen at all: a full token bucket, no
    /// backoff pending, breaker closed, nothing in flight. Dropping one of these hands out nothing —
    /// recreating the state on the next request produces exactly the state being discarded, so no
    /// pacing is bypassed. This is the only tier with no cost, and it is why eligibility is
    /// recomputed through <see cref="Refill"/> rather than read off possibly-stale fields: a host
    /// idle long enough to be back at a full bucket has to be judged by what it is *now*, not by
    /// what it was the last time it was touched.</item>
    /// <item>No idle host exists — every tracked host is either mid-backoff or breaker-open, which
    /// only happens when the map is entirely full of hosts actively being throttled at once, an
    /// adversarial shape rather than an honest workload. The fallback is the least-recently-touched
    /// host with nothing in flight, evicted anyway. This *does* grant a free pass: the evicted host
    /// comes back with a full burst and a closed breaker, up to <see cref="Burst"/> requests before
    /// it is paced again. That is deliberately a smaller concession than letting the map grow
    /// without bound while under exactly this kind of attack — bounded to one host's burst, not an
    /// unbounded number of hosts' state.</item>
    /// <item>Every tracked host, including the one just touched, has a request in flight right now.
    /// Nothing is evicted: <see cref="MaxConcurrentPerHost"/> is 1, so evicting a state whose
    /// <see cref="HostState.Gate"/> is held would let a second request onto that host once the
    /// caller re-creates a fresh state for it — a correctness bug, not a fairness one, so it is
    /// refused even at the cost of leaving <see cref="_hosts"/> over its cap this once. That can
    /// only happen while genuinely <see cref="MaxHosts"/> distinct hosts are concurrently mid-request,
    /// which is bounded by real outstanding work — nothing a single torrent can conjure by naming
    /// hosts, since naming one costs nothing until something actually starts fetching from it.</item>
    /// </list>
    /// </summary>
    private void EvictHost(string exclude, DateTimeOffset now)
    {
        if (TryEvictOne(exclude, now, requireIdle: true))
            return;

        TryEvictOne(exclude, now, requireIdle: false);
    }

    private bool TryEvictOne(string exclude, DateTimeOffset now, bool requireIdle)
    {
        string? victimKey = null;
        var oldestTouch = DateTimeOffset.MaxValue;

        foreach (var pair in _hosts)
        {
            if (pair.Key == exclude)
                continue;

            var state = pair.Value;
            lock (state)
            {
                // Never evict a state whose semaphore is currently held — see AcquireAsync/Permit.
                // A second request would be admitted onto the same host the instant a fresh
                // HostState is created for it, which MaxConcurrentPerHost=1 exists to prevent.
                if (state.Gate.CurrentCount != MaxConcurrentPerHost)
                    continue;

                if (requireIdle)
                {
                    Refill(state, now);
                    var idle = state.ConsecutiveFailures == 0
                        && state.OpenUntil <= now
                        && state.NotBefore <= now
                        && state.Tokens >= Burst;

                    if (!idle)
                        continue;
                }

                if (state.LastTouch < oldestTouch)
                {
                    oldestTouch = state.LastTouch;
                    victimKey = pair.Key;
                }
            }
        }

        if (victimKey is null)
            return false;

        // Re-checked immediately before removal, back-to-back with no await between the two: the
        // scan above is not atomic with this removal, so a concurrent AcquireAsync could in
        // principle take the gate in between. Narrowing the window to adjacent, non-yielding
        // statements is what this can do without restructuring AcquireAsync's own synchronisation,
        // which is out of scope for a map-bounding fix — and the residual window is a few CPU
        // instructions wide, not an await, so it is not something a crafted torrent can reliably hit.
        if (_hosts.TryGetValue(victimKey, out var victim))
        {
            lock (victim)
            {
                if (victim.Gate.CurrentCount != MaxConcurrentPerHost)
                    return false;

                _hosts.TryRemove(victimKey, out _);
                return true;
            }
        }

        return false;
    }

    /// <summary>Doubling from the minimum interval, capped. 1s, 2s, 4s, 8s, 16s, 30s, 30s…</summary>
    private static TimeSpan Backoff(int consecutiveFailures)
    {
        var doublings = Math.Min(consecutiveFailures - 1, 16);
        var backoff = MinimumInterval * Math.Pow(2, doublings);
        return backoff > MaxBackoff ? MaxBackoff : backoff;
    }

    /// <summary>
    /// Adds the tokens the elapsed time earned. Fractional on purpose: rounding down would make the
    /// effective interval longer than the number promised to staff, which is the wrong direction to
    /// be wrong in a number someone is holding us to.
    /// </summary>
    private static void Refill(HostState state, DateTimeOffset now)
    {
        var earned = (now - state.LastRefill).TotalSeconds / MinimumInterval.TotalSeconds;
        if (earned <= 0)
            return;

        state.Tokens = Math.Min(Burst, state.Tokens + earned);
        state.LastRefill = now;
    }

    private sealed class HostState(DateTimeOffset now)
    {
        public readonly SemaphoreSlim Gate = new(MaxConcurrentPerHost, MaxConcurrentPerHost);
        public double Tokens = Burst;
        public DateTimeOffset LastRefill = now;
        public int ConsecutiveFailures;

        /// <summary>Backoff or Retry-After deadline: wait for it.</summary>
        public DateTimeOffset NotBefore;

        /// <summary>Breaker deadline: refuse until it passes, then allow one trial through.</summary>
        public DateTimeOffset OpenUntil;

        /// <summary>
        /// When this host was last touched by <see cref="AcquireAsync(string,TimeSpan,CancellationToken)"/>,
        /// <see cref="RecordFailure"/> or <see cref="RecordSuccess"/> — the LRU signal
        /// <see cref="EvictHost"/> picks a victim by when <see cref="_hosts"/> is over its cap.
        /// </summary>
        public DateTimeOffset LastTouch = now;
    }

    private sealed class Permit(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
