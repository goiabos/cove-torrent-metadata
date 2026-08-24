using System.Net;
using System.Net.Http.Headers;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Cove.Plugins;
using Cove.TorrentMetadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// Covers <c>TorrentApplyService</c>'s cover import, and the batch path that also fetches covers.
///
/// This path had no coverage at all: every other apply test constructs <c>new TorrentApplyService(db)</c>,
/// which leaves <see cref="IBlobService"/> and <see cref="IHttpClientFactory"/> null, and the fetch
/// returns immediately. Two cover bugs shipped and were caught by hand rather than by a test — the
/// cover gated behind the scalar <c>Overwrite</c> flag, and the batch service constructing the applier
/// without its blob and HTTP dependencies. Both are named regressions below.
///
/// The image host is stubbed at <see cref="HttpMessageHandler"/> level so the tests control the status,
/// the content type and the *shape* of the body — a body that declares no length is the only way to
/// tell the streaming cap apart from the declared-length one.
/// </summary>
public class CoverImportTests
{
    private const string CoverHost = "images.example.invalid";
    private const string CoverUrl = $"https://{CoverHost}/cover.jpg";

    /// <summary>Matches <c>TorrentApplyService.MaxCoverBytes</c>, which is private.</summary>
    private const long MaxCoverBytes = 16 * 1024 * 1024;

