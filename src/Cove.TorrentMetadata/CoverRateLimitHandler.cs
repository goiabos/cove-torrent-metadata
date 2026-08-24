using System.Net;

namespace Cove.TorrentMetadata;

/// <summary>
/// Puts every cover request through <see cref="CoverRateLimiter"/> and feeds the outcome back to it.
///
/// On the handler chain rather than around the fetch for the same reason the User-Agent is: the
/// registration is one place and the fetch has two callers. It also means the redirect hops the
/// cover fetch follows by hand are each metered, which is correct — three hops is three requests,
/// and a tracker counting them will count three.
///
/// It does not retry. A 429 sets the host's deadline and the *next* request waits at the gate, which
/// keeps the waiting outside any single request's HTTP timeout and lets the cover cache remember the
/// failure so the rest of a batch does not queue up behind it. Retrying in place would spend the
/// caller's timeout budget on a wait that the following request has to repeat anyway.
///
/// <b>The permit outlives <see cref="SendAsync"/>.</b> <see cref="CoverFetcher"/> calls this client
/// with <c>HttpCompletionOption.ResponseHeadersRead</c> and streams the body itself, so a permit held
/// only for the duration of this method would be released at headers — before a single byte of the
/// image has been read. That is not "one request in flight per host": a preview and a bulk import
/// could hold the same host's slot at once, and a host that answers 200 and then stalls or drops mid
/// body would look like an instant success every time, so the breaker could never trip on it. Instead
/// the permit — and, for a 2xx, the success/failure call that rides with it — is handed to
/// <see cref="TrackingContent"/>, which wraps <see cref="HttpResponseMessage.Content"/> and settles
/// exactly once, on whichever of these actually happens first: the body is read to its end, a read
/// throws, or the content is disposed with neither having happened. <see cref="CoverFetcher"/>
/// disposes every hop's response with a <c>using</c> declaration that spans the whole redirect-loop
/// iteration — body read included — so hanging the release off the content's own disposal is what
/// makes "held for the whole upstream fetch" true rather than aspirational.
/// </summary>
public sealed class CoverRateLimitHandler(CoverRateLimiter limiter) : DelegatingHandler
{
    /// <summary>
    /// Per-request ceiling on how long this request may sit at the gate, defaulting to
    /// <see cref="CoverRateLimiter.MaxWait"/>.
    ///
    /// Carried on the request rather than configured on the client because both callers share one
    /// registration — the named client is where the User-Agent and this limiter are wired, and
    /// splitting it in two to vary one number would give the preview path its own chain to drift.
    /// </summary>
    public static readonly HttpRequestOptionsKey<TimeSpan> MaxWaitOption =
        new("io.github.goiabos.torrent-metadata.cover.max-wait");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var host = request.RequestUri?.Host ?? string.Empty;
        var maxWait = request.Options.TryGetValue(MaxWaitOption, out var bounded)
            ? bounded
            : CoverRateLimiter.MaxWait;

