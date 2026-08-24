using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Data;
using Cove.TorrentMetadata;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// Drives the six endpoints <c>UploadEndpointTests</c> does not, and asserts the permission
/// metadata on all seven.
///
/// Two different things are being protected here.
///
/// The response shapes are a **contract with <c>ui/src/api.ts</c>**, whose interfaces are hand-typed
/// against C# property names with nothing checking they agree. Renaming a property is a silent
/// frontend break, and not even a 404 — <c>api.ts</c> records that unmapped <c>/api/*</c> paths fall
/// through to the SPA <c>index.html</c>, so the caller gets HTML and dies on <c>Unexpected token
/// '&lt;'</c>. Each shape is asserted as an exact property-name set rather than a spot check: a
/// rename fails it, and so does a C# property the UI has never been told about, which is the drift
/// worth hearing about early.
///
/// The permission assertions read endpoint **metadata** rather than driving HTTP and expecting 403.
/// <c>RequireCovePermission</c> only attaches <c>CovePermissionRequirementMetadata</c>
/// (<c>Cove.Sdk/EndpointAuthorizationExtensions.cs</c>); enforcement lives in the host, in
/// <c>ExtensionManager</c>, which this test server does not run. An HTTP-status test would therefore
/// be untestable here rather than merely weak — and the metadata is the thing that actually goes
/// missing in a refactor. It matters because the host treats absence as *allow*: an extension
/// endpoint carrying no Cove authorization metadata is logged as a warning and then served
/// anonymously "for backward compatibility". Dropping the convention from <c>/apply</c> or
/// <c>/batch/apply</c> does not fail loudly, it opens a library write to anyone.
/// </summary>
public class EndpointContractTests
{
    private const string Base = "/api/extensions/torrent-metadata";
    private const string MatchUrl = $"{Base}/match";
    private const string ApplyUrl = $"{Base}/apply";
    private const string SettingsUrl = $"{Base}/settings";
    private const string BatchUrl = $"{Base}/batch";
    private const string BatchApplyUrl = $"{Base}/batch/apply";
    private const string ReloadUrl = $"{Base}/reload";
    private const string FolderStateUrl = $"{Base}/folder-state";
    private const string UploadUrl = $"{Base}/upload";
    private const string CoverUrl = $"{Base}/cover";
    private const string WriteFolderUrl = $"{Base}/write-folder";
    private const string WriteFolderRemoveUrl = $"{Base}/write-folder/remove";

    private const long VideoSize = 5_387_499_251L;
    private const string VideoFile = "sample-scene.mp4";

    // ---------------------------------------------------------------------
    // Permission gating
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Every_endpoint_declares_the_permission_the_host_gates_on()
    {
        await using var host = await StartAsync();

        var routes = host.MappedRoutes();

        // All eleven, named individually: a loop over "whatever got mapped" would still pass if an
        // endpoint disappeared entirely.
        // SettingsUrl twice: GET and PUT are two endpoints on one pattern.
        //
        // /cover is the one whose permission matters most. It is reached by an <img> rather than by
        // api.ts, so it authenticates off the session cookie — and the host serves an extension
        // endpoint carrying no Cove authorization metadata *anonymously*, which on this endpoint
        // would mean anyone could drive the Cove server's outbound requests, allowlist or not.
        Assert.Equal(
            new[]
            {
                ApplyUrl, BatchUrl, BatchApplyUrl, CoverUrl, FolderStateUrl, MatchUrl, ReloadUrl,
                SettingsUrl, SettingsUrl, UploadUrl, WriteFolderUrl, WriteFolderRemoveUrl,
            }.Order(),
            routes.Select(route => route.Pattern).Order());

        foreach (var (pattern, endpoint) in routes)
        {
            var permission = endpoint.Metadata.GetMetadata<CovePermissionRequirementMetadata>();
            Assert.True(permission is not null, $"{pattern} declares no Cove permission, so the host serves it anonymously.");
            Assert.Contains(Permissions.VideosScrape, permission!.Permissions);
        }
    }

    // ---------------------------------------------------------------------
    // Settings
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Settings_reports_the_current_tag_name_style()
    {
        await using var host = await StartAsync();

        var body = await GetJsonAsync(host, SettingsUrl);

        AssertProperties(body, "tagNameStyle", "coverHosts", "sourceFolders", "writeFolder");
        Assert.Equal("titlecase", body.GetProperty("tagNameStyle").GetString());
        // Ships empty, so covers do not import until an operator names their tracker's image hosts.
        Assert.Empty(body.GetProperty("coverHosts").EnumerateArray());
    }

