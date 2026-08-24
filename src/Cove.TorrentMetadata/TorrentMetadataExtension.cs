using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.TorrentMetadata;

/// <summary>
/// Offers the metadata a tracker embeds in its .torrent files as a reviewable suggestion for videos
/// already in the library. Luminance is the dialect it reads.
///
/// Built as a self-contained extension — an entity action plus its own endpoints — rather than as an
/// <see cref="IScraperProvider"/>. The scraper route looks like the natural fit, but its fragment path
/// is unfinished in the host: <c>ScraperService.BuildVideoInput</c> never populates
/// <c>VideoScrapeInput.Files</c>, <c>IScraperHost.GetVideoAsync</c> has no implementation, and the UI
/// never calls <c>scrape-fragment</c> at all. A scraper implementation would therefore be dead code
/// until a host feature lands, which would make this extension a fork in waiting. Going through
/// <see cref="IActionExtension"/> keeps it installable against stock Cove.
///
/// It reads nothing from the network: torrents come from a local folder, and everything else is
/// computed against the database.
/// </summary>
public sealed class TorrentMetadataExtension : CoveExtensionBase, IApiExtension, IActionExtension, IStatefulExtension
{
    private readonly TorrentMetadataSettings _settings = new();
    private readonly CoverCache _coverCache = new();
    private readonly AppliedTorrentBaseline _appliedBaseline = new();
    private readonly CoverRateLimiter _rateLimiter = new();
    private readonly CoverPreviewCache _previewCache = new();

    /// <summary>
    /// Folder under the Cove data root that ingested .torrent files are read from.
    ///
    /// Deliberately not under <see cref="ExtensionContext.DataDirectory"/>: that is the extensions root,
    /// which the host enumerates looking for installable extensions, so a torrent folder there would be
    /// inspected as a candidate on every startup.
    /// </summary>
    public const string TorrentFolderName = "torrent-metadata";

    private const string MatchEndpoint = "/api/extensions/torrent-metadata/match";
    private const string ApplyEndpoint = "/api/extensions/torrent-metadata/apply";
    private const string ReloadEndpoint = "/api/extensions/torrent-metadata/reload";
    private const string FolderStateEndpoint = "/api/extensions/torrent-metadata/folder-state";
    private const string SettingsEndpoint = "/api/extensions/torrent-metadata/settings";
    private const string BatchEndpoint = "/api/extensions/torrent-metadata/batch";
    private const string BatchApplyEndpoint = "/api/extensions/torrent-metadata/batch/apply";
    private const string UploadEndpoint = "/api/extensions/torrent-metadata/upload";

    /// <summary>
    /// Serves a torrent's cover to the UI, so no page ever points an <c>&lt;img&gt;</c> at an image
    /// host directly. See <see cref="CoverProxyService"/> for why that matters.
    /// </summary>
    private const string CoverEndpoint = "/api/extensions/torrent-metadata/cover";

    /// <summary>
    /// The extension's own folder, listed and emptied. Never a source folder: those are the operator's
    /// and are read-only, which is what makes this one deletable at all.
    ///
    /// Named <c>write-folder</c> rather than <c>folder</c> because <see cref="FolderStateEndpoint"/>
    /// sits beside it and answers an unrelated question — whether *any* configured folder has moved
    /// since the last scan, sources included. Two adjacent paths differing by a suffix, one scoped to
    /// the folder we write and one to all of them, is a pair someone reads wrong exactly once.
    /// </summary>
    private const string WriteFolderEndpoint = "/api/extensions/torrent-metadata/write-folder";
    private const string WriteFolderRemoveEndpoint = "/api/extensions/torrent-metadata/write-folder/remove";

    /// <summary>
    /// How long a browser may reuse a proxied cover without asking again.
    ///
    /// Sent on success only. The URL identifies the image, so reopening the dialog or scrolling a row
    /// back into view is free — which is the same promise the server-side caches make, kept one layer
    /// further out. Never sent on a failure: a 429 is a "come back shortly", and caching it for a day
    /// would turn a moment's pacing into a cover that stays missing until a reload.
    /// </summary>
    private const string CoverCacheControl = "private, max-age=86400";

    /// <summary>
    /// The <c>Content-Disposition</c> filename for a served cover, keyed on the content type the proxy
    /// is about to serve — never on anything from the remote URL or the remote host's own filename
    ///. Every branch is a fixed literal; there is nothing here for an attacker-chosen string to
    /// reach. Falls back to an extensionless name rather than throwing if this ever drifts from
    /// <see cref="CoverFetcher.IsSafeRasterContentType"/> — the header is defence in depth, not the
    /// allowlist itself, so an unmapped type here must not turn into a 500 on the served image.
    /// </summary>
    private static string CoverFilename(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => "cover.jpg",
        "image/png" => "cover.png",
        "image/gif" => "cover.gif",
        "image/webp" => "cover.webp",
        "image/avif" => "cover.avif",
        _ => "cover",
    };

    /// <summary>
    /// Cap on a .torrent this extension will read, wherever it came from. Real ones are tens to
    /// hundreds of KB; this is generous.
    ///
    /// One constant for the upload endpoint and the folder walk on purpose. They are the same trust
    /// boundary — a file nobody vouched for — and were briefly two constants with the same value,
    /// which is how a cap that was raised in one place and not the other would leave the folder path
    /// reading what the endpoint refuses. The folder walk checks it with a stat before opening,
    /// since the point there is not to allocate the file's length.
    /// </summary>
    private const long MaxTorrentBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Cap on how many files one request may carry.
    ///
    /// Per-file size was bounded and the count was not, so the only thing standing between this
    /// endpoint and tens of thousands of writes-plus-reads-plus-parses in one request was Kestrel's
    /// default 30 MB body limit — the host's default, not our decision, and one a future Cove release
    /// could raise for its own uploads without ever knowing this existed.
    ///
    /// Comfortably above <c>UPLOAD_CHUNK_FILES</c> in <c>ui/src/upload.ts</c>: our own client splits a
    /// large drop into requests of 100, so this refuses nothing it sends and is a backstop for callers
    /// that are not it.
    /// </summary>
    private const int MaxTorrentUploadFiles = 200;