        // Not a `using` any more — every path below either hands the permit to a TrackingContent that
        // outlives this method, or (no response was ever obtained) releases it here directly. There is
        // no path that falls through without one or the other owning it.
        var permit = await limiter.AcquireAsync(host, maxWait, ct);
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct);
        }
        catch
        {
            // No response exists in this branch, so there is no content whose disposal could release
            // the permit later — this is the one place that still has to do it inline. Cancellation is
            // not counted as a host failure — that is our side giving up — but the permit is released
            // either way, exactly as it was when this lived in a `using`.
            permit.Dispose();
            if (!ct.IsCancellationRequested)
                limiter.RecordFailure(host);
            throw;
        }

        if (IsBackoffSignal(response.StatusCode))
        {
            // Decided at headers, same as always — a 429/503 carries no body worth reading before
            // acting on it. TrackingContent still owns releasing the permit rather than doing it here
            // inline, so a caller that *does* read this response's body (none does today, but nothing
            // stops one) cannot make it settle twice.
            limiter.RecordFailure(host, RetryAfter(response));
            response.Content = new TrackingContent(response.Content, host, permit, accountingLimiter: null);
        }
        else if (CoverFetcher.IsRedirect(response.StatusCode))
        {
            // Neither RecordFailure nor RecordSuccess. Every hop the cover fetcher follows by
            // hand passes through this handler as its own request, so counting a 3xx as a failure
            // opened the breaker on a well-behaved host after five ordinary redirecting covers — five
            // requests, not five problems.
            //
            // Not counted as a success either. The fetcher caps itself at three hops, but a redirect
            // loop is still a run of requests answering "elsewhere" rather than "here is your cover" —
            // treating each hop as a success would let it silently close a breaker that a genuine run
            // of failures had opened, on nothing more than the host being reachable. A 3xx proves the
            // host is answering; it does not prove the request the caller actually wanted succeeded,
            // which is the thing RecordSuccess is supposed to mean.
            //
            // Asked of CoverFetcher rather than answered here, so the two cannot disagree about which
            // statuses those are. Deliberately narrower than "any 3xx": a 304, or any 3xx the fetcher
            // does not follow, leaves it without the cover it asked for, so that response falls
            // through to the ordinary accounting below as the failure it is.
            //
            // The permit is released the same as any other response — off this hop's own content being
            // disposed, at the end of the redirect loop's current iteration — which is what keeps hop N
            // gone before hop N+1 asks for a fresh one (see the class doc).
            response.Content = new TrackingContent(response.Content, host, permit, accountingLimiter: null);
        }
        else if (!response.IsSuccessStatusCode)
        {
            limiter.RecordFailure(host);
            response.Content = new TrackingContent(response.Content, host, permit, accountingLimiter: null);
        }
        else
        {
            // The one branch where the outcome is not yet known at headers: a 200 has promised a body,
            // not delivered one. RecordSuccess now would be exactly the bug this class doc describes —
            // it would fire before the transfer that could still fail. TrackingContent defers it to
            // whichever settles first: EOF (success), a read throwing (failure), or disposal with
            // neither (the caller abandoned it, or the fetcher bailed out early on an over-size body —
            // neither is evidence the *host* did anything wrong, so neither is recorded).
            response.Content = new TrackingContent(response.Content, host, permit, accountingLimiter: limiter);
        }

        return response;
    }

    /// <summary>
    /// The two statuses that mean "slow down" rather than "this is wrong".
    ///
    /// A 404 cover is a failure too and still counts toward the breaker — five dead covers in a row
    /// says something about the host — but only these two carry a <c>Retry-After</c> worth obeying.
    /// </summary>
    private static bool IsBackoffSignal(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    /// <summary>
    /// <c>Retry-After</c> as a delay, in either of the forms RFC 9110 allows.
    ///
    /// A past date reads as zero rather than as a negative delay, so a clock skewed the wrong way
    /// cannot produce a deadline in the past that the gate treats as "go now, repeatedly".
    /// </summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
            return null;

        if (header.Delta is { } delta)
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;

        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Wraps a response's <see cref="HttpContent"/> so the per-host permit it was acquired under is
    /// released when the body is actually done with, not when <see cref="SendAsync"/> returns — and,
    /// when <c>accountingLimiter</c> is supplied, so the success/failure call that
    /// belongs with that release rides along with it instead of firing early.
    ///
    /// <c>accountingLimiter</c> is null for every status <see cref="SendAsync"/> can
    /// already judge from headers alone (a backoff signal, a redirect, any other non-2xx): the outcome
    /// is recorded before this content is even constructed, and passing null here just stops a second,
    /// contradictory call from reaching the limiter if something unusual reads this response's body
    /// anyway — a settle would otherwise call <see cref="CoverRateLimiter.RecordSuccess"/> on a 404
    /// that happened to be read to the end. It is non-null only for the one case where the headers do
    /// not yet say what happened: a 2xx, whose body is the thing being promised.
    ///
    /// Every read path funnels through <see cref="TrackingStream"/> — <c>ReadAsStreamAsync</c> via
    /// <see cref="CreateContentReadStreamAsync()"/>, and <c>ReadAsByteArrayAsync</c>/
    /// <c>ReadAsStringAsync</c>/<c>CopyToAsync</c> via <see cref="SerializeToStreamAsync(Stream,TransportContext?,CancellationToken)"/>,
    /// which is implemented here in terms of the same read stream rather than a second copy of the
    /// tracking logic — so whichever convenience method a caller reaches for sees the same accounting.
    ///
    /// One outcome, ever. <see cref="TrySettle"/> guards every exit with an <see cref="Interlocked"/>
    /// flag, the same idiom <see cref="CoverRateLimiter"/>'s own permit already uses, so a read that
    /// throws after the stream already hit EOF (impossible in practice, but nothing here relies on
    /// that) — or a Dispose racing a still-in-flight read — settles once and once only.
    /// </summary>
    private sealed class TrackingContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly string _host;
        private readonly IDisposable _permit;
        private readonly CoverRateLimiter? _accountingLimiter;
        private int _settled;

        public TrackingContent(HttpContent inner, string host, IDisposable permit, CoverRateLimiter? accountingLimiter)
        {
            _inner = inner;
            _host = host;
            _permit = permit;
            _accountingLimiter = accountingLimiter;

            // Content-Type and Content-Length in particular: CoverFetcher reads both off the response
            // it gets back, and they were already on the wire at headers time regardless of which
            // HttpCompletionOption the caller asked for.
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        private bool TrySettle() => Interlocked.Exchange(ref _settled, 1) == 0;

        /// <summary>The body was read to its end. A no-op for every status decided at headers.</summary>
        private void MarkSuccess()
        {
            if (TrySettle())
            {
                _accountingLimiter?.RecordSuccess(_host);
                _permit.Dispose();
            }
        }

        /// <summary>A read threw. A no-op for every status decided at headers.</summary>
        private void MarkFailure()
        {
            if (TrySettle())
            {
                _accountingLimiter?.RecordFailure(_host);
                _permit.Dispose();
            }
        }

        /// <summary>
        /// Reached only when disposal got here first — nobody read to EOF and nothing threw. That is
        /// either a status already decided at headers (the ordinary case: CoverFetcher never reads a
        /// redirect's or an error's body at all), or a 2xx the caller abandoned, or the one the fetcher
        /// bails out of early itself: an over-size body, refused mid-stream before EOF. None of those
        /// is evidence the transfer would have failed, so nothing is recorded — only the permit is
        /// released, exactly as every other exit does.
        /// </summary>
        private void ReleaseOnly()
        {
            if (TrySettle())
                _permit.Dispose();
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) =>
            new TrackingStream(await _inner.ReadAsStreamAsync(ct).ConfigureAwait(false), this);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var source = await CreateContentReadStreamAsync(ct).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                // Stream.CopyToAsync reads in a loop via ReadAsync, which is exactly what TrackingStream
                // overrides — so ReadAsByteArrayAsync/ReadAsStringAsync/CopyToAsync (all of which funnel
                // through this method) settle the same way ReadAsStreamAsync's caller does, through one
                // code path rather than a second copy of the EOF/exception logic.
                await source.CopyToAsync(stream, ct).ConfigureAwait(false);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseOnly();
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// The stream <see cref="CreateContentReadStreamAsync()"/> hands out. It does not settle on its
        /// own disposal — <see cref="TrackingContent.Dispose"/> is the single fallback for "nobody
        /// finished reading", and settling here too would just be a second, redundant guard around the
        /// same <see cref="Interlocked"/> flag. What it owns is noticing the two events that *do* carry
        /// information: reaching the end (<see cref="MarkSuccess"/>) and a read throwing
        /// (<see cref="MarkFailure"/>), each caught right where it happens rather than reconstructed
        /// from the outside afterwards.
        /// </summary>
        private sealed class TrackingStream(Stream inner, TrackingContent owner) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                try
                {
                    var read = inner.Read(buffer, offset, count);
                    if (read == 0)
                        owner.MarkSuccess();
                    return read;
                }
                catch
                {
                    owner.MarkFailure();
                    throw;
                }
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                try
                {
                    var read = await inner.ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
                    if (read == 0)
                        owner.MarkSuccess();
                    return read;
                }
                catch
                {
                    owner.MarkFailure();
                    throw;
                }
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                try
                {
                    var read = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                        owner.MarkSuccess();
                    return read;
                }
                catch
                {
                    owner.MarkFailure();
                    throw;
                }
            }

            public override void Flush() => inner.Flush();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                // Deliberately no call into owner here — see the class doc. TrackingContent.Dispose,
                // which always runs afterwards (CoverFetcher disposes this stream before the response
                // that owns it, per the reverse-declaration-order `using` rule), is the single place
                // that settles an outcome nobody read to.
                if (disposing)
                    inner.Dispose();

                base.Dispose(disposing);
            }
        }
    }
}
