using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using Cove.Sdk;
using Cove.TorrentMetadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// The cover proxy — the endpoint an <c>&lt;img&gt;</c> in the review UI points at.
///
/// It exists because the UI used to point that <c>&lt;img&gt;</c> at the URL out of the torrent, so
/// the *browser* fetched it and none of the cover machinery ran: no allowlist, no identifying
/// User-Agent, no pacing, no cache. Three of the four conditions the tracker's staff attached to
/// clearance are about those requests, and a page render bypassed all three.
///
/// Driven over the real HTTP pipeline rather than against <c>CoverProxyService</c> alone, because
/// half of what is being asserted only exists at that layer: the status codes an <c>&lt;img&gt;</c>
/// reacts to, the <c>Retry-After</c> the batch page's retry rides on, and the <c>Cache-Control</c>
/// that must appear on a served cover and never on a refusal. The image host is stubbed into the
/// registered client, so nothing here leaves the machine.
/// </summary>
public class CoverProxyTests
{
    private const string Base = "/api/extensions/torrent-metadata";
    private const string CoverEndpoint = $"{Base}/cover";
    private const string SettingsEndpoint = $"{Base}/settings";

    private const string ImageHost = "images.example.invalid";
    private const string TorrentCover = $"https://{ImageHost}/cover.jpg";

    private static string Ask(string url) => $"{CoverEndpoint}?url={Uri.EscapeDataString(url)}";