    [Fact]
    public async Task Settings_round_trips_a_new_style_and_answers_with_it()
    {
        await using var host = await StartAsync();

        var response = await host.Client.PutAsJsonAsync(SettingsUrl, new { tagNameStyle = "dotted" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The dialog writes the style and re-renders from the response, so PUT answering with the
        // stored value rather than an empty 200 is the contract, not a convenience.
        AssertProperties(body, "tagNameStyle", "coverHosts", "sourceFolders", "writeFolder");
        Assert.Equal("dotted", body.GetProperty("tagNameStyle").GetString());
        Assert.Equal("dotted", (await GetJsonAsync(host, SettingsUrl)).GetProperty("tagNameStyle").GetString());
    }

    [Fact]
    public async Task Settings_round_trips_the_cover_hosts_and_normalises_them()
    {
        await using var host = await StartAsync();

        var response = await host.Client.PutAsJsonAsync(
            SettingsUrl, new { coverHosts = new[] { "https://img.example/cover.jpg", "other.example" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Answered with the stored value, like the style is, because the editor re-renders from the
        // response — and the value it renders is the cleaned one, so the user sees what was kept.
        Assert.Equal(
            ["img.example", "other.example"],
            body.GetProperty("coverHosts").EnumerateArray().Select(host => host.GetString()));
    }

    [Fact]
    public async Task Settings_leaves_the_setting_it_was_not_sent_alone()
    {
        await using var host = await StartAsync();

        await host.Client.PutAsJsonAsync(SettingsUrl, new { tagNameStyle = "dotted" });
        await host.Client.PutAsJsonAsync(SettingsUrl, new { coverHosts = new[] { "img.example" } });

        // Two independent controls on one endpoint. A blanket apply would read the absent tagNameStyle
        // as null, parse that to the default, and silently reset a setting the user never touched.
        var body = await GetJsonAsync(host, SettingsUrl);
        Assert.Equal("dotted", body.GetProperty("tagNameStyle").GetString());
        Assert.Equal("img.example", Assert.Single(body.GetProperty("coverHosts").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task A_style_set_in_one_session_is_what_the_next_one_starts_with()
    {
        // One store handed to two hosts: the host gives each loaded extension the same per-extension
        // store, so this is a restart.
        var store = new FakeExtensionStore();

        await using (var first = await StartAsync(store))
        {
            var response = await first.Client.PutAsJsonAsync(SettingsUrl, new { tagNameStyle = "dotted" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await using var second = await StartAsync(store);
        await second.Extension.InitializeAsync(second.Services);

        // The assertion this file exists for. Without the store write and the startup read agreeing, the
        // setting works for the rest of the session and silently reverts to titlecase on restart —
        // a failure that surfaces long after the change that caused it.
        Assert.Equal("dotted", (await GetJsonAsync(second, SettingsUrl)).GetProperty("tagNameStyle").GetString());
    }

    // ---------------------------------------------------------------------
    // Match
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Match_answers_with_every_field_the_review_dialog_reads()
    {
        await using var host = await StartAsync();
        host.Extension.AddToIndex(SampleTorrent());
        var videoId = await host.SeedVideoAsync();

        var response = await host.Client.PostAsJsonAsync(MatchUrl, new { entityId = videoId, entityType = "video" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        AssertProperties(
            body,
            "videoId", "torrentName", "fileName", "matchedOn", "fanOut", "torrentTagCount",
            "title", "date", "studioName", "studioChoices", "studioMatchCount",
            "coverUrl", "coverHostAllowed", "url", "torrentId", "videoHasImage", "currentTitle", "currentDate",
            "currentStudioName", "currentUrls", "tags", "performers",
            "tagNameStyle");

        // The relation shape is a contract too: MatchDialog keys its rows on `name` and renders a
        // badge from `matchesExisting` / `alreadyApplied`.
        AssertProperties(body.GetProperty("tags").EnumerateArray().First(), "name", "source", "matchesExisting", "alreadyApplied");
    }

    [Fact]
    public async Task Match_answers_not_found_with_an_error_the_client_can_show()
    {
        await using var host = await StartAsync();
        var videoId = await host.SeedVideoAsync();

        var response = await host.Client.PostAsJsonAsync(MatchUrl, new { entityId = videoId, entityType = "video" });

        // `send` in api.ts reads `error` off a non-OK body and throws it as the message, falling back
        // to a bare status. A differently named field degrades every failure to "Request failed (404)".
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Match_says_a_missing_video_is_missing_rather_than_unmatched()
    {
        await using var host = await StartAsync();
        var videoId = await host.SeedVideoAsync();

        var unmatched = await host.Client.PostAsJsonAsync(MatchUrl, new { entityId = videoId, entityType = "video" });
        var missing = await host.Client.PostAsJsonAsync(MatchUrl, new { entityId = 987654, entityType = "video" });

        // Both are 404s, and both used to carry the same sentence: a video deleted in another tab told
        // the user their torrent folder had nothing for it, which sends them to rescan a folder that was
        // never the problem. The status code cannot separate them — only the text can.
        Assert.Equal(HttpStatusCode.NotFound, unmatched.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var unmatchedError = (await unmatched.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString();
        var missingError = (await missing.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString();

        Assert.NotEqual(unmatchedError, missingError);
        // And the one about a missing video says nothing about torrents, or it is the same wrong
        // instruction in different words.
        Assert.DoesNotContain("torrent", missingError!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------
    // Apply
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Apply_answers_with_every_counter_the_dialog_summarises()
    {
        await using var host = await StartAsync();
        var videoId = await host.SeedVideoAsync();

        var response = await host.Client.PostAsJsonAsync(ApplyUrl, new { videoId, tags = new[] { "brand new tag" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        AssertProperties(
            body,
            // No `performersCreated`. This path links performers the library already holds and has no
            // way to invent one, so a counter for it could only ever report zero.
            "tagsAdded", "tagsCreated", "performersAdded", "aliasesSeeded",
            "titleChanged", "dateChanged", "studioChanged", "urlAdded", "coverChanged",
            "coverSkipped");
        Assert.Equal(1, body.GetProperty("tagsAdded").GetInt32());
    }

    /// <summary>
    /// A name Cove refuses reaches the reviewer as a sentence rather than as a bare 500.
    ///
    /// Tag names and aliases are one case-insensitive namespace enforced inside SaveChanges, so an
    /// apply that would write a second spelling of a name the library already answers to — or a second
    /// apply racing this one to create the same tag — throws. An extension's minimal-API routes get no
    /// global exception handler: the host's patches only the MVC controllers, so an
    /// uncaught one arrives as a 500 with an HTML body, and `readApiResponse` can then only show its
    /// own fallback wording. The apply is one transaction, so nothing was written and 409 is the
    /// honest status.
    /// </summary>
    [Fact]
    public async Task Apply_answers_conflict_with_the_hosts_own_sentence_when_a_name_is_refused()
    {
        await using var host = await StartAsync(refusesTheTagName: true);
        var videoId = await host.SeedVideoAsync();

        var response = await host.Client.PostAsJsonAsync(ApplyUrl, new { videoId, tags = new[] { "kissing" } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // JSON, because the frontend checks the content type before it parses — an HTML body is
        // indistinguishable from the SPA fallback and loses the message entirely.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("kissing", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Apply_answers_not_found_for_a_video_that_does_not_exist()
    {
        await using var host = await StartAsync();

        var response = await host.Client.PostAsJsonAsync(ApplyUrl, new { videoId = 4242, tags = new[] { "x" } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    // ---------------------------------------------------------------------
    // Batch
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Batch_answers_with_the_overview_and_row_shape_the_page_renders()
    {
        await using var host = await StartAsync();
        host.Extension.AddToIndex(SampleTorrent());
        await host.SeedVideoAsync();

        var body = await GetJsonAsync(host, BatchUrl);

        AssertProperties(body, "rows", "unmatched", "videosMatchableByName", "indexedFiles", "torrents");
        AssertProperties(
            body.GetProperty("rows").EnumerateArray().First(),
            "torrentName", "fileName", "torrentId", "fanOut", "status", "videoId", "videoTitle",
            "videoHasImage", "videoTagCount", "tagsToAdd", "tagsToCreate", "performersToAdd", "torrentCoverUrl",
            "torrentCoverAllowed");
    }

    [Fact]
    public async Task Batch_apply_answers_with_the_counters_the_page_sums_and_nothing_else()
    {
        await using var host = await StartAsync();
        host.Extension.AddToIndex(SampleTorrent());
        var videoId = await host.SeedVideoAsync();

        var response = await host.Client.PostAsJsonAsync(BatchApplyUrl, new
        {
            videoIds = new[] { videoId },
            createNewTags = true,
            includePacks = false,
            importCovers = false,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Exact, so a counter the page never reads cannot sit here unnoticed — which is how `skipped`
        // survived being both wrong and dead for a while. Every name below is folded by `bulkApply.ts`.
        //
        // The last three are the failure half. `stoppedEarly` is the one that has to be here
        // rather than stay server-side: the page applies in chunks, so a breaker the caller cannot
        // read trips once per chunk and lets the run walk the whole selection anyway.
        AssertProperties(
            body,
            "videosTouched", "tagsAdded", "tagsCreated", "performersAdded", "aliasesSeeded",
            "coversImported", "coversSkipped", "coverSkipReason",
            "rowsFailed", "failureReason", "stoppedEarly");
        Assert.Equal(1, body.GetProperty("videosTouched").GetInt32());
    }

    // ---------------------------------------------------------------------
    // Reload
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Reload_reports_what_it_read_and_the_folder_it_read_from()
    {
        await using var host = await StartAsync();
        await File.WriteAllBytesAsync(
            Path.Combine(host.Folder, "sample.torrent"),
            TorrentBytes.SingleFile(VideoFile, VideoSize));

        var response = await host.Client.PostAsync(ReloadUrl, content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        AssertProperties(
            body, "torrents", "files", "folder", "folders", "truncated", "unreadableDirectories", "skipped");
        Assert.Equal(1, body.GetProperty("torrents").GetInt32());
        Assert.Equal(1, body.GetProperty("files").GetInt32());
        Assert.Equal(host.Folder, body.GetProperty("folder").GetString());

        // Per folder as well as in total, so the page can name one that is missing rather than showing
        // a smaller number with no explanation.
        var reported = Assert.Single(body.GetProperty("folders").EnumerateArray());
        AssertProperties(reported, "path", "exists", "torrents", "writable");
        Assert.Equal(host.Folder, reported.GetProperty("path").GetString());
        Assert.True(reported.GetProperty("exists").GetBoolean());
        // The extension's own folder is the one place it writes, and the panel must not offer to
        // remove it beside the operator's own sources.
        Assert.True(reported.GetProperty("writable").GetBoolean());
    }

    [Fact]
    public async Task Reload_reads_a_source_folder_as_well_as_its_own()
    {
        await using var host = await StartAsync();
        var source = Directory.CreateDirectory(Path.Combine(host.Folder, "..", "elsewhere")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(host.Folder, "ours.torrent"), TorrentBytes.SingleFile(VideoFile, VideoSize));
        await File.WriteAllBytesAsync(Path.Combine(source, "theirs.torrent"), TorrentBytes.SingleFile("other.mp4", VideoSize + 1));

        await host.Client.PutAsJsonAsync(SettingsUrl, new { sourceFolders = new[] { source } });
        var body = await (await host.Client.PostAsync(ReloadUrl, content: null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, body.GetProperty("torrents").GetInt32());
        var folders = body.GetProperty("folders").EnumerateArray().ToArray();
        Assert.Equal(2, folders.Length);
        // Ours first, so it wins the content de-duplication against a copy in a source folder.
        Assert.Equal(host.Folder, folders[0].GetProperty("path").GetString());
        Assert.False(folders[1].GetProperty("writable").GetBoolean());
    }

    [Fact]
    public async Task Reload_reports_what_it_skipped_by_reason_and_not_only_what_it_indexed()
    {
        await using var host = await StartAsync();
        var source = Directory.CreateDirectory(Path.Combine(host.Folder, "..", "second-source")).FullName;

        // One of each, so no two counters can be satisfied by the same file. The duplicate is the
        // realistic one: the same .torrent sitting in two configured folders.
        var shared = TorrentBytes.SingleFile(VideoFile, VideoSize);
        await File.WriteAllBytesAsync(Path.Combine(host.Folder, "ours.torrent"), shared);
        await File.WriteAllBytesAsync(Path.Combine(source, "same-again.torrent"), shared);
        await File.WriteAllBytesAsync(Path.Combine(host.Folder, "broken.torrent"), "not bencode"u8.ToArray());
        await File.WriteAllBytesAsync(
            Path.Combine(host.Folder, "gallery.torrent"),
            TorrentBytes.MultiFile("Photo Set", ("Photo Set/001.jpg", 900_000L)));

        await host.Client.PutAsJsonAsync(SettingsUrl, new { sourceFolders = new[] { source } });
        var body = await (await host.Client.PostAsync(ReloadUrl, content: null)).Content.ReadFromJsonAsync<JsonElement>();

        // Exact, because `ui/src/reloadStatus.ts` reads these names and reports a total it did not
        // account for. A reason added on one side and not the other is the drift this catches.
        var skipped = body.GetProperty("skipped");
        AssertProperties(skipped, "unreadable", "malformed", "withoutVideo", "duplicates", "total");
        Assert.Equal(1, body.GetProperty("torrents").GetInt32());
        Assert.Equal(0, skipped.GetProperty("unreadable").GetInt32());
        Assert.Equal(1, skipped.GetProperty("malformed").GetInt32());
        Assert.Equal(1, skipped.GetProperty("withoutVideo").GetInt32());
        Assert.Equal(1, skipped.GetProperty("duplicates").GetInt32());
        Assert.Equal(3, skipped.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Reload_reports_a_source_folder_that_is_not_there_rather_than_failing()
    {
        await using var host = await StartAsync();
        await File.WriteAllBytesAsync(Path.Combine(host.Folder, "ours.torrent"), TorrentBytes.SingleFile(VideoFile, VideoSize));
        var missing = Path.Combine(host.Folder, "..", "not-mounted");

        await host.Client.PutAsJsonAsync(SettingsUrl, new { sourceFolders = new[] { missing } });
        var response = await host.Client.PostAsync(ReloadUrl, content: null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // A source can live on a drive that is not always mounted. One folder being gone must not cost
        // the operator the others, and it must be *said* — silence reads as a folder holding nothing.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, body.GetProperty("torrents").GetInt32());
        var reported = body.GetProperty("folders").EnumerateArray().Last();
        Assert.False(reported.GetProperty("exists").GetBoolean());
    }

    // ---------------------------------------------------------------------
    // Folder state
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Folder_state_reports_a_folder_the_index_has_not_read_yet()
    {
        await using var host = await StartAsync();
        var source = Directory.CreateDirectory(Path.Combine(host.Folder, "..", "added-later")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(host.Folder, "ours.torrent"), TorrentBytes.SingleFile(VideoFile, VideoSize));
        await host.Client.PostAsync(ReloadUrl, content: null);

        await File.WriteAllBytesAsync(Path.Combine(source, "theirs.torrent"), TorrentBytes.SingleFile("other.mp4", VideoSize + 1));
        await host.Client.PutAsJsonAsync(SettingsUrl, new { sourceFolders = new[] { source } });

        var body = await GetJsonAsync(host, FolderStateUrl);

        // Writing settings deliberately does not rebuild the index — the folders take effect on the
        // next rescan — so a source can be configured with nothing of its indexed. This endpoint
        // is the only thing that tells the operator a rescan is owed.
        AssertProperties(body, "changed", "folders", "removed");
        Assert.True(body.GetProperty("changed").GetBoolean());
        var folders = body.GetProperty("folders").EnumerateArray().ToArray();
        AssertProperties(folders[0], "path", "exists", "checked", "changed", "writable", "files");
        Assert.False(folders[0].GetProperty("changed").GetBoolean());
        // Ours first, and the only one flagged: it is what the empty batch page names when it has to
        // say where a hand-copied torrent goes, and a source folder is not somewhere we may write.
        Assert.True(folders[0].GetProperty("writable").GetBoolean());
        // The count the settings panel says out loud while it waits for the listing.
        Assert.Equal(1, folders[0].GetProperty("files").GetInt32());
        Assert.False(folders[1].GetProperty("writable").GetBoolean());
        Assert.Equal(source, folders[1].GetProperty("path").GetString());
        Assert.True(folders[1].GetProperty("changed").GetBoolean());
        Assert.Empty(body.GetProperty("removed").EnumerateArray());
    }

    [Fact]
    public async Task Folder_state_goes_quiet_once_the_index_has_been_rebuilt()
    {
        await using var host = await StartAsync();
        await host.Client.PostAsync(ReloadUrl, content: null);
        await File.WriteAllBytesAsync(Path.Combine(host.Folder, "ours.torrent"), TorrentBytes.SingleFile(VideoFile, VideoSize));
        Assert.True((await GetJsonAsync(host, FolderStateUrl)).GetProperty("changed").GetBoolean());

        await host.Client.PostAsync(ReloadUrl, content: null);

        // The rescan is what settles it, and it is the only thing that does: a probe that reset the
        // baseline would clear the notice by reading it, leaving the next visit claiming a folder was
        // up to date while the index had never read the file.
        Assert.False((await GetJsonAsync(host, FolderStateUrl)).GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task Reload_indexes_the_same_torrent_in_two_folders_once()
    {
        await using var host = await StartAsync();
        var source = Directory.CreateDirectory(Path.Combine(host.Folder, "..", "copy")).FullName;
        var bytes = TorrentBytes.SingleFile(VideoFile, VideoSize);
        await File.WriteAllBytesAsync(Path.Combine(host.Folder, "sample.torrent"), bytes);
        // Same contents, different name and folder — what happens when someone keeps a backup copy.
        await File.WriteAllBytesAsync(Path.Combine(source, "sample-copy.torrent"), bytes);

        await host.Client.PutAsJsonAsync(SettingsUrl, new { sourceFolders = new[] { source } });
        var body = await (await host.Client.PostAsync(ReloadUrl, content: null)).Content.ReadFromJsonAsync<JsonElement>();

        // Identity is the file's contents, not its path. Indexing it twice would put two identical
        // rows on the batch page and double every count on it.
        //
        // Two folders is the case that is testable everywhere; it is not the only one this covers. A
        // symlink loop under a single source folder yields one file 41 times and is collapsed by the
        // same hash — untested here on purpose, because creating a loop needs symlink
        // permissions a Windows contributor may not have, and xunit v2 has no working dynamic skip,
        // so the test would fail rather than stand aside. Do not narrow this to a cross-folder check.
        Assert.Equal(1, body.GetProperty("torrents").GetInt32());
        Assert.Equal(1, body.GetProperty("files").GetInt32());
    }

    [Fact]
    public async Task Reload_skips_a_file_it_cannot_parse_instead_of_failing()
    {
        await using var host = await StartAsync();
        await File.WriteAllBytesAsync(Path.Combine(host.Folder, "good.torrent"), TorrentBytes.SingleFile(VideoFile, VideoSize));
        await File.WriteAllTextAsync(Path.Combine(host.Folder, "junk.torrent"), "not bencode at all");

        var body = await (await host.Client.PostAsync(ReloadUrl, content: null)).Content.ReadFromJsonAsync<JsonElement>();

        // A watched folder legitimately holds torrents this extension cannot use. One unreadable file
        // must not cost the user the rest of the folder.
        Assert.Equal(1, body.GetProperty("torrents").GetInt32());
    }

    [Fact]
    public async Task Reload_does_not_count_a_torrent_that_holds_no_video()
    {
        await using var host = await StartAsync();
        await File.WriteAllBytesAsync(
            Path.Combine(host.Folder, "comic.torrent"),
            TorrentBytes.SingleFile("scan-001.cbz", 4242L));

        var body = await (await host.Client.PostAsync(ReloadUrl, content: null)).Content.ReadFromJsonAsync<JsonElement>();

        // Image sets and comics parse fine and index nothing, so counting them would report a folder
        // as usable when none of it can ever match.
        Assert.Equal(0, body.GetProperty("torrents").GetInt32());
        Assert.Equal(0, body.GetProperty("files").GetInt32());
    }

    // ---------------------------------------------------------------------
    // Initialization
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Initialize_loads_settings_and_indexes_the_folder_it_was_given()
    {
        await using var host = await StartAsync();
        await File.WriteAllBytesAsync(
            Path.Combine(host.Folder, "sample.torrent"),
            TorrentBytes.SingleFile(VideoFile, VideoSize));

        await host.Extension.InitializeAsync(host.Services);

        // The host calls this once at startup, and it is the only thing that fills the index before a
        // user asks for anything. Without it every endpoint answers correctly about an empty folder.
        Assert.Equal(1, host.Extension.IndexedFileCount);
        Assert.Equal("titlecase", (await GetJsonAsync(host, SettingsUrl)).GetProperty("tagNameStyle").GetString());
    }

    // ---------------------------------------------------------------------
    // Assertions
    // ---------------------------------------------------------------------

    /// <summary>
    /// Asserts the object carries exactly these property names. Exactly, rather than at-least: a
    /// renamed property has to fail, and a new C# property the UI was never told about is drift worth
    /// surfacing while it is still one line to fix in <c>api.ts</c>.
    /// </summary>
    private static void AssertProperties(JsonElement body, params string[] expected)
    {
        var actual = body.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.Equal(expected.Order().ToArray(), actual);
    }

    private static async Task<JsonElement> GetJsonAsync(EndpointHost host, string url)
    {
        var response = await host.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ---------------------------------------------------------------------
    // Host
    // ---------------------------------------------------------------------

    /// <summary>
    /// <paramref name="store"/> stands in for the host handing over this extension's key-value store,
    /// which it does before initialization. Passing the same one to two hosts is a restart.
    /// </summary>
    private static async Task<EndpointHost> StartAsync(IExtensionStore? store = null, bool refusesTheTagName = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "torrent-metadata-endpoint-tests", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "torrents");
        Directory.CreateDirectory(folder);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();

        // One connection held open for the host's lifetime. A SQLite in-memory database lives and dies
        // with its connection, so registering the context by connection string would give every request
        // its own empty database and every one of these tests would pass against nothing.
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        builder.Services.AddDbContext<CoveContext>(options => options.UseSqlite(connection));

        // Last registration wins, so this swaps the context the endpoints resolve while leaving the
        // options AddDbContext built. Faking the throw is the only way to reach it here: the exception
        // comes from a 1.3 rule the SQLite fixture does not enforce, and the alternative — seeding a
        // library that really holds two spellings — cannot be built through the ORM on 1.3 at all
        //.
        if (refusesTheTagName)
            builder.Services.AddScoped<CoveContext>(provider =>
                new RefusesTheTagName(provider.GetRequiredService<DbContextOptions<CoveContext>>()));

        var extension = new TorrentMetadataExtension();
        extension.UseTorrentFolder(folder);
        extension.ConfigureServices(builder.Services, Context());
        if (store is not null)
            extension.SetStore(store);

        var app = builder.Build();
        extension.MapEndpoints(app);
        await app.StartAsync();

        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CoveContext>().Database.EnsureCreatedAsync();

        return new EndpointHost(app, extension, connection, root, folder);
    }

    /// <summary>Stands in for 1.3 refusing a tag name the namespace already holds.</summary>
    private sealed class RefusesTheTagName(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            ChangeTracker.Entries<Tag>().Any(entry => entry.State == EntityState.Added)
                ? throw TagNameConflictException.ForExistingTagName("kissing")
                : base.SaveChangesAsync(cancellationToken);
    }

    private static ExtensionContext Context() => new()
    {
        Configuration = new ConfigurationBuilder().Build(),
        DataDirectory = Path.GetTempPath(),
        CoveVersion = "test",
    };

    private sealed class EndpointHost(
        WebApplication app,
        TorrentMetadataExtension extension,
        SqliteConnection connection,
        string root,
        string folder) : IAsyncDisposable
    {
        public TorrentMetadataExtension Extension { get; } = extension;

        public string Folder { get; } = folder;

        public IServiceProvider Services => app.Services;

        public HttpClient Client { get; } = app.GetTestClient();

        /// <summary>Every route this extension mapped, with the metadata the host authorizes on.</summary>
        public (string Pattern, Endpoint Endpoint)[] MappedRoutes() =>
        [
            .. app.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(Base, StringComparison.Ordinal) == true)
                .Select(endpoint => (endpoint.RoutePattern.RawText!, (Endpoint)endpoint)),
        ];

        public async Task<int> SeedVideoAsync()
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

            var video = new Video { Title = VideoFile };
            db.Videos.Add(video);
            await db.SaveChangesAsync();

            var parent = new Folder { Path = "/library" };
            db.Set<Folder>().Add(parent);
            await db.SaveChangesAsync();

            db.VideoFiles.Add(new VideoFile
            {
                Basename = VideoFile,
                ParentFolderId = parent.Id,
                Size = VideoSize,
                VideoId = video.Id,
            });
            await db.SaveChangesAsync();
            return video.Id;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            connection.Dispose();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover scratch directory in the temp folder is not worth failing a test over.
            }
        }
    }

    // ---------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------

    private static TorrentRelease SampleTorrent() => new()
    {
        Name = "sample-scene",
        Title = "[SAMPLE-001] A Sample Scene",
        Comment = "https://tracker.invalid/torrents.php?id=1133888",
        TagList = ["kissing", "deep.blue.sea"],
        Videos = [new TorrentVideoFile(VideoFile, VideoSize)],
    };
}