    /// <summary>
    /// Cap on how many torrents one reload will index.
    ///
    /// The measured corpus is 3,218, so this is not a limit anyone reaches by having a lot of
    /// torrents. It bounds a mistake: source folders are operator-chosen now, enumeration is
    /// <c>AllDirectories</c>, and a path a few levels too high turns a rescan into a crawl that fills
    /// memory. Reaching it is reported rather than silent, because a silently short index looks like
    /// torrents that stopped matching.
    ///
    /// It bounds what is *held*, not what is walked: refusing filesystem roots in
    /// <see cref="SourceFolderSetting"/> is what keeps the directory walk itself sane.
    /// </summary>
    private const int MaxIndexedTorrents = 50_000;

    private readonly TorrentIndex _index = new();
    private string? _torrentFolder;

    /// <summary>
    /// What the indexed folders looked like when the index was last built, so a later sweep can say
    /// whether they have moved on.
    ///
    /// In memory rather than in the extension store, because it never needs to outlive the process:
    /// <see cref="InitializeAsync"/> reloads the index on startup, which re-seeds this in the same
    /// call. Persisting it would write a few hundred kilobytes of torrent filenames into
    /// <c>extension_data</c> on every rescan to answer a question that is already answered.
    /// </summary>
    private IReadOnlyList<FolderSignature> _lastScan = [];

    // Id, Name and Version are deliberately not overridden: CoveExtensionBase reads them from
    // extension.json, which is also what names the install directory. Overriding Id with a value that
    // differs from the manifest makes the host serve asset URLs it cannot resolve — GetAsset builds the
    // path as <extensions>/<code Id>, so the UI bundle 404s and the whole extension runtime fails to load.

    /// <summary>The folder torrents are ingested from. Public so the reload endpoint can report it.</summary>
    public string? TorrentFolder => _torrentFolder;