    // ---------------------------------------------------------------------
    // The happy path
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Stores_the_fetched_cover_and_reports_that_it_changed()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => ImageResponse(body: [0xFF, 0xD8, 0xFF, 0xE0]));

        var result = await Service(db, blobs, handler).ApplyAsync(Request(videoId));

        Assert.True(result!.CoverChanged);
        var stored = Assert.Single(blobs.Stored);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0], stored.Data);
        Assert.Equal("image/jpeg", stored.ContentType);
        Assert.Equal(blobs.LastBlobId, (await db.Videos.SingleAsync()).ImageBlobId);
        Assert.Equal(new Uri(CoverUrl), Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task Passes_the_content_type_through_unchanged()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var blobs = new FakeBlobService();

        // The blob keeps whatever the host served. An animated WebP that arrives as image/webp has to be
        // stored as image/webp or it cannot render as one later.
        await Service(db, blobs, new StubHandler(_ => ImageResponse("image/webp"))).ApplyAsync(Request(videoId));

        Assert.Equal("image/webp", Assert.Single(blobs.Stored).ContentType);
    }

    [Fact]
    public async Task Replaces_an_existing_cover_even_though_overwrite_is_off()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, existingBlobId: "previous-cover");
        var blobs = new FakeBlobService();

        // Regression. The cover was once gated behind the scalar-field Overwrite flag, so ticking the
        // cover box did nothing on a video that already had one — while the dialog said "will replace".
        // A cover URL is only ever sent when the reviewer ticked the box, so its presence *is* the intent.
        var result = await Service(db, blobs, new StubHandler(_ => ImageResponse()))
            .ApplyAsync(Request(videoId) with { Overwrite = false });

        Assert.True(result!.CoverChanged);
        Assert.Equal(blobs.LastBlobId, (await db.Videos.SingleAsync()).ImageBlobId);
    }

    [Fact]
    public async Task Leaves_the_replaced_blob_to_the_hosts_own_cleanup()
    {
        // We assign ImageBlobId and never delete the blob it displaced, which looks like a leak. It is
        // not: BlobReferenceSaveChangesInterceptor is registered on CoveContext by AddCoveData, and on
        // any modified *BlobId property it deletes the original value after the save completes.
        //
        // Doing it ourselves would be worse than redundant. The cleanup has to run *after* the save —
        // BlobReferenceCounter opens its own scope and counts rows in the database, so a delete issued
        // next to the assignment would still see the old reference, retain the blob, and quietly do
        // nothing. This test wires the interceptor exactly as the host does, so if that guarantee ever
        // goes away we find out here rather than by watching a blob directory grow.
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var blobs = new FakeBlobService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBlobReferenceCoordinator, BlobReferenceCoordinator>();
        services.AddSingleton<IBlobService>(blobs);
        // Both, because the host registers both: the interceptor rejects a blob-reference change made
        // inside an explicit transaction, and the coordinator is its opt-in. An apply is one
        // transaction, so a fixture with only the first describes an arrangement Cove never
        // builds and fails on the guard rather than on anything this test is about.
        services.AddScoped<BlobReferenceSaveChangesInterceptor>();
        services.AddScoped<BlobReferenceTransactionCoordinator>();
        services.AddDbContext<CoveContext>((provider, options) => options
            .UseSqlite(connection)
            .AddInterceptors(provider.GetRequiredService<BlobReferenceSaveChangesInterceptor>()));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        await db.Database.EnsureCreatedAsync();

        var video = new Video { Title = "video", ImageBlobId = "previous-cover" };
        db.Videos.Add(video);
        await db.SaveChangesAsync();
        Assert.Empty(blobs.Deleted);

        await Service(
                db,
                blobs,
                new StubHandler(_ => ImageResponse()),
                blobTransactions: scope.ServiceProvider.GetRequiredService<BlobReferenceTransactionCoordinator>())
            .ApplyAsync(Request(video.Id));

        Assert.Equal(["previous-cover"], blobs.Deleted);
    }

    [Fact]
    public async Task Fetches_nothing_when_the_reviewer_selected_no_cover()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => ImageResponse());

        var result = await Service(db, blobs, handler).ApplyAsync(Request(videoId) with { CoverUrl = null });

        Assert.False(result!.CoverChanged);
        Assert.Empty(handler.Requests);
        Assert.Empty(blobs.Stored);
    }

    // ---------------------------------------------------------------------
    // What is refused, and why
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://images.example.invalid/cover.jpg")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("not a url at all")]
    public async Task Refuses_a_cover_url_that_is_not_http(string url)
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var handler = new StubHandler(_ => ImageResponse());

        var result = await Service(db, new FakeBlobService(), handler)
            .ApplyAsync(Request(videoId) with { CoverUrl = url });

        Assert.False(result!.CoverChanged);
        // The scheme is checked before anything is opened, so a file:// URL is never dereferenced.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Ignores_a_response_that_is_not_successful()
    {
        await AssertNoCoverAsync(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    // An SVG is not a raster image: it can carry a <script> and its own <image href>/<use>/CSS
    // references are fetched by the browser with none of this pipeline's allowlist, User-Agent or
    // pacing. A prefix test on "image/" let it through as one; the allowlist below is what
    // makes this the same refusal as any other non-raster content type.
    [InlineData("image/svg+xml")]
    public async Task Ignores_a_response_that_is_not_an_image(string contentType)
    {
        await AssertNoCoverAsync(_ => ImageResponse(contentType));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/jpg")]
    [InlineData("image/png")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("image/avif")]
    public async Task Stores_every_raster_type_the_allowlist_names(string contentType)
    {
        // The other half of the same rule: an allowlist that only ever gets exercised by its refusals
        // could shrink to nothing and every test above would stay green.
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var blobs = new FakeBlobService();

        var result = await Service(db, blobs, new StubHandler(_ => ImageResponse(contentType)))
            .ApplyAsync(Request(videoId));

        Assert.True(result!.CoverChanged);
        Assert.Equal(contentType, Assert.Single(blobs.Stored).ContentType);
    }

    [Fact]
    public async Task Ignores_a_response_with_no_content_type_at_all()
    {
        await AssertNoCoverAsync(_ =>
        {
            var content = new ByteArrayContent([1, 2, 3, 4]);
            content.Headers.ContentType = null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
    }

    [Fact]
    public async Task Ignores_a_body_that_declares_more_than_the_cap()
    {
        await AssertNoCoverAsync(_ =>
        {
            var content = new ByteArrayContent([1, 2, 3, 4]);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Headers.ContentLength = MaxCoverBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
    }

    [Fact]
    public async Task Ignores_a_body_that_exceeds_the_cap_without_declaring_a_length()
    {
        // The declared length is the easy half. A host that sends no Content-Length — or lies about it —
        // is only stopped by the check inside the read loop, which is what this exercises.
        await AssertNoCoverAsync(_ =>
        {
            var content = new UndeclaredLengthContent(MaxCoverBytes + 81920);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
    }

    [Fact]
    public async Task Ignores_an_empty_body()
    {
        await AssertNoCoverAsync(_ => ImageResponse(body: []));
    }

    // ---------------------------------------------------------------------
    // A failed cover must never cost the user the tags they just approved
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Keeps_the_approved_tags_when_the_image_host_is_unreachable()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var handler = new StubHandler(_ => throw new HttpRequestException("no such host"));

        var result = await Service(db, new FakeBlobService(), handler).ApplyAsync(Request(videoId));

        Assert.NotNull(result);
        Assert.False(result.CoverChanged);
        // The cover is the least important thing in a proposal; the tags are the point of it.
        Assert.Equal(1, result.TagsAdded);
        Assert.Equal("kissing", (await db.Tags.SingleAsync()).Name);
    }

    [Fact]
    public async Task Keeps_the_approved_tags_when_the_blob_store_fails()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var blobs = new FakeBlobService { OnStore = () => throw new IOException("disk full") };

        var result = await Service(db, blobs, new StubHandler(_ => ImageResponse())).ApplyAsync(Request(videoId));

        Assert.NotNull(result);
        Assert.False(result.CoverChanged);
        Assert.Equal(1, result.TagsAdded);
        Assert.Null((await db.Videos.SingleAsync()).ImageBlobId);
    }

    [Fact]
    public async Task Skips_the_cover_when_the_service_was_built_without_its_dependencies()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);

        // The default construction the rest of the suite uses. Covers are skipped rather than throwing,
        // which is why the batch service silently no-opped on them for a while — see below.
        var result = await new TorrentApplyService(db).ApplyAsync(Request(videoId));

        Assert.False(result!.CoverChanged);
        Assert.Equal(1, result.TagsAdded);
    }

    // ---------------------------------------------------------------------
    // The batch path, which is a second construction site
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Batch_apply_imports_covers()
    {
        await using var db = CreateContext();
        // Bulk apply defaults to tags the library already has, and skips a video whose proposal comes
        // out empty — so the tag has to exist for the row to be applied at all.
        await SeedTagAsync(db);
        var videoId = await SeedVideoAsync(db, size: SceneSize, withFile: true);
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => ImageResponse());
        var batch = Batch(db, blobs, handler);

        var result = await batch.ApplyAsync(new BatchApplyRequest { Rows = [Row(videoId, "scene")], ImportCovers = true });

        // Regression. TorrentBatchService once built TorrentApplyService without the blob and HTTP
        // dependencies, so bulk cover import reported success and stored nothing.
        Assert.Equal(1, result.VideosTouched);
        Assert.Single(blobs.Stored);
        Assert.Equal(blobs.LastBlobId, (await db.Videos.SingleAsync()).ImageBlobId);
    }

    [Fact]
    public async Task Batch_apply_fetches_no_cover_when_covers_were_not_requested()
    {
        await using var db = CreateContext();
        await SeedTagAsync(db);
        var videoId = await SeedVideoAsync(db, size: SceneSize, withFile: true);
        var handler = new StubHandler(_ => ImageResponse());

        var result = await Batch(db, new FakeBlobService(), handler)
            .ApplyAsync(new BatchApplyRequest { Rows = [Row(videoId, "scene")], ImportCovers = false });

        Assert.Equal(1, result.VideosTouched);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------
    // The host allowlist
    //
    // A cover URL is the one field in a proposal that makes the *server* reach out, and it arrives as
    // `cover url` inside a .torrent downloaded from a tracker. Before this the only checks were
    // "absolute" and "http or https", so a crafted torrent could aim the fetch at anything the Cove host
    // can reach. The manifest declares two image hosts; Cove parses that declaration and enforces
    // nothing, so the extension enforces it on itself.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Never_asks_for_a_cover_on_a_host_the_manifest_does_not_declare()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => ImageResponse());

        var result = await Service(db, blobs, handler)
            .ApplyAsync(Request(videoId) with { CoverUrl = "http://169.254.169.254/latest/meta-data" });

        // No request at all, rather than a discarded response: with blind SSRF the request *is* the harm,
        // and the 30-second timeout makes reachability observable even when nothing comes back.
        Assert.Empty(handler.Requests);
        Assert.False(result!.CoverChanged);

        // And the rest of the proposal still applies. A refused cover must never cost the user the tags
        // they just approved — the same reason every other failure here returns null instead of throwing.
        Assert.Equal(1, result.TagsAdded);
    }

    [Fact]
    public async Task Refuses_a_host_that_merely_ends_with_a_declared_one()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var handler = new StubHandler(_ => ImageResponse());

        // "evilimages.example.invalid" ends with the declared host and is a completely different domain.
        // Registering one is trivial, so a suffix check without the separating dot is a hole, not a nicety.
        var result = await Service(db, new FakeBlobService(), handler)
            .ApplyAsync(Request(videoId) with { CoverUrl = $"https://evil{CoverHost}/cover.jpg" });

        Assert.Empty(handler.Requests);
        Assert.False(result!.CoverChanged);
    }

    [Fact]
    public async Task Refuses_a_subdomain_of_a_declared_host_that_was_not_marked_for_subdomains()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => ImageResponse());

        // Subdomains used to be included automatically. They are opt-in now because a listed apex is
        // often a shared suffix — allowlisting one admitted any subdomain an attacker could get a
        // record for, and the URL being matched comes out of an untrusted .torrent.
        var result = await Service(db, blobs, handler)
            .ApplyAsync(Request(videoId) with { CoverUrl = $"https://cdn.{CoverHost}/cover.jpg" });

        Assert.False(result!.CoverChanged);
        Assert.Empty(blobs.Stored);

        // Refused before the request, which is the half that matters: with SSRF the request is the harm.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Fetches_from_a_subdomain_of_a_host_declared_with_the_wildcard()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var blobs = new FakeBlobService();

        // The opt-in spelling, for the operator whose image host really does move covers between
        // subdomains without the tracker's metadata changing.
        var result = await Service(db, blobs, new StubHandler(_ => ImageResponse()), coverHosts: Wildcard())
            .ApplyAsync(Request(videoId) with { CoverUrl = $"https://cdn.{CoverHost}/cover.jpg" });

        Assert.True(result!.CoverChanged);
        Assert.Single(blobs.Stored);
    }

    [Fact]
    public async Task Stops_a_redirect_that_leaves_the_allowlist()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var blobs = new FakeBlobService();

        // A declared host answering with a redirect is the obvious way around a URL check, which is why
        // the client is registered with automatic redirects off: the default handler would have followed
        // this one before the service ever saw it, and only the first URL would have been checked.
        var handler = new StubHandler(request => request.RequestUri!.Host == CoverHost
            ? RedirectTo("http://169.254.169.254/latest/meta-data")
            : ImageResponse());

        var result = await Service(db, blobs, handler).ApplyAsync(Request(videoId));

        Assert.Equal(new Uri(CoverUrl), Assert.Single(handler.Requests));
        Assert.False(result!.CoverChanged);
        Assert.Empty(blobs.Stored);
    }

    [Fact]
    public async Task Follows_a_redirect_that_stays_on_a_declared_host()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var blobs = new FakeBlobService();
        var moved = $"https://{CoverHost}/real-cover.jpg";

        // Checking each hop must not mean refusing all of them: hosts really do redirect, and a cover
        // that stays inside the declared scope is exactly what the scope permits.
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsoluteUri == CoverUrl ? RedirectTo(moved) : ImageResponse());

        var result = await Service(db, blobs, handler).ApplyAsync(Request(videoId));

        Assert.True(result!.CoverChanged);
        Assert.Single(blobs.Stored);
        Assert.Equal([new Uri(CoverUrl), new Uri(moved)], handler.Requests);
    }

    [Fact]
    public async Task Gives_up_on_a_redirect_that_never_arrives()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var blobs = new FakeBlobService();

        // Every hop is allowlisted, so nothing here is refused on host grounds — only the hop count
        // stops it. Without that bound a declared host can hold the apply open for its whole timeout.
        var handler = new StubHandler(_ => RedirectTo($"https://{CoverHost}/next.jpg"));

        var result = await Service(db, blobs, handler).ApplyAsync(Request(videoId));

        Assert.False(result!.CoverChanged);
        Assert.Empty(blobs.Stored);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Refuses_every_cover_when_no_allowlist_was_supplied()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => ImageResponse());

        // The allowlist is the only thing in front of this request, so an unwired dependency has to cost
        // a cover rather than the check — the same failure the batch service already had once with the
        // blob and HTTP dependencies, which failed the harmless way round.
        var service = new TorrentApplyService(
            db, blobs, new CoverResolver(httpClients: new StubHttpClientFactory(handler)));

        var result = await service.ApplyAsync(Request(videoId));

        Assert.Empty(handler.Requests);
        Assert.False(result!.CoverChanged);
    }

    // ---------------------------------------------------------------------
    // Saying why, which is what makes an empty default survivable
    //
    // The allowlist ships empty, so the first apply on a fresh install imports no cover. Before
    // this every refusal returned null in silence and the only signal was a cover that did not change,
    // which is indistinguishable from a broken feature.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Names_the_refused_host_so_the_reviewer_knows_what_to_add()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var handler = new StubHandler(_ => ImageResponse());

        var result = await Service(db, new FakeBlobService(), handler)
            .ApplyAsync(Request(videoId) with { CoverUrl = "https://other.example.invalid/cover.jpg" });

        Assert.False(result!.CoverChanged);
        Assert.Empty(handler.Requests);

        // The host itself, not a generic refusal: the user has to know which name to type into the
        // setting, and the URL they were shown is not necessarily the host that was checked.
        Assert.Contains("other.example.invalid", result.CoverSkipped);
        Assert.Contains("settings", result.CoverSkipped!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Says_the_allowlist_is_unconfigured_rather_than_that_the_host_was_rejected()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var handler = new StubHandler(_ => ImageResponse());

        var service = new TorrentApplyService(
            db, new FakeBlobService(), Resolver(handler, new CoverHostAllowlist([])));

        var result = await service.ApplyAsync(Request(videoId));

        Assert.Empty(handler.Requests);
        Assert.False(result!.CoverChanged);

        // Wording, and it matters: this is the shipped default, so "not in the allowlist" would
        // describe an unconfigured feature as a blocked tracker. The user has not set it up yet.
        Assert.Contains("no cover hosts are configured", result.CoverSkipped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is not in the cover-host allowlist", result.CoverSkipped!);
    }

    [Fact]
    public async Task Reports_no_skip_reason_when_the_cover_actually_imported()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var handler = new StubHandler(_ => ImageResponse(body: [0xFF, 0xD8]));

        var result = await Service(db, new FakeBlobService(), handler).ApplyAsync(Request(videoId));

        // The other half of the contract. A reason that is always populated is no more use than one
        // that never is, and the dialog renders it whenever it is present.
        Assert.True(result!.CoverChanged);
        Assert.Null(result.CoverSkipped);
    }

    [Fact]
    public async Task Reports_no_skip_reason_when_no_cover_was_requested()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var handler = new StubHandler(_ => ImageResponse());

        var result = await Service(db, new FakeBlobService(), handler)
            .ApplyAsync(Request(videoId) with { CoverUrl = null });

        // Not asking for a cover is not a skipped cover. Reporting one here would put a warning in
        // front of every reviewer who left the box unticked.
        Assert.Empty(handler.Requests);
        Assert.Null(result!.CoverSkipped);
    }

    [Fact]
    public async Task Batch_apply_counts_the_skipped_covers_and_reports_one_reason_for_them()
    {
        await using var db = CreateContext();
        // Same setup as Batch_apply_imports_covers: bulk skips a video whose proposal is empty, so the
        // tag has to already exist for the row to be applied and the cover to be reached at all.
        await SeedTagAsync(db);
        var videoId = await SeedVideoAsync(db, size: SceneSize, withFile: true);
        var handler = new StubHandler(_ => ImageResponse());

        var index = new TorrentIndex();
        index.Add(new TorrentRelease
        {
            Name = "scene",
            TagList = ["kissing"],
            CoverUrl = CoverUrl,
            Videos = [new TorrentVideoFile("scene.mp4", SceneSize)],
        });
        var batch = new TorrentBatchService(
            db, index, new TorrentMetadataSettings(), new FakeBlobService(),
            new CoverHostAllowlist([]), Resolver(handler, new CoverHostAllowlist([])));

        var result = await batch.ApplyAsync(new BatchApplyRequest { Rows = [Row(videoId, "scene")], ImportCovers = true });

        Assert.Equal(1, result.VideosTouched);

        Assert.Empty(handler.Requests);
        Assert.Equal(0, result.CoversImported);
        Assert.Equal(1, result.CoversSkipped);

        // One sample rather than one line per video: a bulk run against an unconfigured allowlist
        // skips every cover for the same reason, and 468 copies of it is not more information.
        Assert.Contains("no cover hosts are configured", result.CoverSkipReason, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------
    // The User-Agent
    //
    // One of the three measures the tracker's staff conditioned clearance on. These go through the
    // *registered* client rather than a hand-built handler chain, because the thing that can break is
    // the registration: a header asserted on a chain a test assembled itself would stay green while
    // the shipped client sent nothing.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sends_a_user_agent_naming_the_extension_and_the_shipped_version()
    {
        var handler = new StubHandler(_ => ImageResponse());
        var client = RegisteredClient(handler);

        await client.GetAsync(CoverUrl);

        // Compared against extension.json rather than a literal: the version is bumped in that file at
        // every release, and a User-Agent frozen at whatever it said when this was written is exactly
        // the drift reading it at request time exists to prevent.
        Assert.Equal(
            $"TorrentMetadata/{Shipped.Manifest().Version} (+{CoverUserAgentHandler.ContactUrl})",
            Assert.Single(handler.UserAgents));
    }

    [Fact]
    public async Task Replaces_any_user_agent_already_on_the_request()
    {
        var handler = new StubHandler(_ => ImageResponse());
        var client = RegisteredClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "SomethingElse/9.9");

        await client.GetAsync(CoverUrl);

        // Ours alone, not appended to. Staff asked for a UA they can identify and *block*; a list of
        // products makes that rule a parse rather than a match.
        Assert.Equal($"TorrentMetadata/{Shipped.Manifest().Version} (+{CoverUserAgentHandler.ContactUrl})", Assert.Single(handler.UserAgents));
    }

    [Theory]
    [InlineData("0.8.0", "TorrentMetadata/0.8.0 (+" + CoverUserAgentHandler.ContactUrl + ")")]
    [InlineData("1.2.3-dev.179+abc", "TorrentMetadata/1.2.3-dev.179+abc (+" + CoverUserAgentHandler.ContactUrl + ")")]
    // A version that is not a valid HTTP token would throw inside the handler, and the cover fetch
    // swallows every exception — so a manifest typo would present as covers silently not importing,
    // the failure the allowlist just finished removing.
    [InlineData("1.0 beta \"x\"", "TorrentMetadata/1.0betax (+" + CoverUserAgentHandler.ContactUrl + ")")]
    [InlineData("", "TorrentMetadata/0 (+" + CoverUserAgentHandler.ContactUrl + ")")]
    [InlineData(null, "TorrentMetadata/0 (+" + CoverUserAgentHandler.ContactUrl + ")")]
    public void Keeps_the_user_agent_well_formed_whatever_the_version_says(string? version, string expected)
    {
        Assert.Equal(expected, CoverUserAgentHandler.Format(version));
    }

    [Fact]
    public async Task Paces_cover_requests_through_the_client_the_extension_registers()
    {
        var clock = new FakeClock();
        var limiter = new CoverRateLimiter(clock, clock.DelayAsync);
        var handler = new StubHandler(_ => ImageResponse());
        var client = RegisteredClient(handler, limiter);

        for (var i = 0; i <= CoverRateLimiter.Burst; i++)
            (await client.GetAsync(CoverUrl)).Dispose();

        // Asserted through the real registration, like the User-Agent: the limiter working in
        // isolation says nothing about whether the shipped client goes anywhere near it.
        Assert.Equal(CoverRateLimiter.Burst + 1, handler.Requests.Count);
        Assert.Equal([CoverRateLimiter.MinimumInterval], clock.Waits);
    }

    [Fact]
    public async Task Single_apply_fetches_through_the_client_the_extension_registers()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var factory = new StubHttpClientFactory(new StubHandler(_ => ImageResponse()));

        await new TorrentApplyService(
            db,
            new FakeBlobService(),
            new CoverResolver(httpClients: factory, coverHosts: Allowlist())).ApplyAsync(Request(videoId));

        Assert.Equal(TorrentApplyService.CoverHttpClientName, Assert.Single(factory.Names));
    }

    [Fact]
    public async Task Batch_apply_fetches_through_the_client_the_extension_registers()
    {
        await using var db = CreateContext();
        await SeedTagAsync(db);
        var videoId = await SeedVideoAsync(db, size: SceneSize, withFile: true);
        var factory = new StubHttpClientFactory(new StubHandler(_ => ImageResponse()));

        var index = new TorrentIndex();
        index.Add(new TorrentRelease
        {
            Name = "scene",
            TagList = ["kissing"],
            CoverUrl = CoverUrl,
            Videos = [new TorrentVideoFile("scene.mp4", SceneSize)],
        });
        var batch = new TorrentBatchService(
            db, index, new TorrentMetadataSettings(), new FakeBlobService(), Allowlist(),
            new CoverResolver(httpClients: factory, coverHosts: Allowlist()));

        await batch.ApplyAsync(new BatchApplyRequest { Rows = [Row(videoId, "scene")], ImportCovers = true });

        // The bulk path builds its own applier, so it is a second chance to ask for the wrong client.
        Assert.Equal(TorrentApplyService.CoverHttpClientName, Assert.Single(factory.Names));
    }

    // ---------------------------------------------------------------------
    // The cover cache
    //
    // The last of the three measures staff conditioned clearance on: "caching to avoid redownloading
    // the same images over and over". The shape that matters is a pack — one cover URL applied to
    // every scene in turn — so most of these assert on the *request count*, not on the blob.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Reuses_the_stored_blob_instead_of_fetching_the_same_cover_twice()
    {
        await using var db = CreateContext();
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => ImageResponse());
        var cache = new CoverCache();

        var first = await SeedVideoAsync(db);
        var second = await SeedVideoAsync(db);

        var one = await Service(db, blobs, handler, cache).ApplyAsync(Request(first));
        var two = await Service(db, blobs, handler, cache).ApplyAsync(Request(second));

        // One request and one blob for two videos. Both halves matter: the request is what staff
        // asked about, and the blob is ours to avoid because Cove's store is GUID-keyed, so storing
        // the same bytes twice yields two files with nothing to notice it.
        Assert.Single(handler.Requests);
        Assert.Single(blobs.Stored);
        Assert.True(one!.CoverChanged);
        Assert.True(two!.CoverChanged);
        Assert.Equal(one.CoverSkipped, two.CoverSkipped);
    }

    [Fact]
    public async Task Reuses_a_cover_remembered_by_an_earlier_session()
    {
        await using var db = CreateContext();
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => ImageResponse());
        var store = new FakeStore();

        var first = new CoverCache();
        first.AttachStore(store);
        await Service(db, blobs, handler, first).ApplyAsync(Request(await SeedVideoAsync(db)));
        Assert.Single(handler.Requests);

        // A fresh cache on the same store, which is what a restart looks like. Persisting is the half
        // of the promise an in-memory map would not keep — every restart would re-download the lot.
        var second = new CoverCache();
        second.AttachStore(store);
        await Service(db, blobs, handler, second).ApplyAsync(Request(await SeedVideoAsync(db)));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Re_fetches_when_the_remembered_blob_is_gone_and_forgets_the_dangling_entry()
    {
        await using var db = CreateContext();
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => ImageResponse());
        var store = new FakeStore();
        var cache = new CoverCache();
        cache.AttachStore(store);

        await Service(db, blobs, handler, cache).ApplyAsync(Request(await SeedVideoAsync(db)));
        var firstBlob = blobs.LastBlobId!;

        // What the host does when the last video referencing a cover loses it: the blob goes, and the
        // remembered id now points at nothing. Handing that back would set ImageBlobId to a missing
        // blob, which the user sees as a broken image rather than as a cache fault.
        await blobs.DeleteBlobAsync(firstBlob);

        var result = await Service(db, blobs, handler, cache).ApplyAsync(Request(await SeedVideoAsync(db)));

        Assert.Equal(2, handler.Requests.Count);
        Assert.True(result!.CoverChanged);
        Assert.NotEqual(firstBlob, blobs.LastBlobId);

        // The entry now names the new blob. That the stale one is *pruned* rather than overwritten is
        // a separate claim and needs its own test — the URL hashes to the same key either way, so
        // this assertion cannot tell the two apart.
        Assert.Equal(blobs.LastBlobId, Assert.Single(store.Values).Value);
    }

    [Fact]
    public async Task Prunes_a_remembered_entry_whose_blob_has_gone()
    {
        var blobs = new FakeBlobService();
        var store = new FakeStore();
        var cache = new CoverCache();
        cache.AttachStore(store);

        using var image = new MemoryStream([0xFF, 0xD8]);
        var blobId = await blobs.StoreBlobAsync(image, "image/jpeg");
        await cache.RememberAsync(CoverUrl, blobId);
        Assert.Single(store.Values);

        await blobs.DeleteBlobAsync(blobId);

        Assert.Null(await cache.TryReuseAsync(CoverUrl, blobs));

        // Driven straight against the cache because the apply path immediately stores a replacement
        // under the same key, which would hide a missing delete behind an overwrite. Since the eviction fix this
        // is the *only* thing that deletes a persisted row, so it is also the whole of what bounds
        // the store: a row is dropped the first time it is looked up and found to point at nothing.
        Assert.Empty(store.Values);
    }

    [Fact]
    public async Task Keeps_the_persisted_row_when_a_cold_entry_falls_out_of_the_memory_cap()
    {
        var blobs = new FakeBlobService();
        var store = new FakeStore();
        // Two, so one insert past it evicts. Production never passes this; ten thousand real entries
        // would say nothing this does not.
        var cache = new CoverCache(maxCachedCovers: 2);
        cache.AttachStore(store);

        var cold = await RememberImageAsync(cache, blobs, "https://images.example/cold.jpg");
        await RememberImageAsync(cache, blobs, "https://images.example/b.jpg");
        await RememberImageAsync(cache, blobs, "https://images.example/c.jpg");

        // Evicted from memory — but the row survives, and that is the fix. Deleting it reclaimed a
        // ~110-byte record and charged the next video wanting this cover a fresh request to the image
        // host plus a duplicate blob, because Cove's store is GUID-keyed and cannot notice the same
        // bytes twice. The reclaim was four orders of magnitude smaller than the spend, and the spend
        // landed on a promise made to a third party.
        // Asserted by blob id rather than by key: `CoverCache.Key` is internal to the extension
        // assembly, and the claim here is about the row surviving, not about how it is addressed.
        Assert.Equal(3, store.Values.Count);
        Assert.Contains(cold, store.Values.Values);
    }

    [Fact]
    public async Task Reuses_a_cover_that_was_evicted_from_memory_without_asking_the_host()
    {
        await using var db = CreateContext();
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => ImageResponse());
        var store = new FakeStore();
        var cache = new CoverCache(maxCachedCovers: 1);
        cache.AttachStore(store);

        // The first apply caches this cover; the second, on a different URL of the same allowed host,
        // pushes it out of memory.
        await Service(db, blobs, handler, cache).ApplyAsync(Request(await SeedVideoAsync(db)));
        await Service(db, blobs, handler, cache).ApplyAsync(
            Request(await SeedVideoAsync(db)) with { CoverUrl = $"https://{CoverHost}/other.jpg" });
        Assert.Equal(2, handler.Requests.Count);

        var result = await Service(db, blobs, handler, cache).ApplyAsync(Request(await SeedVideoAsync(db)));

        // Still two requests and still two blobs: the evicted entry was re-read from the store, so a
        // cold cache costs one store lookup rather than a download and a duplicate. This is the
        // behaviour the old eviction destroyed, and it is what "one download per cover URL, persisted
        // across restarts" means in the clearance conditions.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, blobs.Stored.Count);
        Assert.True(result!.CoverChanged);
    }

    /// <summary>Stores one image and remembers it under <paramref name="url"/>, returning the blob id.</summary>
    private static async Task<string> RememberImageAsync(CoverCache cache, FakeBlobService blobs, string url)
    {
        using var image = new MemoryStream([0xFF, 0xD8]);
        var blobId = await blobs.StoreBlobAsync(image, "image/jpeg");
        await cache.RememberAsync(url, blobId);
        return blobId;
    }

    [Fact]
    public async Task Keeps_a_shared_cover_alive_while_another_video_still_points_at_it()
    {
        // Modelled, not verified — see FakeBlobService. Cove.Api.BlobService does the real counting
        // and is not referenceable here, so this pins the contract the cache now depends on rather
        // than the host's implementation of it.
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        FakeBlobService? blobs = null;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBlobReferenceCoordinator, BlobReferenceCoordinator>();
        services.AddSingleton<IBlobService>(_ => blobs!);
        // Both, because the host registers both: the interceptor rejects a blob-reference change made
        // inside an explicit transaction, and the coordinator is its opt-in. An apply is one
        // transaction, so a fixture with only the first describes an arrangement Cove never
        // builds and fails on the guard rather than on anything this test is about.
        services.AddScoped<BlobReferenceSaveChangesInterceptor>();
        services.AddScoped<BlobReferenceTransactionCoordinator>();
        services.AddDbContext<CoveContext>((provider, options) => options
            .UseSqlite(connection)
            .AddInterceptors(provider.GetRequiredService<BlobReferenceSaveChangesInterceptor>()));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        await db.Database.EnsureCreatedAsync();

        blobs = new FakeBlobService
        {
            ReferenceCounter = blobId => db.Videos.Count(video => video.ImageBlobId == blobId),
        };

        var cache = new CoverCache();
        var handler = new StubHandler(_ => ImageResponse());
        var first = await SeedVideoAsync(db);
        var second = await SeedVideoAsync(db);

        var blobTransactions = scope.ServiceProvider.GetRequiredService<BlobReferenceTransactionCoordinator>();
        await Service(db, blobs, handler, cache, blobTransactions: blobTransactions).ApplyAsync(Request(first));
        await Service(db, blobs, handler, cache, blobTransactions: blobTransactions).ApplyAsync(Request(second));

        var shared = Assert.Single(blobs.Live);

        db.Videos.Remove(await db.Videos.SingleAsync(video => video.Id == first));
        await db.SaveChangesAsync();

        // Before the cache, one video meant one blob and this could not arise. Now a pack's scenes
        // share one, so deleting any of them has to leave the rest with their artwork.
        Assert.Equal([shared], blobs.Live);
        Assert.Empty(blobs.Deleted);
    }

    [Fact]
    public async Task Stops_re_requesting_a_cover_that_failed_until_the_ttl_expires()
    {
        await using var db = CreateContext();
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var clock = new FakeClock();
        var cache = new CoverCache(clock);

        var first = await Service(db, blobs, handler, cache).ApplyAsync(Request(await SeedVideoAsync(db)));
        Assert.Single(handler.Requests);

        var second = await Service(db, blobs, handler, cache).ApplyAsync(Request(await SeedVideoAsync(db)));

        // A dead cover on a pack re-requested once per scene, forever. That is the case this exists
        // for, and the replayed reason is the original one so the second video's report is no worse.
        Assert.Single(handler.Requests);
        Assert.Equal(first!.CoverSkipped, second!.CoverSkipped);
        Assert.NotNull(second.CoverSkipped);

        clock.Advance(CoverCache.FailureTtl + TimeSpan.FromSeconds(1));
        await Service(db, blobs, handler, cache).ApplyAsync(Request(await SeedVideoAsync(db)));

        // Expiry matters as much as the caching: a cover that was briefly 500 has to become fetchable
        // again without a restart.
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Never_negative_caches_a_cover_it_did_not_actually_request()
    {
        await using var db = CreateContext();
        var handler = new StubHandler(_ => ImageResponse());
        var cache = new CoverCache();

        // Refused by the allowlist, so no request was made and nothing was learned about the host.
        var service = new TorrentApplyService(
            db, new FakeBlobService(), Resolver(handler, new CoverHostAllowlist([]), cache));
        await service.ApplyAsync(Request(await SeedVideoAsync(db)));

        Assert.Empty(handler.Requests);

        // Configuring the host must work immediately. Caching the refusal would make the user wait out
        // a TTL after fixing the exact thing the message told them to fix.
        var configured = await Service(db, new FakeBlobService(), handler, cache)
            .ApplyAsync(Request(await SeedVideoAsync(db)));

        Assert.Single(handler.Requests);
        Assert.True(configured!.CoverChanged);
    }

    [Fact]
    public async Task Batch_apply_fetches_a_shared_cover_once_for_the_whole_pack()
    {
        await using var db = CreateContext();
        await SeedTagAsync(db);
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => ImageResponse());

        var scenes = new[] { SceneSize, SceneSize + 1, SceneSize + 2 };
        var videoIds = new List<int>();
        foreach (var size in scenes)
            videoIds.Add(await SeedVideoAsync(db, size: size, withFile: true));

        var index = new TorrentIndex();
        index.Add(new TorrentRelease
        {
            Name = "pack",
            TagList = ["kissing"],
            CoverUrl = CoverUrl,
            Videos = [.. scenes.Select((size, i) => new TorrentVideoFile($"scene{i}.mp4", size))],
        });

        var batch = new TorrentBatchService(
            db, index, new TorrentMetadataSettings(), blobs,
            Allowlist(), Resolver(handler, cache: new CoverCache()));

        var result = await batch.ApplyAsync(
            new BatchApplyRequest
            {
                Rows = [.. videoIds.Select(id => Row(id, "pack"))],
                IncludePacks = true,
                ImportCovers = true,
            });

        // The measured folder holds packs of up to 1913 videos, all sharing one cover URL. Un-cached
        // that is 1913 identical requests in one run, which is the shape staff described as spamming.
        Assert.Equal(3, result.VideosTouched);
        Assert.Equal(3, result.CoversImported);
        Assert.Single(handler.Requests);
        Assert.Single(blobs.Stored);
    }

    // ---------------------------------------------------------------------
    // Reading through the preview cache
    //
    // The review dialog now previews a cover through the extension's own proxy, which fetches it with
    // the same client, the same pacing and the same allowlist an import uses, and keeps the bytes.
    // The acceptance criterion for the pair is that the network is hit **at most once per URL**, in
    // whichever order preview and import happen.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Imports_a_cover_that_was_previewed_without_asking_the_host_again()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => ImageResponse());
        var previews = new CoverPreviewCache();

        // What the proxy leaves behind after the dialog has shown the cover.
        previews.Store(CoverUrl, [0xFF, 0xD8, 0x11], "image/jpeg");

        var result = await new TorrentApplyService(
            db, blobs, Resolver(handler, cache: new CoverCache(), previews: previews))
            .ApplyAsync(Request(videoId));

        // The reviewer looked at it, ticked the box, and the tracker saw one request for the two.
        Assert.True(result!.CoverChanged);
        Assert.Empty(handler.Requests);
        Assert.Equal([0xFF, 0xD8, 0x11], Assert.Single(blobs.Stored).Data);
        Assert.Equal(blobs.LastBlobId, (await db.Videos.SingleAsync()).ImageBlobId);
    }

    [Fact]
    public async Task Stops_holding_a_previewed_cover_in_memory_once_it_is_a_blob()
    {
        await using var db = CreateContext();
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => ImageResponse());
        var previews = new CoverPreviewCache();
        var cache = new CoverCache();
        previews.Store(CoverUrl, [0xFF, 0xD8, 0x11], "image/jpeg");

        var service = new TorrentApplyService(db, blobs, Resolver(handler, cache: cache, previews: previews));
        await service.ApplyAsync(Request(await SeedVideoAsync(db)));

        // Dropped, not kept: the persistent cache answers for this URL from here on, so a second copy
        // in memory is image bytes held in a singleton for nothing.
        Assert.Null(previews.Get(CoverUrl));
        Assert.Equal(0, previews.HeldBytes);

        // And the blob is what the next scene of the pack gets, with no request either way.
        var second = await service.ApplyAsync(Request(await SeedVideoAsync(db)));
        Assert.True(second!.CoverChanged);
        Assert.Empty(handler.Requests);
        Assert.Single(blobs.Stored);
    }

    [Fact]
    public async Task Keeps_a_previewed_cover_when_the_blob_store_could_not_take_it()
    {
        await using var db = CreateContext();
        var blobs = new FakeBlobService { OnStore = () => throw new IOException("disk full") };
        var handler = new StubHandler(_ => ImageResponse());
        var previews = new CoverPreviewCache();
        previews.Store(CoverUrl, [0xFF, 0xD8, 0x11], "image/jpeg");

        var result = await new TorrentApplyService(
            db, blobs, Resolver(handler, cache: new CoverCache(), previews: previews))
            .ApplyAsync(Request(await SeedVideoAsync(db)));

        // The store failed, not the fetch. Dropping the entry would send the next attempt back to the
        // image host for bytes we are still holding — and the tags, as ever, survive.
        Assert.False(result!.CoverChanged);
        Assert.NotNull(result.CoverSkipped);
        Assert.Equal(1, result.TagsAdded);
        Assert.NotNull(previews.Get(CoverUrl));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Still_fetches_when_nothing_has_previewed_the_cover()
    {
        await using var db = CreateContext();
        var blobs = new FakeBlobService();
        var handler = new StubHandler(_ => ImageResponse());
        var previews = new CoverPreviewCache();

        // The other half of the read-through: an empty preview cache must not be mistaken for a hit.
        // Applying from the batch page, which never rendered the dialog, is exactly this case.
        var result = await new TorrentApplyService(
            db, blobs, Resolver(handler, cache: new CoverCache(), previews: previews))
            .ApplyAsync(Request(await SeedVideoAsync(db)));

        Assert.True(result!.CoverChanged);
        Assert.Single(handler.Requests);
        // Not put into the preview cache on the way past: it is a blob now, and the persistent cache
        // is what remembers that.
        Assert.Equal(0, previews.HeldBytes);
    }

    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    private const long SceneSize = 5_387_499_251L;

    private static HttpResponseMessage RedirectTo(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };

    /// <summary>Asserts that a given response leaves the video without a cover, and the tags intact.</summary>
    private static async Task AssertNoCoverAsync(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var blobs = new FakeBlobService();

        var result = await Service(db, blobs, new StubHandler(respond)).ApplyAsync(Request(videoId));

        Assert.False(result!.CoverChanged);
        Assert.Empty(blobs.Stored);
        Assert.Null((await db.Videos.SingleAsync()).ImageBlobId);
        Assert.Equal(1, result.TagsAdded);
    }

    /// <summary>
    /// The cover client exactly as <c>TorrentMetadataExtension.ConfigureServices</c> registers it, with
    /// the stub swapped in for the primary handler so nothing leaves the machine.
    ///
    /// Built through the real service collection on purpose: what these tests are about is the
    /// registration, and a chain assembled by hand here would agree with itself forever while the
    /// shipped client sent no header at all.
    /// </summary>
    private static HttpClient RegisteredClient(StubHandler handler, CoverRateLimiter? limiter = null)
    {
        var services = new ServiceCollection();
        Shipped.Extension().ConfigureServices(services, Shipped.Context());

        // The rate-limit handler resolves its limiter from the provider, so overriding the singleton
        // here puts one driven by a clock the test controls into the shipped chain — the alternative
        // is a suite that really waits a second, which is how a timing rule stops being checked.
        if (limiter is not null)
            services.AddSingleton(limiter);

        // Re-registering the same name adds another configuration step; the primary handler is
        // last-one-wins, so this replaces the socket without touching the rest of the chain.
        services.AddHttpClient(TorrentApplyService.CoverHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(TorrentApplyService.CoverHttpClientName);
    }

    private static TorrentApplyService Service(
        CoveContext db,
        IBlobService blobs,
        StubHandler handler,
        CoverCache? cache = null,
        CoverHostAllowlist? coverHosts = null,
        BlobReferenceTransactionCoordinator? blobTransactions = null) =>
        new(db, blobs, Resolver(handler, coverHosts, cache), baseline: null, blobTransactions);

    /// <summary>
    /// A configured operator, declaring the host every fixture URL here uses. The real list is a user
    /// setting now and ships empty; <see cref="ManifestTests"/> is what checks the registration
    /// reads that setting rather than a hand-written list.
    /// </summary>
    private static CoverHostAllowlist Allowlist() => new([CoverHost]);

    /// <summary>
    /// The resolver the apply path now goes through, wired to a stubbed image host.
    ///
    /// One helper rather than a constructor call per test, because the cover sequence lives in one
    /// place now and a test that assembles its own pieces would be describing an arrangement
    /// the extension never builds.
    /// </summary>
    private static CoverResolver Resolver(
        StubHandler handler,
        CoverHostAllowlist? coverHosts = null,
        CoverCache? cache = null,
        CoverPreviewCache? previews = null) =>
        new(previews, new StubHttpClientFactory(handler), coverHosts ?? Allowlist(), cache);

    /// <summary>The same operator, having opted that host's subdomains in as well.</summary>
    private static CoverHostAllowlist Wildcard() => new([$"{CoverHostSetting.WildcardPrefix}{CoverHost}"]);

    private static TorrentBatchService Batch(CoveContext db, IBlobService blobs, StubHandler handler)
    {
        var index = new TorrentIndex();
        index.Add(new TorrentRelease
        {
            Name = "scene",
            TagList = ["kissing"],
            CoverUrl = CoverUrl,
            Videos = [new TorrentVideoFile("scene.mp4", SceneSize)],
        });
        return new TorrentBatchService(
            db, index, new TorrentMetadataSettings(), blobs, Allowlist(), Resolver(handler));
    }

    /// <summary>
    /// One row of the batch overview, by what identifies it: the video, and which torrent describes it
    /// (named rows, reshaped once rows gained a real identity).
    /// </summary>
    private static BatchRowRef Row(int videoId, string torrent, string? torrentId = null) =>
        new() { VideoId = videoId, TorrentName = torrent, TorrentId = torrentId };

    private static TorrentApplyRequest Request(int videoId) => new()
    {
        VideoId = videoId,
        Tags = ["kissing"],
        CoverUrl = CoverUrl,
    };

    private static HttpResponseMessage ImageResponse(string contentType = "image/jpeg", byte[]? body = null)
    {
        var content = new ByteArrayContent(body ?? [1, 2, 3, 4]);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>The library tag "kissing" resolves to, so a batch proposal is not empty.</summary>
    private static async Task SeedTagAsync(CoveContext db)
    {
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
    }

    private static async Task<int> SeedVideoAsync(
        CoveContext db,
        string? existingBlobId = null,
        long size = 0,
        bool withFile = false)
    {
        var video = new Video { Title = "video", ImageBlobId = existingBlobId };
        db.Videos.Add(video);
        await db.SaveChangesAsync();

        if (withFile)
        {
            var folder = new Folder { Path = $"/library/{video.Id}" };
            db.Set<Folder>().Add(folder);
            await db.SaveChangesAsync();

            db.VideoFiles.Add(new VideoFile
            {
                Basename = "scene.mp4",
                ParentFolderId = folder.Id,
                Size = size,
                VideoId = video.Id,
            });
            await db.SaveChangesAsync();
        }

        return video.Id;
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new CoveContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    // ---------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------

    /// <summary>
    /// A blob store that keeps what it was given, so the cache's existence check has something real to
    /// ask.
    ///
    /// <see cref="ReferenceCounter"/> is the interesting part and needs its caveat stated. Reusing one
    /// blob across many videos makes the host's reference counting a correctness dependency, and that
    /// counting lives in <c>Cove.Api.BlobService</c> — an assembly this project does not reference and
    /// cannot. So the retention behaviour is *modelled* here, not verified: set the counter and the
    /// fake keeps a blob that a video still points at, the way the real one does.
    ///
    /// That makes the shared-blob test a statement of the contract we now depend on rather than proof
    /// the host honours it. It is still worth having — it fails the moment someone decides one video
    /// means one blob and starts deleting — but nobody should read it as covering the host.
    ///
    /// Note also that <c>DeleteBlobIfUnreferencedAsync</c> is a *default* interface method whose
    /// default body deletes unconditionally, so a fake that leaves it alone silently drops shared
    /// blobs. That is the trap: the host's helper deletes unconditionally when its optional
    /// argument is omitted.
    /// </summary>
    private sealed class FakeBlobService : IBlobService
    {
        public List<(byte[] Data, string ContentType)> Stored { get; } = [];

        /// <summary>Set to make the store fail, standing in for a full disk or a broken blob root.</summary>
        public Action? OnStore { get; init; }

        /// <summary>
        /// Stands in for <c>Cove.Api.BlobService</c>'s row count. Left null, this fake behaves like the
        /// unoverridden interface default and deletes on request.
        /// </summary>
        public Func<string, int>? ReferenceCounter { get; init; }

        public string? LastBlobId { get; private set; }

        private readonly Dictionary<string, (byte[] Data, string ContentType)> _live = [];

        public async Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
        {
            OnStore?.Invoke();

            using var buffer = new MemoryStream();
            await data.CopyToAsync(buffer, ct);
            Stored.Add((buffer.ToArray(), contentType));
            LastBlobId = $"blob-{Stored.Count}";
            _live[LastBlobId] = (buffer.ToArray(), contentType);
            return LastBlobId;
        }

        public List<string> Deleted { get; } = [];

        /// <summary>Live blob ids, so a test can say "this one survived" without reaching inside.</summary>
        public IReadOnlyCollection<string> Live => _live.Keys;

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default) =>
            Task.FromResult(_live.TryGetValue(blobId, out var blob)
                ? ((Stream)new MemoryStream(blob.Data), blob.ContentType)
                : ((Stream, string)?)null);

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
        {
            Deleted.Add(blobId);
            _live.Remove(blobId);
            return Task.CompletedTask;
        }

        public Task DeleteBlobIfUnreferencedAsync(string blobId, CancellationToken ct = default) =>
            ReferenceCounter?.Invoke(blobId) > 0 ? Task.CompletedTask : DeleteBlobAsync(blobId, ct);
    }

    /// <summary>An extension store backed by a dictionary, for the cache's persistence.</summary>
    private sealed class FakeStore : IExtensionStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, string>(Values));
    }


    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        /// <summary>The User-Agent as it went out, or the empty string when none was set.</summary>
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

    /// <summary>The handler outlives the client, which the service disposes after every fetch.</summary>
    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        /// <summary>
        /// Names asked for. The User-Agent and everything after it hang off one *named* registration
        ///, so a fetch path that asked for a differently named client — or the default one —
        /// would quietly send none of it.
        /// </summary>
        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    /// <summary>
    /// A body that reports no <c>Content-Length</c> and produces its bytes lazily, so the cap can only be
    /// enforced by the read loop rather than by inspecting a header.
    /// </summary>
    private sealed class UndeclaredLengthContent(long length) : HttpContent
    {
        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new GeneratedStream(length));
    }

    /// <summary>Yields <paramref name="length"/> bytes without ever holding them in memory.</summary>
    private sealed class GeneratedStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, _remaining);
            _remaining -= take;
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
