using System.Net;
using Cove.Core.Interfaces;
using Cove.TorrentMetadata;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// The one cover-resolution sequence, and the two things only it can answer.
///
/// It exists as a type because the sequence used to be written twice — once in
/// <see cref="CoverProxyService"/> and once inside <see cref="TorrentApplyService"/> — and the copies
/// had drifted three ways before anyone put them side by side. Each read correctly alone, which is
/// what makes a second copy the defect rather than any one line in it.
///
/// Driven against the resolver rather than through either caller, because the drifts were *between*
/// the callers: a test that went through one of them would still be describing one copy.
/// </summary>
public class CoverResolverTests
{
    private const string CoverHost = "images.example.invalid";
    private const string CoverUrl = $"https://{CoverHost}/cover.jpg";

    // ---------------------------------------------------------------------
    // The three drifts
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Never_remembers_a_cover_the_limiter_would_not_let_it_ask_for()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);
        var cache = new CoverCache();
        var handler = new StubHandler(_ => Jpeg());

        // The host asked to be left alone for longer than an import will wait, so nothing is sent.
        limiter.RecordFailure(CoverHost, TimeSpan.FromMinutes(5));

        var resolver = Resolver(handler, cache: cache, limiter: limiter);
        var refused = await resolver.ResolveAsync(CoverUrl, new FakeBlobService());

        Assert.Equal(CoverRefusal.Throttled, refused.Refusal);
        Assert.Empty(handler.Requests);

        // The drift that lived on the import side and mattered most. CoverCache is a singleton shared
        // with the batch page, so recording a paced request as a dead cover turned one host's
        // sixty-second breaker into ten minutes of 502 thumbnails — for covers nothing had ever asked
        // the host about.
        Assert.Null(cache.RecentFailure(CoverUrl));
    }

    [Fact]
    public async Task Serves_bytes_it_is_already_holding_over_a_failure_it_remembers()
    {
        var cache = new CoverCache();
        var previews = new CoverPreviewCache();
        var handler = new StubHandler(_ => Jpeg());

        cache.RememberFailure(CoverUrl, CoverFetcher.Unfetchable);
        previews.Store(CoverUrl, [1, 2, 3], "image/png");

        var resolved = await Resolver(handler, cache: cache, previews: previews).ResolveAsync(CoverUrl);

        // Both copies asked the negative cache before the preview cache, so a cover whose bytes were
        // sitting in memory was refused for having failed earlier. A remembered failure is a claim
        // about the network; bytes in hand are not, so they win.
        Assert.Equal([1, 2, 3], resolved.Bytes);
        Assert.Equal("image/png", resolved.ContentType);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Leaves_the_previewed_bytes_in_memory_after_storing_them_as_a_blob()
    {
        var previews = new CoverPreviewCache();
        previews.Store(CoverUrl, [1, 2, 3], "image/png");
        var handler = new StubHandler(_ => Jpeg());

        var stored = await Resolver(handler, previews: previews).StoreAsync(CoverUrl, new FakeBlobService());

        Assert.NotNull(stored.BlobId);
        Assert.Empty(handler.Requests);

        // Dropping the entry here is what the import used to do, and it is one failed SaveChanges away
        // from an orphaned blob and a re-download — the "at most one request per URL" promise broken by
        // the code that keeps it. The importer drops it after its save lands instead.
        Assert.NotNull(previews.Get(CoverUrl));
    }

    [Fact]
    public void Drops_the_previewed_bytes_when_the_caller_says_its_save_landed()
    {
        var previews = new CoverPreviewCache();
        previews.Store(CoverUrl, [1, 2, 3], "image/png");

        Assert.True(Resolver(new StubHandler(_ => Jpeg()), previews: previews).ForgetPreview(CoverUrl));

        // The other half: once the blob exists and the persistent cache answers for this URL, a second
        // copy in memory is image bytes held in a singleton for nothing.
        Assert.Null(previews.Get(CoverUrl));
        Assert.Equal(0, previews.HeldBytes);
    }

    // ---------------------------------------------------------------------
    // One request per URL, concurrently rather than merely eventually
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Asks_the_host_once_when_two_callers_want_the_same_cover_at_the_same_moment()
    {
        var arrived = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var handler = new StubHandler(async _ =>
        {
            arrived.TrySetResult();
            await release.Task;
            return Jpeg();
        });

        var resolver = Resolver(handler);

        var first = resolver.ResolveAsync(CoverUrl);
        await arrived.Task;

        // Started while the first is provably still in flight — which is the only state the caches
        // cannot cover. They make one-request-per-URL true *eventually*, because the second scene of a
        // pack hits the persistent cache after the first finished; two tabs, or a preview racing the
        // apply it triggered, both miss every cache and both go to the host.
        var second = resolver.ResolveAsync(CoverUrl);

        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(handler.Requests);
        Assert.All(results, result => Assert.Equal("image/jpeg", result.ContentType));
    }

    [Fact]
    public async Task Fetches_again_once_the_shared_request_has_finished()
    {
        var handler = new StubHandler(_ => Jpeg());
        var resolver = Resolver(handler);

        await resolver.ResolveAsync(CoverUrl);
        await resolver.ResolveAsync(CoverUrl);

        // The in-flight entry has to be cleared when the fetch ends. It is added before the fetch
        // starts for exactly this reason: started first, a fast failure would remove an entry that had
        // not been inserted yet, and the completed task would be joined by every later caller forever.
        // With no cache wired here, two sequential asks are two requests — which is what proves it.
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Waits_out_its_own_fetch_rather_than_refusing_the_cover_it_started()
    {
        var handler = new StubHandler(async _ =>
        {
            await Task.Delay(400);
            return Jpeg();
        });

        var resolver = Resolver(handler);

        // The ceiling a preview asks under, scaled down so the test does not sleep for it. It bounds
        // how long this caller may *queue* at the limiter's gate — not how long the transfer it went
        // on to start is allowed to take, which is the HTTP client's own timeout.
        var resolved = await resolver.ResolveAsync(CoverUrl, maxWait: TimeSpan.FromMilliseconds(100));

        // The request went out and the host answered it. Refusing the one caller waiting on that
        // answer — with a message saying something *else* is asking for it — throws away a fetch that
        // had already succeeded, and the browser then retries a cover that was on its way. It reads as
        // a race and is not one: the winner joins its own Lazy through GetOrAdd, so any cover slower
        // than the wait its caller allows refuses that caller. A 5 MB animated GIF takes seconds, so
        // this fired on every cold cover rather than on a collision.
        Assert.Single(handler.Requests);
        Assert.Equal("image/jpeg", resolved.ContentType);
    }

    // ---------------------------------------------------------------------
    // Cancellation is our side giving up
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Does_not_remember_a_cover_the_caller_stopped_waiting_for()
    {
        var cache = new CoverCache();
        var release = new TaskCompletionSource();
        var handler = new StubHandler(async _ =>
        {
            await release.Task;
            return Jpeg();
        });

        using var caller = new CancellationTokenSource();
        var resolving = Resolver(handler, cache: cache).ResolveAsync(CoverUrl, ct: caller.Token);
        await caller.CancelAsync();

        // CoverImg aborts on unmount and on cover change, so closing the review or scrolling a row away
        // mid-fetch is this. Folded into "could not be fetched" it was negative-cached, and that cover
        // was then dead for every row and every apply for ten minutes.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving);
        Assert.Null(cache.RecentFailure(CoverUrl));

        release.SetResult();
    }

    [Fact]
    public async Task Reports_a_cancelled_fetch_as_cancelled_rather_than_as_a_dead_cover()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // The fetcher's own half of the rule. Its contract is "never throws", which is right for
        // anything the image host does — but cancellation is not the image host doing anything, and a
        // caller that cannot tell the two apart caches the wrong one.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CoverFetcher.FetchAsync(
            new Uri(CoverUrl),
            new StubHttpClientFactory(new StubHandler(_ => Jpeg())),
            new CoverHostAllowlist([CoverHost]),
            ct: cancelled.Token));
    }

    // ---------------------------------------------------------------------
    // A redirect may not weaken the transport
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Refuses_a_redirect_that_drops_an_https_cover_onto_http()
    {
        var insecure = $"http://{CoverHost}/cover.jpg";
        var handler = new StubHandler(request =>
            request.RequestUri!.Scheme == Uri.UriSchemeHttps ? RedirectTo(insecure) : Jpeg());

        var fetched = await CoverFetcher.FetchAsync(
            new Uri(CoverUrl),
            new StubHttpClientFactory(handler),
            new CoverHostAllowlist([CoverHost]));

        // The allowlist checks the host, not the transport, so this hop passed every check there was:
        // same host, still http(s). It put the request and the identifying User-Agent on the wire in
        // cleartext at a redirect the image host chose.
        Assert.Null(fetched.Bytes);
        Assert.Equal(CoverFetcher.Downgraded, fetched.Skipped);

        // Named rather than folded into "could not be fetched", and refused before it is followed.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Follows_a_redirect_that_strengthens_the_transport()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.Scheme == Uri.UriSchemeHttp ? RedirectTo(CoverUrl) : Jpeg());

        var fetched = await CoverFetcher.FetchAsync(
            new Uri($"http://{CoverHost}/cover.jpg"),
            new StubHttpClientFactory(handler),
            new CoverHostAllowlist([CoverHost]));

        // The other direction is the ordinary one — an image host moving its traffic to https — and
        // refusing it would drop covers to enforce nothing.
        Assert.NotNull(fetched.Bytes);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ---------------------------------------------------------------------
    // What the limiter's handler must not take away on its way past
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Still_reads_the_declared_length_and_type_through_the_limiters_handler()
    {
        var clock = new FakeClock();
        var oversize = new StubHandler(_ =>
        {
            var content = new ByteArrayContent([1, 2, 3, 4]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            content.Headers.ContentLength = CoverFetcher.MaxCoverBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var refused = await Resolver(oversize, limiter: new CoverRateLimiter(clock, clock.DelayAsync))
            .ResolveAsync(CoverUrl);

        // The handler now replaces response.Content with a wrapper, so that the per-host permit is held
        // until the body is actually read rather than released at headers. CoverFetcher reads
        // Content-Length and Content-Type off that content to refuse a hostile response *before*
        // streaming it — a wrapper that dropped either would quietly turn the cheap guard into the
        // expensive one, and every test of those guards builds a chain without this handler in it.
        Assert.Null(refused.Bytes);
        Assert.Equal(CoverFetcher.Unfetchable, refused.Skipped);
    }

    [Fact]
    public async Task Still_serves_the_content_type_through_the_limiters_handler()
    {
        var clock = new FakeClock();

        var resolved = await Resolver(new StubHandler(_ => Jpeg()), limiter: new CoverRateLimiter(clock, clock.DelayAsync))
            .ResolveAsync(CoverUrl);

        // The other half of the same worry: the media type decides whether these bytes are servable at
        // all, and it arrives on the content the wrapper replaced.
        Assert.Equal("image/jpeg", resolved.ContentType);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0], resolved.Bytes);
    }

    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    private static CoverResolver Resolver(
        StubHandler handler,
        CoverCache? cache = null,
        CoverPreviewCache? previews = null,
        CoverRateLimiter? limiter = null)
    {
        // Built through the limiter's own handler when a test supplies one, so a paced refusal reaches
        // the resolver the way it does in the shipped chain rather than being simulated.
        HttpMessageHandler chain = limiter is null
            ? handler
            : new CoverRateLimitHandler(limiter) { InnerHandler = handler };

        return new CoverResolver(previews, new StubHttpClientFactory(chain), new CoverHostAllowlist([CoverHost]), cache);
    }

    private static HttpResponseMessage Jpeg()
    {
        var content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage RedirectTo(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };

    /// <summary>
    /// The image host. Its own rather than shared with the other cover suites: this one has to be able
    /// to *block* mid-request, which is the only way to observe two callers overlapping, and a fake
    /// that grows a feature for a second caller stops describing either.
    /// </summary>
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this(request => Task.FromResult(respond(request)))
        {
        }

        private readonly Lock _gate = new();

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (_gate)
                Requests.Add(request.RequestUri!);

            return respond(request);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A blob store that only has to accept, since nothing here reads one back.</summary>
    private sealed class FakeBlobService : IBlobService
    {
        private int _stored;

        public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default) =>
            Task.FromResult($"blob-{++_stored}");

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default) =>
            Task.FromResult<(Stream, string)?>(null);

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