    // ---------------------------------------------------------------------
    // What it refuses, before anything is sent
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("not a url at all")]
    [InlineData("/relative/cover.jpg")]
    [InlineData("")]
    public async Task Answers_bad_request_for_a_url_it_cannot_use(string url)
    {
        await using var host = await StartAsync();

        var response = await host.Client.GetAsync(Ask(url));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.ImageHost.Requests);
    }

    [Fact]
    public async Task Refuses_a_host_the_operator_has_not_named_and_says_which()
    {
        await using var host = await StartAsync();

        // No allowlist configured, which is the shipped default — so this is what the first render on
        // a fresh install does.
        var response = await host.Client.GetAsync(Ask(TorrentCover));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(host.ImageHost.Requests);

        // The same wording the apply path uses, because it is the same explanation: the user has not
        // set this up rather than the host being blocked.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("no cover hosts are configured", body.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Logs_the_reason_a_cover_was_refused()
    {
        var logs = new ListLoggerProvider();
        await using var host = await StartAsync(logs: logs);

        var response = await host.Client.GetAsync(Ask(TorrentCover));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // The reason used to exist only as {"error": …} in an answer one browser read once, so from
        // the log's side every refusal was invisible and from the console's side every one was the
        // same status code — one diagnosis went through the wrong subsystem for exactly that
        // reason. One line per refusal, and at Information rather than Debug, because Debug is
        // off exactly when the line is needed.
        var line = Assert.Single(logs.Of<CoverProxyService>());
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("no cover hosts are configured", line.Message, StringComparison.OrdinalIgnoreCase);

        // The sentence, not the request: a cover URL names the tracker's image host and its paths,
        // and the reason already carries as much of that as a log file should.
        Assert.DoesNotContain(TorrentCover, line.Message);
    }

    [Fact]
    public async Task Logs_nothing_for_a_served_cover()
    {
        var logs = new ListLoggerProvider();
        await using var host = await StartAsync(logs: logs);
        await AllowAsync(host);

        var response = await host.Client.GetAsync(Ask(TorrentCover));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // A served cover is the ordinary case — a line per thumbnail is the volume worry that made
        // this a question in the first place, and the answer is that success says nothing.
        Assert.Empty(logs.Of<CoverProxyService>());
    }

    [Fact]
    public async Task Refuses_a_url_aimed_at_the_servers_own_network()
    {
        await using var host = await StartAsync();
        await AllowAsync(host);

        // The endpoint takes a URL, so "is this an open proxy?" is the question it has to answer. It
        // is not: an allowlisted host is the only thing it will fetch, and the list ships empty, so
        // it reaches nowhere an import could not already. With blind SSRF the request *is* the harm,
        // which is why this asserts that none was made rather than that none succeeded.
        var response = await host.Client.GetAsync(Ask("http://169.254.169.254/latest/meta-data"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(host.ImageHost.Requests);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data", "169.254.169.254")]
    [InlineData("http://127.0.0.1:5073/api/videos", "127.0.0.1")]
    [InlineData("http://10.0.0.5/admin", "10.0.0.5")]
    [InlineData("http://localhost:5073/api/videos", "localhost")]
    public async Task Refuses_an_internal_target_even_after_someone_puts_it_in_the_allowlist(
        string url, string entry)
    {
        await using var proxy = await StartAsync();

        // The endpoint is guarded by videos:scrape, and so is the settings endpoint — so "reviewer
        // adds one line to the allowlist" is a move any account that can reach the proxy can make.
        // Without it, …/cover?url= is an authenticated proxy onto the host's network,
        // and where the target answered image/* the body came back, so it was not even blind.
        var saved = await proxy.Client.PutAsJsonAsync(SettingsEndpoint, new { coverHosts = new[] { entry } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // The settings endpoint refuses to store it at all, which is the assertion that matters:
        // there is no state in which the proxy has been pointed inward.
        var stored = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(stored.GetProperty("coverHosts").EnumerateArray());

        var response = await proxy.Client.GetAsync(Ask(url));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(proxy.ImageHost.Requests);
    }

    [Fact]
    public async Task Refuses_a_subdomain_of_a_listed_host_and_names_the_edit_that_would_allow_it()
    {
        await using var host = await StartAsync();
        await AllowAsync(host);

        var response = await host.Client.GetAsync(Ask($"https://cdn.{ImageHost}/cover.jpg"));

        // Subdomains are opt-in. This is the one refusal that rule newly produces, so
        // the message has to name the entry the operator already has and the edit that fixes it —
        // "not in the allowlist" would send them to add a host that is already sitting there.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(host.ImageHost.Requests);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains($"*.{ImageHost}", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetches_a_subdomain_once_the_operator_marks_the_entry_for_it()
    {
        await using var host = await StartAsync();

        var saved = await host.Client.PutAsJsonAsync(SettingsEndpoint, new { coverHosts = new[] { $"*.{ImageHost}" } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var response = await host.Client.GetAsync(Ask($"https://cdn.{ImageHost}/cover.jpg"));

        // The other side of the same rule, through the endpoint the panel actually writes to: the
        // marker has to survive normalisation, or the opt-in would be unreachable from the UI.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(host.ImageHost.Requests);
    }

    [Fact]
    public async Task Stops_a_redirect_that_leaves_the_allowlist()
    {
        await using var host = await StartAsync(respond: _ =>
            new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("http://169.254.169.254/latest/meta-data") },
            });
        await AllowAsync(host);

        var response = await host.Client.GetAsync(Ask(TorrentCover));

        // The hop check is shared with the import path rather than reimplemented here, which is the
        // reason the fetch core is one function: a declared host answering with a redirect is the
        // obvious way around a URL check, and a second copy of the check is how one of them loses it.
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(new Uri(TorrentCover), Assert.Single(host.ImageHost.Requests));
    }

    // ---------------------------------------------------------------------
    // What it serves
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Serves_the_cover_through_the_client_the_extension_registers()
    {
        await using var host = await StartAsync();
        await AllowAsync(host);

        var response = await host.Client.GetAsync(Ask(TorrentCover));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);

        // The point of the whole change: a preview is now a request the tracker can attribute to this
        // extension and block on its own. A browser <img> sent the browser's own User-Agent.
        Assert.Equal($"TorrentMetadata/{Shipped.Manifest().Version} (+{CoverUserAgentHandler.ContactUrl})", Assert.Single(host.ImageHost.UserAgents));
    }

    [Fact]
    public async Task Lets_the_browser_reuse_a_served_cover_but_never_a_refusal()
    {
        await using var host = await StartAsync();
        await AllowAsync(host);

        var served = await host.Client.GetAsync(Ask(TorrentCover));

        // Private, because a cover is only visible to someone with the permission this endpoint
        // gates on — a shared cache holding it would hand it to anyone behind the same proxy.
        Assert.True(served.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromDays(1), served.Headers.CacheControl!.MaxAge);

        var refused = await host.Client.GetAsync(Ask("https://elsewhere.invalid/cover.jpg"));

        // A cached refusal is the failure mode this avoids: a 429 means "come back shortly", and a
        // browser that held it for a day would leave the cover missing until a reload.
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Null(refused.Headers.CacheControl);
    }

    [Fact]
    public async Task Asks_the_image_host_once_however_many_times_the_page_renders_it()
    {
        await using var host = await StartAsync();
        await AllowAsync(host);

        for (var i = 0; i < 4; i++)
            (await host.Client.GetAsync(Ask(TorrentCover))).Dispose();

        // The preview cache. A pack's scenes share one cover URL, so a batch page full of them is one
        // request rather than one per visible row — the shape staff described as spamming.
        Assert.Single(host.ImageHost.Requests);
    }

    [Fact]
    public async Task Serves_a_cover_a_sibling_scene_already_imported_without_asking_again()
    {
        var blobs = new MemoryBlobService();
        var cache = new CoverCache();
        await using var host = await StartAsync(blobs: blobs, coverCache: cache);
        await AllowAsync(host);

        using (var image = new MemoryStream([1, 2, 3]))
            await cache.RememberAsync(TorrentCover, await blobs.StoreBlobAsync(image, "image/png"));

        var response = await host.Client.GetAsync(Ask(TorrentCover));

        // Previewing a cover another scene already imported must cost nothing at all — the bytes are
        // on disk, and the persistent cache is what knows which blob they are.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([1, 2, 3], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(host.ImageHost.Requests);
    }

    [Fact]
    public async Task Refuses_a_blob_store_hit_whose_content_type_predates_the_allowlist()
    {
        var blobs = new MemoryBlobService();
        var cache = new CoverCache();
        await using var host = await StartAsync(blobs: blobs, coverCache: cache);
        await AllowAsync(host);

        // Models a blob written before CoverFetcher's raster allowlist existed — or by anything else
        // that ever wrote a *BlobId — carrying a content type today's policy would refuse. The reuse
        // path has to re-check rather than trust what it stored, or an old SVG already on disk would
        // keep being served same-origin forever, no matter how tight the fetch-time allowlist gets
        //.
        using (var image = new MemoryStream([1, 2, 3]))
            await cache.RememberAsync(TorrentCover, await blobs.StoreBlobAsync(image, "image/svg+xml"));

        var response = await host.Client.GetAsync(Ask(TorrentCover));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        // No live retry either: the blob is what is wrong, not the URL, so this must not spend a
        // request against the image host to reach the same refusal.
        Assert.Empty(host.ImageHost.Requests);
    }

    // ---------------------------------------------------------------------
    // What it will not echo into the browser
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    public async Task Refuses_an_active_content_type_instead_of_echoing_it(string contentType)
    {
        await using var host = await StartAsync(respond: _ => TypedResponse(contentType));
        await AllowAsync(host);

        var response = await host.Client.GetAsync(Ask(TorrentCover));

        // …/cover?url= is same-origin, cookie-authenticated and directly navigable. Serving either of
        // these back would hand the URL out of an untrusted .torrent a way to run in Cove's own origin.
        // The fetch-layer allowlist is what actually decides; this proves the proxy has no second path
        // around it.
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(CoverFetcher.Unfetchable, (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString());
    }

    [Fact]
    public async Task Sets_nosniff_on_a_served_cover()
    {
        await using var host = await StartAsync();
        await AllowAsync(host);

        var response = await host.Client.GetAsync(Ask(TorrentCover));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Sets_nosniff_on_a_refusal_too()
    {
        await using var host = await StartAsync();
        // No allowlist configured, so this never leaves the refusal branch — nosniff has to hold here
        // exactly as it does on a served image, because a header only present on the success path is
        // no defence at all against the branch that actually needs it least tested.
        var response = await host.Client.GetAsync(Ask(TorrentCover));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Sets_an_inline_content_disposition_naming_nothing_from_the_remote_url()
    {
        await using var host = await StartAsync();
        await AllowAsync(host);

        var response = await host.Client.GetAsync(Ask(TorrentCover));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        // inline, never attachment: a browser refuses to render an attachment-disposed response inside
        // the <img> this endpoint exists to feed. The filename is a fixed literal keyed only on the
        // content type served — never the remote URL or the remote host's own filename.
        Assert.Equal("inline", disposition!.DispositionType);
        Assert.Equal("cover.jpg", disposition.FileName?.Trim('"'));
    }

    // ---------------------------------------------------------------------
    // What it does when the answer is "not now"
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Refuses_rather_than_parking_an_image_element_for_the_import_paths_full_wait()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);
        await using var host = await StartAsync(limiter: limiter);
        await AllowAsync(host);

        // The host asked for ten seconds. An import would spend them — it has twenty to give and a
        // cover the user ticked to lose. A preview must not: it holds one of the browser's handful of
        // connections to Cove while it waits, and a screenful of them would starve the extension's
        // own API calls on the same origin.
        limiter.RecordFailure(ImageHost, TimeSpan.FromSeconds(10));

        var response = await host.Client.GetAsync(Ask(TorrentCover));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Empty(host.ImageHost.Requests);

        // The limiter's own number, so the batch page's retry comes back when the host is ready
        // rather than on a guess. Sending fewer requests weakens no promise made to the tracker.
        Assert.Equal(TimeSpan.FromSeconds(10), response.Headers.RetryAfter?.Delta);
        Assert.Null(response.Headers.CacheControl);
    }

    [Fact]
    public async Task Never_negative_caches_a_cover_the_limiter_would_not_let_it_ask_for()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);
        await using var host = await StartAsync(limiter: limiter);
        await AllowAsync(host);

        limiter.RecordFailure(ImageHost, TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.Client.GetAsync(Ask(TorrentCover))).StatusCode);

        // Nothing was learned about the cover, so a retry after the pace clears has to work. Caching
        // it as a failure would turn a second of politeness into ten minutes of a missing image.
        limiter.RecordSuccess(ImageHost);
        var retried = await host.Client.GetAsync(Ask(TorrentCover));

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Single(host.ImageHost.Requests);
    }

    [Fact]
    public async Task Replays_a_dead_cover_without_asking_the_host_a_second_time()
    {
        await using var host = await StartAsync(respond: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        await AllowAsync(host);

        Assert.Equal(HttpStatusCode.BadGateway, (await host.Client.GetAsync(Ask(TorrentCover))).StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, (await host.Client.GetAsync(Ask(TorrentCover))).StatusCode);

        // The negative cache, reached through the proxy. A dead cover on a 1913-scene pack is one
        // request, not one per row — and it is why the page can retry a failed thumbnail cheaply.
        Assert.Single(host.ImageHost.Requests);
    }

    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    /// <summary>Names the operator's cover hosts through the real settings endpoint.</summary>
    private static async Task AllowAsync(ProxyHost host)
    {
        var response = await host.Client.PutAsJsonAsync(SettingsEndpoint, new { coverHosts = new[] { ImageHost } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The extension mapped onto a real test server, with the image host stubbed into the *registered*
    /// cover client rather than into a chain assembled here — the User-Agent and the rate limiter hang
    /// off that registration, and a hand-built chain would agree with itself while the shipped one
    /// sent none of it.
    /// </summary>
    private static async Task<ProxyHost> StartAsync(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
        CoverRateLimiter? limiter = null,
        IBlobService? blobs = null,
        CoverCache? coverCache = null,
        ILoggerProvider? logs = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        if (logs is not null)
            builder.Logging.AddProvider(logs);

        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        builder.Services.AddDbContext<CoveContext>(options => options.UseSqlite(connection));

        // With the manifest applied, as the host loads it — the User-Agent reads the version off it
        // at request time, so an extension without one would send the placeholder and the assertion
        // that staff get an identifiable string would be checking nothing.
        var extension = Shipped.Extension();
        extension.UseTorrentFolder(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        extension.ConfigureServices(builder.Services, Shipped.Context());

        // Registered after the extension, so these win: the container resolves the last registration
        // of a service type. It is how a test drives the clock the limiter reads, and how the blob
        // store exists at all — Cove registers it, and this server is not Cove.
        var imageHost = new StubImageHost(respond ?? (_ => Jpeg()));
        builder.Services.AddHttpClient(TorrentApplyService.CoverHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => imageHost);
        if (limiter is not null)
            builder.Services.AddSingleton(limiter);
        if (blobs is not null)
            builder.Services.AddSingleton(blobs);
        if (coverCache is not null)
            builder.Services.AddSingleton(coverCache);

        var app = builder.Build();
        extension.MapEndpoints(app);
        await app.StartAsync();

        return new ProxyHost(app, imageHost, connection);
    }

    private static HttpResponseMessage Jpeg()
    {
        var content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage TypedResponse(string contentType)
    {
        var content = new ByteArrayContent([1, 2, 3, 4]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class ProxyHost(WebApplication app, StubImageHost imageHost, SqliteConnection connection)
        : IAsyncDisposable
    {
        public HttpClient Client { get; } = app.GetTestClient();

        public StubImageHost ImageHost { get; } = imageHost;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            connection.Dispose();
        }
    }

    /// <summary>
    /// Collects what the proxy writes to the host's log, category and all — the host wires the real
    /// logger factory, so what lands here is what an operator's log file would carry.
    /// </summary>
    private sealed class ListLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Category, string Message)> _records = [];

        public IReadOnlyList<(LogLevel Level, string Category, string Message)> Of<T>()
        {
            lock (_records)
                return _records.Where(r => r.Category == typeof(T).FullName).ToList();
        }

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ListLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._records)
                    owner._records.Add((logLevel, category, formatter(state, exception)));
            }
        }
    }

    /// <summary>
    /// The image host. Deliberately not shared with <c>CoverImportTests</c>'s stub: that one models
    /// the blob store's reference counting because the import path depends on it, and this one needs
    /// none of that — a fake that grows features for a second caller stops describing either.
    /// </summary>
    private sealed class StubImageHost(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        public List<string> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            // Joined with the space the wire itself uses between a product token and its comment —
            // Concat would weld "Product/1.0" to "(+url)" into a string no header ever carried.
            UserAgents.Add(string.Join(" ", request.Headers.UserAgent));
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>A blob store that keeps what it was given, so a cache hit has something to serve.</summary>
    private sealed class MemoryBlobService : IBlobService
    {
        private readonly Dictionary<string, (byte[] Data, string ContentType)> _blobs = [];

        public async Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
        {
            using var buffer = new MemoryStream();
            await data.CopyToAsync(buffer, ct);
            var id = $"blob-{_blobs.Count + 1}";
            _blobs[id] = (buffer.ToArray(), contentType);
            return id;
        }

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default) =>
            Task.FromResult(_blobs.TryGetValue(blobId, out var blob)
                ? ((Stream)new MemoryStream(blob.Data), blob.ContentType)
                : ((Stream, string)?)null);

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
        {
            _blobs.Remove(blobId);
            return Task.CompletedTask;
        }
    }
}