    /// <summary>
    /// Reads torrents from <paramref name="path"/> instead of the default under the Cove data root.
    ///
    /// The default resolves through <c>COVE_HOME</c>, which is process-global — so pointing the
    /// endpoints at a scratch folder by setting that variable would leak across anything else running
    /// in the same process. This is the seam instead.
    /// </summary>
    public void UseTorrentFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _torrentFolder = path;
    }

    public int IndexedFileCount => _index.Count;

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        // Only the default; an explicit UseTorrentFolder wins regardless of which ran first.
        _torrentFolder ??= CoveDefaultPaths.GetDataSubdirectory(TorrentFolderName);

        // The index is extension-owned state shared across requests; the match service is per-request
        // because it holds a CoveContext, which the overlay leases per scope from the host's pool.
        services.AddSingleton(_index);
        services.AddSingleton(_settings);
        // Singleton so a bulk run and the single-apply path share one cache — a pack's scenes are
        // applied one request at a time, and a per-request cache would re-fetch for every one.
        services.AddSingleton(_coverCache);
        // Singleton so the bulk path and the dialog path record into one place, and so the overview
        // reads what either of them just wrote.
        services.AddSingleton(_appliedBaseline);
        // Singleton for the same reason, and more so: a per-request limiter would mean every video
        // in a bulk run started with a full burst, which is no limit at all.
        services.AddSingleton(_rateLimiter);
        // Singleton for the same reason again, and it is the seam that keeps a preview and the import
        // that follows it to one request between them: the dialog previews a cover, the reviewer ticks
        // the box, and the apply reads the bytes back out of here instead of asking the host twice.
        services.AddSingleton(_previewCache);

        // The operator's own cover-host list, enforced by us and read live off the settings singleton
        // so an edit takes effect without a restart. It used to come from permissions.network in the
        // manifest; that named one tracker's image hosts in a published artifact, and the host reads
        // the field nowhere anyway, so nothing was lost by moving it
        // and the scope is now something each operator can actually get right.
        services.AddSingleton(_ => new CoverHostAllowlist(_settings));

        // Redirects off, so TryStoreCoverAsync sees each hop and can check it. The default handler
        // follows them itself, which would leave only the first URL checked.
        //
        // ConnectCallback is the other half of the allowlist, and it has to be here because here is
        // where the socket is made: CoverHostAllowlist compares names, and the name is re-resolved by
        // the socket at connect time, so a host that answers public to the check and 127.0.0.1 to the
        // connect is fetched anyway. CoverAddressPolicy resolves once, checks every address, and
        // connects to those — no second lookup for an attacker to answer differently. It is
        // also why this is a SocketsHttpHandler: HttpClientHandler has no such seam.
        //
        // This registration is the one place the outbound cover request can be shaped, which is why
        // the User-Agent hangs off it rather than off the fetch: both the single-apply and the bulk
        // path ask the factory for this client by name, so a header added here covers both at once.
        services.AddHttpClient(TorrentApplyService.CoverHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = CoverAddressPolicy.ConnectAsync,
            })
            .AddHttpMessageHandler(() => new CoverUserAgentHandler(ShippedVersion))
            // Resolved from the provider rather than captured, so the limiter is the registered
            // singleton — and so a test can swap in one driven by a clock it controls.
            .AddHttpMessageHandler(provider =>
                new CoverRateLimitHandler(provider.GetRequiredService<CoverRateLimiter>()));

        // A singleton, because its in-flight map is the whole point and only means anything shared
        // across requests — two tabs, or a preview racing the apply it triggered, are exactly the
        // callers it deduplicates. Everything it sits in front of is a singleton for the same reason.
        services.AddSingleton(provider => new CoverResolver(
            provider.GetRequiredService<CoverPreviewCache>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<CoverHostAllowlist>(),
            provider.GetRequiredService<CoverCache>()));

        services.AddScoped<TorrentMatchService>();
        services.AddScoped<TorrentApplyService>();
        services.AddScoped<TorrentBatchService>();
        services.AddScoped<CoverProxyService>();
        services.AddScoped<WriteFolderService>();
    }

    /// <summary>
    /// The version <c>extension.json</c> declares, or nothing before the host has applied a manifest.
    ///
    /// Read through a catch because <c>Manifest</c> throws until then, and this is called from a
    /// handler factory: letting the exception out would fail the request rather than the header, and
    /// a cover lost to a version lookup would be an absurd trade. An empty answer still produces a
    /// well-formed User-Agent — <see cref="CoverUserAgentHandler.Format"/> substitutes a placeholder
    /// — so the tracker always gets the product token, which is the half they identify us by.
    /// </summary>
    private string ShippedVersion()
    {
        try
        {
            return Version;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Contributes the review dialog. The bundle is hand-written ESM rather than a compiled React
    /// component: the dialog is self-contained and needs no framework, so shipping it this way keeps the
    /// extension buildable with the .NET SDK alone, with no separate frontend toolchain.
    /// </summary>
    public override UIManifest GetUIManifest() => ManifestBuilder()
        .WithJsBundle("ui/main.js")
        // The convenience overload has no permission parameter, so the full definition is used to keep
        // the page behind the same permission its endpoints require.
        // Route is a bare page name, not a path: the router builds the URL as `/${route.page}`, so a
        // leading slash yields "//torrent-metadata" — a protocol-relative URL the browser resolves to
        // a different host. Built-in pages follow the same convention ("videos", "images", …).
        .AddPage(new UIPageDefinition(
            Route: "torrent-metadata",
            Label: "Torrent Matches",
            // Only "music" and "puzzle" exist in the host's ICON_MAP; anything else renders no icon.
            Icon: "puzzle",
            RequiredPermission: Permissions.VideosScrape,
            ComponentName: "TorrentBatchPage"))
        // A tab of our own under Settings → Extensions, rather than one more card stacked on the
        // shared Extensions tab with everyone else's. The key doubles as the route segment after
        // `/settings/`, and the host derives a shorthand alias by dropping the `extensions/` prefix —
        // so the id has to be the second half of it for both to resolve.
        //
        // The host groups every contributed tab under "extensions" regardless, so this is a submenu
        // entry and not a fourth top-level group.
        .AddSettingsTab(
            key: SettingsTabKey,
            label: "Torrent Metadata",
            order: 100,
            // Resolved against the host's own ICON_MAP for settings tabs; anything it does not hold
            // falls back to the generic plug, which is what an untabbed extension already gets.
            icon: "searchcode",
            description: "Where torrents are read from, how new tags are named, and which hosts covers may come from.")
        // Targeted at that tab. A null TargetTab is what puts a panel on the shared Extensions tab —
        // naming a tab the host does not have does not fail, it simply renders the panel on none of
        // them, so this string and the one above must stay the same string.
        //
        // Unlike a page, a settings panel carries no permission of its own — what gates it is whichever
        // tab hosts it, and every candidate is gated the same way: a contributed tab is shown only to a
        // user who may write system settings, and so is the shared Extensions tab a null TargetTab
        // would have put this on. So moving it here changes where the panel appears and not who may
        // see it. The panel still says why it is empty rather than assuming the reader could have
        // loaded it — a load can fail for reasons that have nothing to do with permission.
        //
        // The tag naming style lives here rather than in the review dialog because a control that
        // re-fetches the proposal cannot sit inside the review it would discard.
        .AddSettingsPanel(new UISettingsPanel(
            Id: $"{Id}:naming",
            Label: "Torrent Metadata",
            ExtensionId: Id,
            ComponentName: "TorrentMetadataSettings",
            TargetTab: SettingsTabKey))
        .Build();

    /// <summary>
    /// The settings tab's key, which is also its route segment under <c>/settings/</c>.
    ///
    /// <para>
    /// <c>extensions/</c> plus the manifest id, which is the convention the host's own alias handling
    /// assumes: it lowercases the key and registers the part after <c>extensions/</c> as a shorthand,
    /// so a key shaped any other way resolves from only one of the two URLs.
    /// </para>
    /// </summary>
    private string SettingsTabKey => $"extensions/{Id}";

    /// <summary>The host hands over this extension's key-value store before initialization.</summary>
    public void SetStore(IExtensionStore store)
    {
        _settings.AttachStore(store);
        // The cover cache persists through the same store: a restart must not send us back to the
        // tracker for images already on disk, which is the half of the caching promise a purely
        // in-memory map would not keep.
        _coverCache.AttachStore(store);
        // And the applied baselines, for the same reason: "has this torrent changed since you applied
        // it" is a question about the past, so the answer has to outlive the process.
        _appliedBaseline.AttachStore(store);
    }

    public override async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await _settings.LoadAsync(ct);
        ReloadIndex();
    }

    public IReadOnlyList<ExtensionAction> GetActions() =>
    [
        new ExtensionAction(
            Id: "torrent-metadata.match",
            Label: "Match from torrent",
            ExtensionId: Id,
            // "toolbar", not "context-menu": the host declares three action types but only renders
            // "toolbar" (entity detail pages) and "bulk" (multi-select). A "context-menu" action is
            // accepted by the manifest and then never displayed.
            ActionType: "toolbar",
            EntityTypes: ["video"],
            Icon: "file-search",
            // A JS handler rather than a bare endpoint call: the point of this extension is that the
            // user sees what a torrent proposes and decides, so clicking must open the review dialog
            // instead of writing anything.
            HandlerName: "openTorrentMatchDialog",
            Order: 50)
        {
            RequiredPermission = Permissions.VideosScrape,
            // The handler opens a dialog rather than queueing work, so the host's default
            // "…queued for video" alert would be both wrong and in the way.
            SuppressSuccessAlert = true,
        },
    ];

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Returns a proposal only — nothing is written. Applying the user's selection is a separate
        // endpoint so review always stands between a torrent and the library.
        endpoints.MapPost(MatchEndpoint, async (
            MatchRequest request,
            TorrentMatchService matcher,
            CancellationToken ct) =>
        {
            var outcome = await matcher.MatchAsync(request.EntityId, request.TorrentName, request.FileName, ct);
            return outcome.Status switch
            {
                TorrentMatchStatus.Matched => Results.Ok(outcome.Proposal),
                // Worded like the apply endpoint's, and deliberately says nothing about torrents: a
                // video deleted in another tab is reachable from the shipping UI, and telling that user
                // to rescan their folder sends them to fix the one thing that is not wrong.
                TorrentMatchStatus.VideoNotFound => Results.NotFound(new { error = "Video not found." }),
                _ => Results.NotFound(new { error = "No indexed torrent describes any file of this video." }),
            };
        })
        .RequireCovePermission(Permissions.VideosScrape);

        endpoints.MapPost(ApplyEndpoint, async (
            TorrentApplyRequest request,
            TorrentApplyService applier,
            CancellationToken ct) =>
        {
            TorrentApplyResult? result;
            try
            {
                result = await applier.ApplyAsync(request, ct);
            }
            // Cove folds tag names and aliases into one case-insensitive namespace, does the same for
            // entity names, and enforces both inside SaveChanges — so an apply that would write a
            // second spelling of a name the library already answers to, or a second apply racing this
            // one to create a tag, throws instead of writing. An extension's minimal-API routes get no
            // global exception handler: the host's patches only the MVC controllers, so an
            // uncaught one reaches the browser as a bare 500 with an HTML body, and `readApiResponse`
            // can only answer with its fallback sentence.
            //
            // Nothing was written — the apply is one transaction — so the honest answer is a 409
            // carrying the host's own message, which names the spelling that conflicts. That is the
            // difference between "something went wrong" and a sentence the user can act on.
            catch (Exception conflict) when (conflict is TagNameConflictException or EntityNameConflictException)
            {
                return Results.Json(new { error = conflict.Message }, statusCode: StatusCodes.Status409Conflict);
            }

            return result is null ? Results.NotFound(new { error = "Video not found." }) : Results.Ok(result);
        })
        .RequireCovePermission(Permissions.VideosScrape);

        // The UI's only way to see a torrent's cover. It is a GET with the image URL in the query
        // because an <img> can send nothing else — and for the same reason it authenticates off the
        // same-origin cove_access_token cookie the host sets, exactly like the built-in
        // /api/videos/{id}/image URLs the dialog already renders.
        //
        // Taking a URL does not make it an open proxy: CoverProxyService refuses anything not on the
        // operator's allowlist, which ships empty, so it reaches nowhere an import could not already.
        endpoints.MapGet(CoverEndpoint, async (
            string? url,
            HttpContext http,
            CoverProxyService covers,
            CancellationToken ct) =>
        {
            // Set before the branch below runs, not inside it: nosniff has to hold on the served image
            // *and* on every JSON refusal alike, and putting it only in the success branch is exactly
            // the kind of thing a later refusal branch forgets to copy.
            http.Response.Headers.XContentTypeOptions = "nosniff";

            var result = await covers.GetAsync(url, ct);

            if (result.Bytes is not null && result.ContentType is not null)
            {
                http.Response.Headers.CacheControl = CoverCacheControl;
                // inline, never attachment: the review dialog renders this URL in an <img>, and a
                // browser refuses to display an attachment-disposed response there. The filename is a
                // fixed literal keyed only on the content type this proxy is about to serve — never the
                // remote URL or the remote host's own filename, neither of which is trustworthy enough
                // to put in a header.
                http.Response.Headers.ContentDisposition = $"inline; filename=\"{CoverFilename(result.ContentType)}\"";
                return Results.File(result.Bytes, result.ContentType);
            }

            // The limiter's own advice, passed straight through: it is the only thing that knows
            // whether the wait is a second of pacing or a minute of open breaker, and the retry in
            // the batch page is what turns it back into a thumbnail.
            if (result.RetryAfter is { } retryAfter)
            {
                http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return Results.Json(new { error = result.Error }, statusCode: (int)result.Status);
        })
        .RequireCovePermission(Permissions.VideosScrape);

        // Settings are read and written here rather than through the host's generic extension-data API
        // so the dialog can change them with the same permission it already needs, instead of requiring
        // the broader extensions.configure.
        endpoints.MapGet(SettingsEndpoint, () => Results.Ok(CurrentSettings()))
            .RequireCovePermission(Permissions.VideosScrape);

        // Each field is applied only when the caller sent it. A PUT carrying one setting must not
        // reset the other to its default, which is what a blanket apply would do — the cover-host
        // editor and the tag-style selector are separate controls on the same endpoint.
        //
        // One call rather than three awaited in sequence. The three were independent
        // write-then-assign pairs, so two saves of one setting could reach the store in one order and
        // memory in the other and leave them disagreeing until a restart. `ApplyAsync` holds a single
        // gate across the whole request, and the state it returns is read under that same gate — so
        // the panel's read-back is the document its own save produced rather than a mix of that and
        // whatever landed beside it.
        endpoints.MapPut(SettingsEndpoint, async (SettingsRequest request, CancellationToken ct) =>
        {
            // No reload here on purpose: adding a folder is often two or three edits before the
            // operator settles, and re-reading every torrent in every folder on each of them would
            // charge for work nobody asked for yet. Rescan is the action the page already offers.
            var applied = await _settings.ApplyAsync(
                request.TagNameStyle is null ? null : TagNameStyler.Parse(request.TagNameStyle),
                request.CoverHosts,
                request.SourceFolders,
                ct);

            return Results.Ok(CurrentSettings(applied));
        })
        .RequireCovePermission(Permissions.VideosScrape);

        // What is in the folder we own, so the panel can offer to remove any of it. Listed from the
        // folder rather than the index, which carries neither a path nor the folder an entry came from.
        endpoints.MapGet(WriteFolderEndpoint, async (WriteFolderService folder, CancellationToken ct) =>
            Results.Ok(new
            {
                folder = _torrentFolder,
                torrents = await folder.ListAsync(_torrentFolder, ct),
            }))
            .RequireCovePermission(Permissions.VideosScrape);

        // POST rather than DELETE: a remove names any number of files, and a body is the only place a
        // list of them fits. The panel's bulk button sends every filtered match, not the page of them
        // it happens to be showing.
        endpoints.MapPost(WriteFolderRemoveEndpoint, (WriteFolderRemoveRequest request, CancellationToken _) =>
        {
            var result = WriteFolderService.Remove(_torrentFolder, request.Files ?? []);

            // Reloaded even when nothing was removed: the request only reaches here at all because the
            // user acted on a list, and a list that disagreed with the folder is exactly when they most
            // need the numbers refreshed.
            var report = ReloadIndex();
            return Results.Ok(new
            {
                removed = result.Removed,
                refused = result.Refused,
                torrents = report.Torrents,
                files = _index.Count,
            });
        })
        .RequireCovePermission(Permissions.VideosScrape);

        endpoints.MapGet(BatchEndpoint, async (TorrentBatchService batch, CancellationToken ct) =>
            Results.Ok(await batch.ListAsync(ct)))
            .RequireCovePermission(Permissions.VideosScrape);

        endpoints.MapPost(BatchApplyEndpoint, async (
            BatchApplyRequest request,
            TorrentBatchService batch,
            CancellationToken ct) => Results.Ok(await batch.ApplyAsync(request, ct)))
            .RequireCovePermission(Permissions.VideosScrape);

        // Uploads are deliberately narrow: .torrent only, size-capped, and written into the same watched
        // folder the index already reads, so there is one source of truth rather than two.
        endpoints.MapPost(UploadEndpoint, async (HttpRequest http, CancellationToken ct) =>
        {
            if (!http.HasFormContentType)
                return Results.BadRequest(new { error = "Expected a multipart form upload." });
            if (string.IsNullOrEmpty(_torrentFolder))
                return Results.BadRequest(new { error = "Torrent folder is not configured." });

            var form = await http.ReadFormAsync(ct);
            Directory.CreateDirectory(_torrentFolder);

            var rejected = new List<string>();
            // Keyed by the target path rather than counted per file: two files in one request can
            // reduce to the same base name, and the atomic move below means only the one processed
            // last actually survives on disk. Indexing on `target` means a second entry for the same
            // name replaces the first one here too, so `saved`/`added` — taken from this map's count
            // and values below — describe exactly what is on disk rather than how many files were
            // individually well-formed. The alternative (refusing the second same-name file outright)
            // was rejected: a drag-and-drop of a folder full of re-exports routinely repeats a name,
            // and there is nothing to warn about — the writer already deals in "the current file under
            // this name", same as any other write into this folder.
            var savedFiles = new Dictionary<string, List<object>>(StringComparer.Ordinal);

            // The overflow is refused per file rather than the request as a whole, so a caller that
            // sent too many still keeps what fitted — the same shape as every other refusal in this
            // loop, and the reason `Keeps_the_readable_files_in_a_mixed_upload` exists.
            foreach (var file in form.Files.Skip(MaxTorrentUploadFiles))
                rejected.Add($"{file.FileName}: more than {MaxTorrentUploadFiles} files in one upload");

            foreach (var file in form.Files.Take(MaxTorrentUploadFiles))
            {
                if (!file.FileName.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                {
                    rejected.Add($"{file.FileName}: not a .torrent");
                    continue;
                }

                if (file.Length <= 0 || file.Length > MaxTorrentBytes)
                {
                    rejected.Add($"{file.FileName}: size out of range");
                    continue;
                }

                // Only the base name is used, so an upload cannot write outside the watched folder.
                var baseName = Path.GetFileName(file.FileName);
                var target = Path.Combine(_torrentFolder, baseName);

                // Written under a name neither the index walk (`ReloadIndex`) nor the settings panel's
                // listing (`WriteFolderService`) will ever pick up — both glob `*.torrent` only — and
                // unique per attempt, so two requests uploading the same name at once cannot collide on
                // one temp file. It still names the upload so a person browsing the folder can tell what
                // it is. Only a successful parse-check below earns the atomic rename onto `target`; a bad
                // parse, no video, or a cancelled request is cleaned up in `finally` and `target` is never
                // touched, which is the fix — a corrupt re-upload can no longer destroy the good
                // file already there. The one gap: a hard process crash between the write and the rename
                // leaves this file behind permanently, since nothing scans for it after the fact. That is
                // an acceptable residue rather than a silent loss — it is inert (invisible to both the
                // reload and the removal panel) and named for what it is, so an operator who finds it can
                // delete it by hand with no doubt about what happened.
                var tempPath = Path.Combine(_torrentFolder, $"{baseName}.uploading-{Guid.NewGuid():N}");
                try
                {
                    await using (var stream = File.Create(tempPath))
                    {
                        await file.CopyToAsync(stream, ct);
                    }

                    // Validated from the bytes just written under the temp name, not by re-opening
                    // `target`: checking what this request actually produced, rather than a second, later
                    // read of the live file, is what keeps the check from racing a concurrent reload of
                    // `target` — the file this check is judging cannot be touched by anything else.
                    if (!TorrentRelease.TryRead(await File.ReadAllBytesAsync(tempPath, ct), out var parsed))
                    {
                        rejected.Add($"{file.FileName}: not readable as a torrent");
                        continue;
                    }

                    if (!parsed.HasVideo)
                    {
                        rejected.Add($"{file.FileName}: contains no video");
                        continue;
                    }

                    // `overwrite: true` makes this a rename on Linux, and a rename is atomic: `target` is
                    // either the old file or the new one, in full, never a truncated in-between state.
                    // That atomicity is the whole fix — `File.Create(target)` used to truncate the good
                    // file in place before the parse-check ever ran.
                    File.Move(tempPath, target, overwrite: true);

                    var entries = new List<object>();
                    foreach (var video in parsed.Videos)
                        entries.Add(new { torrentName = parsed.Name, fileName = video.Basename, fanOut = parsed.FanOut });
                    savedFiles[target] = entries;
                }
                finally
                {
                    // Covers a failed parse-check, a request cancelled mid-copy, and any other exception
                    // thrown before the rename: the temp file never becomes visible under its real name,
                    // so there is nothing to delete from the one folder this extension may delete from,
                    // and nothing to explain to the user.
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
            }

            // Identities of what was just added, so a caller can pin a proposal to the file it dropped
            // rather than re-searching and possibly landing on a different torrent. Flattened from
            // `savedFiles` so it only ever names videos that are actually on disk.
            var saved = savedFiles.Count;
            var added = savedFiles.Values.SelectMany(entries => entries).ToList();

            var torrents = ReloadIndex().Torrents;
            return Results.Ok(new { saved, rejected, added, torrents, files = _index.Count });
        })
        .RequireCovePermission(Permissions.VideosScrape)
        .DisableAntiforgery();

        // A GET, and cheap enough to be one: it stats the folders and opens nothing. Kept off
        // `/batch` deliberately — that endpoint lists the whole library against the whole index and
        // took 1.5 s over the measured corpus, and this has to be callable whenever the page comes
        // back into view.
        endpoints.MapGet(FolderStateEndpoint, () =>
        {
            var report = ProbeFolders();
            return Results.Ok(new
            {
                changed = report.Changed,
                // Projected member by member, the same way the reload report is: this shape is the
                // contract `ui/src/folderState.ts` reads, and a property added to the record for some
                // other reason should not silently join it.
                folders = report.Folders.Select(folder => new
                {
                    path = folder.Path,
                    exists = folder.Exists,
                    @checked = folder.Checked,
                    changed = folder.Changed,
                    files = folder.Files,
                    // Which one to name when the page has to tell someone where a torrent goes. Ours
                    // is the only answer: a source folder is read-only and may not even be mounted,
                    // and on a fresh install this is the folder that does not exist yet.
                    writable = SourceFolderSetting.PathComparer.Equals(folder.Path, _torrentFolder),
                }),
                removed = report.Removed,
            });
        })
        .RequireCovePermission(Permissions.VideosScrape);

        endpoints.MapPost(ReloadEndpoint, (CancellationToken _) =>
        {
            var report = ReloadIndex();
            return Results.Ok(new
            {
                torrents = report.Torrents,
                files = _index.Count,
                // Kept for the single-folder callers that predate source folders; it is where uploads
                // land, which is the one folder the page can point a user at for hand-copied files.
                folder = _torrentFolder,
                folders = report.Folders.Select(entry => new
                {
                    path = entry.Path,
                    exists = entry.Exists,
                    torrents = entry.Torrents,
                    // The operator can remove a source; ours is not theirs to move, so the page must be
                    // able to tell them apart rather than offering a Remove button beside all of them.
                    writable = SourceFolderSetting.PathComparer.Equals(entry.Path, _torrentFolder),
                }),
                truncated = report.Truncated,
                // Directories the walk could not open. Top-level rather than inside `skipped`, which
                // counts files: an unopened directory hides an unknown number of them, so summing the
                // two would produce a figure in no unit at all.
                unreadableDirectories = report.UnreadableDirectories,
                // Projected member by member rather than handed over whole, the same way `folders` is:
                // this is a contract `ui/src/reloadStatus.ts` reads, and a property added to the record
                // for some other reason should not silently become part of it.
                skipped = new
                {
                    unreadable = report.Skipped.Unreadable,
                    malformed = report.Skipped.Malformed,
                    withoutVideo = report.Skipped.WithoutVideo,
                    duplicates = report.Skipped.Duplicates,
                    total = report.Skipped.Total,
                    // `Oversized` is deliberately not projected as its own field yet: the JSON shape
                    // here is a contract `ui/src/reloadStatus.ts` reads (`EndpointContractTests` pins
                    // the exact property set), and that module sits outside this fix's scope. It is
                    // folded into `total` in the meantime, so the count is not lost — only its own
                    // reason label is, until the frontend is updated to show it.
                },
            });
        })
        .RequireCovePermission(Permissions.VideosScrape);
    }

    /// <summary>
    /// Every folder the index is built from: the extension's own, then the operator's read-only
    /// sources, in that order.
    ///
    /// Ours comes first so a torrent the user uploaded wins the content de-duplication against the
    /// same file sitting in a source folder — it is the copy this extension is responsible for.
    /// </summary>
    public IReadOnlyList<string> IndexedFolders =>
        string.IsNullOrEmpty(_torrentFolder)
            ? _settings.SourceFolders
            : [_torrentFolder, .. _settings.SourceFolders.Where(folder => !SourceFolderSetting.PathComparer.Equals(folder, _torrentFolder))];

    /// <summary>
    /// Rebuilds the index from every configured folder. A file that cannot be read, cannot be parsed,
    /// holds no video, or repeats one already seen is skipped — but it is *counted* by reason, because
    /// a skipped file otherwise appears in no row and in no number at all, not even the batch page's
    /// unmatched count, which is per indexed video file. That made an unreadable torrent
    /// indistinguishable from one describing a file the user never downloaded, and only the first is
    /// something they can act on.
    ///
    /// Built to the side and swapped in at the end rather than cleared and refilled: this runs from
    /// both the reload and the upload endpoint against a singleton index, so two of them can overlap,
    /// and a scrape can be matching throughout.
    ///
    /// A folder that is not there is reported rather than thrown: a source can live on a drive that is
    /// not always mounted, and one missing folder must not cost the operator the other three.
    /// </summary>
    public IndexReloadReport ReloadIndex()
    {
        // Taken *before* the walk, and the direction matters. A torrent copied in while the walk is
        // running may land in a directory the walk has already passed, so it would be in the index of
        // neither this reload nor the fingerprint if that were captured at the end — silently missing,
        // with the page insisting the folder was unchanged. Capturing first can only over-report: the
        // worst case is a rescan that finds nothing new, which costs a click and lies about nothing.
        //
        // Held locally rather than written to `_lastScan` here. Every exception this walk can
        // throw is meant to be caught below and turned into a skip count, but "meant to" is not
        // "proven to" — an exception that gets past a future edit to this loop must not leave a
        // snapshot in place with no rebuilt index behind it. Assigning the field only once `Replace`
        // has actually happened is what keeps a half-finished reload from making `ProbeFolders` insist
        // the folder is unchanged forever: the field simply keeps whatever it held before, which is the
        // honest answer when this reload did not finish.
        var scan = FolderSignature.Snapshot(IndexedFolders);

        var builder = new TorrentIndexBuilder();
        var folders = new List<FolderReport>();
        // Identity is the file's contents, not its path: the same .torrent in two source folders is one
        // torrent, and indexing it twice would put two identical rows on the batch page and double
        // every count on it.
        //
        // A symlink loop under a source folder is the same question reached by a different route, and
        // is why narrowing this to "one folder against another" would be wrong. `AllDirectories`
        // follows symlinked directories, so a loop yields the *same* file once per level: measured at
        // 41 times on net10.0/Linux, where the walk ends itself at `MAXSYMLINKS` because
        // `EnumerateFiles` skips the `ELOOP` directory rather than throwing. Hashing the contents
        // collapses all 41 to one entry and keeps both reported counts true.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var indexed = 0;
        var truncated = false;
        var unreadableDirectories = 0;
        var unreadable = 0;
        var oversized = 0;
        var malformed = 0;
        var withoutVideo = 0;
        var duplicates = 0;

        foreach (var folder in IndexedFolders)
        {
            if (!Directory.Exists(folder))
            {
                folders.Add(new FolderReport(folder, false, 0));
                continue;
            }

            var fromThisFolder = 0;
            // `TorrentFileWalk` rather than `SearchOption.AllDirectories`, and the difference is the
            // whole point: that overload throws out of the *enumerator* on a subdirectory it cannot
            // open, which is outside every guard below — there is no file yet to guard — so one locked
            // directory aborted the entire reload and the operator was told nothing about which one.
            // The walk skips it, counts it, and keeps going, and `FolderSignature` skips the same one
            // so the fingerprint and the index still describe the same folder.
            var walk = new TorrentFileWalk(folder);
            foreach (var file in walk.Files())
            {
                if (indexed >= MaxIndexedTorrents)
                {
                    truncated = true;
                    break;
                }

                // Checked before the read rather than after, so a huge file costs a stat rather than an
                // allocation the size of the file. `UnauthorizedAccessException` is caught here
                // too — a permission error can surface either at the stat or at the open, and both mean
                // the same thing to the operator: this file could not be read.
                long length;
                try
                {
                    length = file.Length;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    unreadable++;
                    continue;
                }

                if (length > MaxTorrentBytes)
                {
                    oversized++;
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(file.FullName);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // `UnauthorizedAccessException` derives from `SystemException`, not `IOException`,
                    // and a permission error is the most likely reason a .torrent in an operator-chosen
                    // folder cannot be read. Catching only `IOException` let it escape this
                    // method entirely — which, before the fix above, would have left `_lastScan`
                    // pointing at a snapshot with no rebuilt index behind it.
                    unreadable++;
                    continue;
                }

                if (!seen.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))))
                {
                    duplicates++;
                    continue;
                }

                // Split rather than short-circuited into one condition, which is how these two were
                // written and why they could not be told apart. A torrent holding no video is the
                // normal case — image sets, comics and audio-only releases are what `HasVideo` exists
                // for — and one that will not parse is not. Reporting them as a single number would
                // put the routine alongside the broken and describe neither.
                if (!TorrentRelease.TryRead(bytes, out var torrent))
                {
                    malformed++;
                    continue;
                }

                if (!builder.Add(torrent))
                {
                    withoutVideo++;
                    continue;
                }

                indexed++;
                fromThisFolder++;
            }

            unreadableDirectories += walk.Inaccessible;
            folders.Add(new FolderReport(folder, true, fromThisFolder));
            if (truncated)
                break;
        }

        _index.Replace(builder);
        // Only now: see the comment on `scan` above. A reload that reaches this line has actually
        // rebuilt the index the snapshot describes.
        _lastScan = scan;
        return new IndexReloadReport(
            indexed,
            folders,
            truncated,
            new IndexSkipCounts(unreadable, oversized, malformed, withoutVideo, duplicates),
            unreadableDirectories);
    }

    /// <summary>One folder's contribution to the index, so the page can say which one is empty or gone.</summary>
    /// <param name="Exists">False when the folder is not there — a source on an unmounted drive, or a
    /// path that has since been renamed. Reported rather than treated as an error.</param>
    public sealed record FolderReport(string Path, bool Exists, int Torrents);

    /// <summary>
    /// Files the walk passed over, by reason.
    ///
    /// Counts rather than paths, deliberately. A list would be the more useful answer and is a
    /// different decision: it grows with the folder, and the measured corpus is 3,218 torrents in one
    /// source. The number is what turns silence into a statement, and it is what the rescan line has
    /// room for.
    /// </summary>
    /// <param name="Unreadable">The file could not be read at all — a permission, a device or a path
    /// that went away mid-walk. Distinct from <paramref name="Malformed"/> because the fix is.</param>
    /// <param name="Oversized">Bigger than <c>MaxTorrentBytes</c> and skipped before it was even
    /// opened. Distinct from <paramref name="Unreadable"/> for the same reason that one is
    /// distinct from <paramref name="Malformed"/>: the file could have been read, and refusing to is a
    /// choice this extension made, not a filesystem failure.</param>
    /// <param name="Malformed">Read, but not parseable as bencode.</param>
    /// <param name="WithoutVideo">A valid torrent carrying no video: an image set, a comic, an
    /// audio-only release. Routine, and counted only so it is not mistaken for one of the others.</param>
    /// <param name="Duplicates">The same bytes as a file already indexed, most often the same torrent
    /// sitting in two source folders — or the same one reached repeatedly through a symlink loop.</param>
    public sealed record IndexSkipCounts(int Unreadable, int Oversized, int Malformed, int WithoutVideo, int Duplicates)
    {
        /// <summary>Every file the walk saw and did not index.</summary>
        public int Total => Unreadable + Oversized + Malformed + WithoutVideo + Duplicates;
    }

    /// <summary>What a reload found, per folder as well as in total.</summary>
    /// <param name="Truncated">True when <c>MaxIndexedTorrents</c> was reached and the rest of the
    /// folders were not read. Surfaced so a short index reads as a cap rather than as torrents that
    /// stopped matching.</param>
    /// <param name="Skipped">Files the walk did not index, by reason. The cap is not among them: it is
    /// <paramref name="Truncated"/>, because it stops the walk rather than passing over a file.</param>
    /// <param name="UnreadableDirectories">Directories the walk could not open, so nothing under them
    /// was seen at all — a permission, a share that went away, or a symlink loop.
    ///
    /// Held apart from <paramref name="Skipped"/> rather than added to it, because that record counts
    /// *files* and this counts directories, with an unknown number of files behind each. Adding them
    /// would make a single number that means neither, which is the unit mix-up exactly.
    ///
    /// It is not a skip reason in the other sense either: a skipped file was seen and passed over,
    /// while these are the walk saying there is part of the folder it cannot describe.</param>
    public sealed record IndexReloadReport(
        int Torrents,
        IReadOnlyList<FolderReport> Folders,
        bool Truncated,
        IndexSkipCounts Skipped,
        int UnreadableDirectories);

    /// <summary>
    /// Whether the folders have changed since the index was last built.
    ///
    /// The question this answers is narrow on purpose: *has the disk moved*, not *would a rescan
    /// change the index*. The two differ under <c>MaxIndexedTorrents</c> and under every skip reason,
    /// so a user can act on this and see the counts stay where they were. Answering the stronger
    /// question means reading and parsing every file, which is the rescan itself.
    ///
    /// It compares a fingerprint against a fingerprint. It must never compare files on disk against
    /// torrents indexed: <see cref="FolderReport.Torrents"/> is a post-skip count, so a folder holding
    /// three image-set torrents would read as permanently behind and no rescan would ever settle it —
    /// the same unit mix-up, reached from a new direction.
    /// </summary>
    public FolderChangeReport ProbeFolders()
    {
        var baseline = _lastScan;
        // Read once: the property rebuilds the list on every access, and the removal check below asks
        // it for each folder the last scan recorded.
        var configured = IndexedFolders;
        var folders = FolderSignature.Snapshot(configured)
            .Select(current =>
            {
                var previous = baseline.FirstOrDefault(
                    entry => SourceFolderSetting.PathComparer.Equals(entry.Path, current.Path));

                // A folder the last scan never saw is changed by that fact alone, whether or not it
                // could be swept just now — it was configured since, and nothing in it is indexed.
                // Settings deliberately do not rebuild the index, so this is how the operator
                // learns that the folder they just added is waiting on a rescan.
                return new FolderChange(
                    current.Path,
                    current.Exists,
                    current.Checked,
                    Changed: previous is null || current.DiffersFrom(previous),
                    current.Files);
            })
            .ToList();

        // A source dropped from the settings leaves its torrents in the index until the next rebuild,
        // which is a staleness with no folder left to report it against. Named separately because the
        // fix reads backwards: rescanning *removes* rows rather than adding them.
        var removed = baseline
            .Where(entry => !configured.Any(folder => SourceFolderSetting.PathComparer.Equals(folder, entry.Path)))
            .Select(entry => entry.Path)
            .ToList();

        return new FolderChangeReport(
            folders.Exists(folder => folder.Changed) || removed.Count > 0,
            folders,
            removed);
    }

    /// <summary>One folder's answer to "has this moved since the last scan?".</summary>
    /// <param name="Checked">False when the folder could not be swept. <paramref name="Changed"/> is
    /// then false as well unless the folder is new — not knowing is not the same as unchanged, and the
    /// page says so rather than staying silent.</param>
    /// <param name="Files">How many <c>.torrent</c> files the sweep saw. Carried because the stat that
    /// produced it is 8 ms where reading and parsing the same folder is a second or more, so this is
    /// the only number the settings panel can have *before* the listing it is waiting on. Zero
    /// for a folder that is missing or could not be swept — it is a count of what was seen, not a
    /// claim about what is there.</param>
    public sealed record FolderChange(string Path, bool Exists, bool Checked, bool Changed, int Files);

    /// <summary>What a folder probe found, per folder and in total.</summary>
    /// <param name="Removed">Folders the last scan read that are no longer configured. Their torrents
    /// are still indexed.</param>
    public sealed record FolderChangeReport(
        bool Changed,
        IReadOnlyList<FolderChange> Folders,
        IReadOnlyList<string> Removed);

    /// <summary>Indexes an already-parsed torrent. Used by tests and by future ingest paths.</summary>
    public bool AddToIndex(TorrentRelease torrent) => _index.Add(torrent);

    /// <summary>The entity-action payload Cove posts; <c>entityId</c> is the video.</summary>
    /// <summary>
    /// Entity-action payload. <c>TorrentName</c>/<c>FileName</c> pin the proposal to a specific indexed
    /// torrent instead of searching by file size.
    /// </summary>
    public sealed record MatchRequest(int EntityId, string? EntityType, string? TorrentName, string? FileName);

    /// <summary>
    /// Settings update payload. A null member means "leave this one alone", not "reset it" — see the
    /// PUT handler.
    /// </summary>
    public sealed record SettingsRequest(string? TagNameStyle, List<string>? CoverHosts, List<string>? SourceFolders);

    /// <summary>
    /// Files to remove from the extension's own folder, named the way the listing names them —
    /// relative to that folder. Any name resolving outside it is refused per file.
    /// </summary>
    public sealed record WriteFolderRemoveRequest(List<string>? Files);

    /// <summary>
    /// The settings as the UI reads them. One shape, so GET and PUT cannot drift.
    /// </summary>
    /// <param name="state">
    /// The state to describe. A PUT passes the one its own save produced, read under the write gate;
    /// a GET passes nothing and gets whatever is current. Either way it is a single
    /// <see cref="TorrentMetadataSettings.State"/> rather than three properties read one after
    /// another, so the three fields below always belong to the same moment.
    /// </param>
    private object CurrentSettings(TorrentMetadataSettings.State? state = null)
    {
        var settings = state ?? _settings.Current;

        return new
        {
            tagNameStyle = TagNameStyler.Serialize(settings.TagNameStyle),
            coverHosts = settings.CoverHosts,
            sourceFolders = settings.SourceFolders,
            // Read-only, and sent so the panel can name the folder hand-copied torrents belong in. It
            // is not in `sourceFolders` because it is not the operator's to remove.
            writeFolder = _torrentFolder,
        };
    }
}
