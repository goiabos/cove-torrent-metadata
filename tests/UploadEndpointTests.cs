using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cove.TorrentMetadata;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// Drives <c>POST /api/extensions/torrent-metadata/upload</c> over a real HTTP pipeline.
///
/// This is the only endpoint that accepts user-supplied files, and every rule protecting it lives in
/// the endpoint lambda rather than in a service — the extension check, the size cap, writing by base
/// name so nothing escapes the watched folder, and the parse-check that deletes what it cannot read.
/// Testing an extracted helper would assert the logic and miss the boundary, so the endpoints are
/// mapped onto a test host and driven with real multipart requests.
///
/// The response body is asserted too, because it is a contract rather than a detail: <c>ui/src/api.ts</c>
/// types it and <c>TorrentDropZone</c> reads <c>added[0]</c> to pin a proposal to the file the user
/// just dropped.
///
/// Torrents are built in-code. The sample corpus in <c>resources/</c> is optional in a checkout, and
/// the older suites skip themselves when it is missing — a test that quietly returns is a test that
/// reports success without running, which is not good enough for this endpoint.
/// </summary>
public class UploadEndpointTests
{
    private const string UploadUrl = "/api/extensions/torrent-metadata/upload";

    /// <summary>Matches <c>TorrentMetadataExtension.MaxTorrentBytes</c>, which is private.</summary>
    private const long MaxUploadBytes = 8 * 1024 * 1024;

    /// <summary>Matches <c>TorrentMetadataExtension.MaxTorrentUploadFiles</c>, which is private.</summary>
    private const int MaxUploadFiles = 200;

    // ---------------------------------------------------------------------
    // Request-level guards
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Rejects_a_request_that_is_not_a_multipart_form()
    {
        await using var host = await StartAsync();

        var response = await host.Client.PostAsync(
            UploadUrl,
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Expected a multipart form upload.",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Rejects_an_upload_before_the_torrent_folder_is_resolved()
    {
        // MapEndpoints on an extension that never went through ConfigureServices, so it has no folder.
        // The host always configures first, which makes this guard defensive — but a 400 saying so is
        // the difference between a clear failure and an unhandled exception on a user's upload.
        await using var host = await StartAsync(withTorrentFolder: false);

        var response = await host.Client.PostAsync(UploadUrl, Upload(("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 42))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Torrent folder is not configured.",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    // ---------------------------------------------------------------------
    // The happy path, and the identities the dialog depends on
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Stores_a_torrent_and_reports_the_video_it_describes()
    {
        await using var host = await StartAsync();

        var body = await PostAsync(host, ("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 5_387_499_251L)));

        Assert.Equal(1, body.GetProperty("saved").GetInt32());
        Assert.Empty(body.GetProperty("rejected").EnumerateArray());
        Assert.True(File.Exists(Path.Combine(host.Folder, "scene.torrent")));

        // One entry per video, carrying what the drop zone needs to pin a proposal to this exact file.
        var added = Assert.Single(body.GetProperty("added").EnumerateArray().ToList());
        Assert.Equal("scene.mp4", added.GetProperty("torrentName").GetString());
        Assert.Equal("scene.mp4", added.GetProperty("fileName").GetString());
        Assert.Equal(1, added.GetProperty("fanOut").GetInt32());

        // The upload reloads the index, so the new torrent is queryable without a separate reload call.
        Assert.Equal(1, body.GetProperty("torrents").GetInt32());
        Assert.Equal(1, body.GetProperty("files").GetInt32());
        Assert.Equal(1, host.Extension.IndexedFileCount);
    }

    [Fact]
    public async Task Reports_every_video_in_a_pack_against_the_one_file_that_was_saved()
    {
        await using var host = await StartAsync();

        var body = await PostAsync(host, ("pack.torrent", TorrentBytes.MultiFile(
            "Season Pack",
            ("Season Pack/scene-01.mp4", 1_000L),
            ("Season Pack/scene-02.mp4", 2_000L),
            ("Season Pack/cover.jpg", 3_000L))));

        // saved counts files written; added counts videos described. A pack makes the two differ, and
        // the fan-out is what lets the UI de-emphasise shared pack metadata.
        Assert.Equal(1, body.GetProperty("saved").GetInt32());
        var added = body.GetProperty("added").EnumerateArray().ToList();
        Assert.Equal(2, added.Count);
        Assert.Equal(["scene-01.mp4", "scene-02.mp4"], added.Select(entry => entry.GetProperty("fileName").GetString()));
        Assert.All(added, entry => Assert.Equal(2, entry.GetProperty("fanOut").GetInt32()));
        Assert.Equal(2, body.GetProperty("files").GetInt32());
    }

    // ---------------------------------------------------------------------
    // Per-file rejection rules
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Rejects_a_file_that_is_not_a_torrent_without_writing_it()
    {
        await using var host = await StartAsync();

        var body = await PostAsync(host, ("payload.txt", Encoding.UTF8.GetBytes("not a torrent")));

        Assert.Equal(0, body.GetProperty("saved").GetInt32());
        Assert.Equal("payload.txt: not a .torrent", Assert.Single(Rejected(body)));
        // Rejected on the name alone, so nothing was ever written to disk.
        Assert.Empty(Directory.GetFiles(host.Folder));
    }

    [Fact]
    public async Task Accepts_a_torrent_whose_extension_is_upper_case()
    {
        await using var host = await StartAsync();

        var body = await PostAsync(host, ("SCENE.TORRENT", TorrentBytes.SingleFile("scene.mp4", 42)));

        Assert.Equal(1, body.GetProperty("saved").GetInt32());
        Assert.True(File.Exists(Path.Combine(host.Folder, "SCENE.TORRENT")));
    }

    [Fact]
    public async Task Rejects_an_empty_file()
    {
        await using var host = await StartAsync();

        var body = await PostAsync(host, ("empty.torrent", []));

        Assert.Equal(0, body.GetProperty("saved").GetInt32());
        Assert.Equal("empty.torrent: size out of range", Assert.Single(Rejected(body)));
        Assert.Empty(Directory.GetFiles(host.Folder));
    }

    [Fact]
    public async Task Rejects_a_file_over_the_size_cap_without_writing_it()
    {
        await using var host = await StartAsync();

        var body = await PostAsync(host, ("huge.torrent", new byte[MaxUploadBytes + 1]));

        Assert.Equal(0, body.GetProperty("saved").GetInt32());
        Assert.Equal("huge.torrent: size out of range", Assert.Single(Rejected(body)));
        // The cap is checked before the write, so an oversized upload never reaches the disk.
        Assert.Empty(Directory.GetFiles(host.Folder));
    }

    [Fact]
    public async Task Refuses_the_files_past_the_count_cap_and_keeps_the_rest()
    {
        await using var host = await StartAsync();

        // Two past the cap. Per-file size bounded the payload and nothing bounded the count, so the
        // only thing between this endpoint and tens of thousands of write-read-parse cycles in one
        // request was the host's default body limit — which is Kestrel's decision, not ours.
        var files = Enumerable.Range(0, MaxUploadFiles + 2)
            .Select(index => ($"scene-{index}.torrent", TorrentBytes.SingleFile($"scene-{index}.mp4", 100L + index)))
            .ToArray();

        var body = await PostAsync(host, files);

        Assert.Equal(MaxUploadFiles, body.GetProperty("saved").GetInt32());
        // Refused per file rather than by failing the request, so what fitted is still kept — the same
        // shape as every other refusal in this loop.
        Assert.Equal(
            [
                $"scene-{MaxUploadFiles}.torrent: more than {MaxUploadFiles} files in one upload",
                $"scene-{MaxUploadFiles + 1}.torrent: more than {MaxUploadFiles} files in one upload",
            ],
            Rejected(body));
        Assert.Equal(MaxUploadFiles, Directory.GetFiles(host.Folder).Length);
    }

    [Fact]
    public async Task Counts_every_part_against_the_cap_not_only_the_torrents()
    {
        await using var host = await StartAsync();

        // The cap bounds the request, not the number of torrents in it: what it is protecting is the
        // loop itself, and a caller can make that loop long with parts of any kind.
        var files = Enumerable.Range(0, MaxUploadFiles)
            .Select(index => ($"notes-{index}.txt", Encoding.UTF8.GetBytes("ignore me")))
            .Append(("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 42)))
            .ToArray();

        var body = await PostAsync(host, files);

        Assert.Equal(0, body.GetProperty("saved").GetInt32());
        Assert.Contains(
            $"scene.torrent: more than {MaxUploadFiles} files in one upload",
            Rejected(body));
        Assert.Empty(Directory.GetFiles(host.Folder));
    }

    [Fact]
    public async Task Deletes_a_file_it_cannot_parse()
    {
        await using var host = await StartAsync();

        var body = await PostAsync(host, ("corrupt.torrent", [0x00, 0x01, 0x02, 0x03]));

        Assert.Equal(0, body.GetProperty("saved").GetInt32());
        Assert.Equal("corrupt.torrent: not readable as a torrent", Assert.Single(Rejected(body)));
        // The parse check runs after the write, so the file has to be cleaned up rather than left to
        // fail silently on every future reload.
        Assert.Empty(Directory.GetFiles(host.Folder));
    }

    [Fact]
    public async Task Deletes_a_torrent_that_contains_no_video()
    {
        await using var host = await StartAsync();

        var body = await PostAsync(host, ("images.torrent", TorrentBytes.MultiFile(
            "Photo Set",
            ("Photo Set/001.jpg", 100L),
            ("Photo Set/002.jpg", 200L))));

        Assert.Equal(0, body.GetProperty("saved").GetInt32());
        Assert.Equal("images.torrent: contains no video", Assert.Single(Rejected(body)));
        Assert.Empty(Directory.GetFiles(host.Folder));
        Assert.Equal(0, body.GetProperty("files").GetInt32());
    }

    // ---------------------------------------------------------------------
    // A corrupt or duplicate upload must not destroy what is already there
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Keeps_the_existing_torrent_byte_for_byte_when_a_same_name_upload_is_corrupt()
    {
        await using var host = await StartAsync();

        // A perfectly good torrent already sitting in the folder, exactly as it would be after a
        // real user's earlier upload.
        var goodBytes = TorrentBytes.SingleFile("scene.mp4", 5_387_499_251L);
        await PostAsync(host, ("scene.torrent", goodBytes));

        // Re-uploading a corrupt file under that exact name used to truncate `scene.torrent` in
        // place (File.Create opens for writing before anything is validated), so the parse-check
        // that followed always failed and the file was deleted — losing a torrent the user never
        // named. Writing to a temp name first means the failure here can only cost the temp file.
        var body = await PostAsync(host, ("scene.torrent", [0x00, 0x01, 0x02, 0x03]));

        Assert.Equal(0, body.GetProperty("saved").GetInt32());
        Assert.Equal("scene.torrent: not readable as a torrent", Assert.Single(Rejected(body)));
        Assert.Equal(
            goodBytes,
            await File.ReadAllBytesAsync(Path.Combine(host.Folder, "scene.torrent")));
        // Nothing else survives either: no truncated target (already checked above) and no leftover
        // ".uploading-*" temp file from the write-then-validate sequence that never reached its rename.
        Assert.Equal(["scene.torrent"], Directory.GetFiles(host.Folder).Select(Path.GetFileName));
    }

    // ---------------------------------------------------------------------
    // Path safety
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("../../escape.torrent")]
    [InlineData("..\\..\\escape.torrent")]
    [InlineData("/etc/cron.d/escape.torrent")]
    public async Task Writes_by_base_name_so_an_upload_cannot_escape_the_watched_folder(string fileName)
    {
        await using var host = await StartAsync();

        var body = await PostAsync(host, (fileName, TorrentBytes.SingleFile("scene.mp4", 42)));

        Assert.Equal(1, body.GetProperty("saved").GetInt32());
        // The invariant is containment, not the resulting name: which separators count is
        // platform-specific, but nothing may ever appear beside the watched folder.
        Assert.Equal([host.Folder], Directory.GetFileSystemEntries(host.Root));
        Assert.Single(Directory.GetFiles(host.Folder));
    }

    // ---------------------------------------------------------------------
    // Batches, and re-uploads
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Keeps_the_readable_files_in_a_mixed_upload()
    {
        await using var host = await StartAsync();

        var body = await PostAsync(
            host,
            ("good.torrent", TorrentBytes.SingleFile("scene.mp4", 42)),
            ("notes.txt", Encoding.UTF8.GetBytes("ignore me")),
            ("corrupt.torrent", [0x00, 0x01]));

        // One bad file in a drag-and-drop of many must not cost the user the rest of the batch.
        Assert.Equal(1, body.GetProperty("saved").GetInt32());
        Assert.Equal(
            ["notes.txt: not a .torrent", "corrupt.torrent: not readable as a torrent"],
            Rejected(body));
        Assert.Equal(["good.torrent"], Directory.GetFiles(host.Folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Re_uploading_the_same_name_replaces_it_rather_than_indexing_it_twice()
    {
        await using var host = await StartAsync();

        await PostAsync(host, ("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 42)));
        var body = await PostAsync(host, ("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 4242)));

        Assert.Equal(1, body.GetProperty("saved").GetInt32());
        Assert.Single(Directory.GetFiles(host.Folder));
        // The index is rebuilt from the folder, so the replaced file leaves no stale entry behind.
        Assert.Equal(1, body.GetProperty("files").GetInt32());
        // Not only does the API report success — the rename actually replaced the bytes on disk,
        // which is the part `overwrite: true` has to get right rather than merely returning 200.
        Assert.Equal(
            TorrentBytes.SingleFile("scene.mp4", 4242),
            await File.ReadAllBytesAsync(Path.Combine(host.Folder, "scene.torrent")));
    }

    [Fact]
    public async Task Counts_one_saved_when_two_files_in_one_request_share_a_base_name()
    {
        await using var host = await StartAsync();

        // Two different torrents that happen to reduce to the same base name — the same shape as a
        // browser drag-and-drop that repeats a filename. Only one file can exist under that name once
        // both have been written, so "saved" counting each independently well-formed part is exactly
        // the mismatch that exposed it: the count has to describe what survives, not how many parts parsed.
        var second = TorrentBytes.SingleFile("scene.mp4", 4242);
        var body = await PostAsync(
            host,
            ("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 42)),
            ("scene.torrent", second));

        Assert.Equal(1, body.GetProperty("saved").GetInt32());
        Assert.Single(Directory.GetFiles(host.Folder));
        // The later part in request order is the one still on disk, since each is written to its own
        // temp file and then renamed onto the shared target in the order the loop processes them.
        Assert.Equal(second, await File.ReadAllBytesAsync(Path.Combine(host.Folder, "scene.torrent")));
        // `added` is built from the same map "saved" is counted from, so it names one video rather
        // than two — a caller pinning a proposal to "the file just dropped" must not be handed two
        // candidates for a name that resolves to a single file.
        Assert.Single(body.GetProperty("added").EnumerateArray().ToList());
    }

    // ---------------------------------------------------------------------
    // The folder seam itself
    // ---------------------------------------------------------------------

    [Fact]
    public void ConfigureServices_keeps_an_explicitly_set_torrent_folder()
    {
        var extension = new TorrentMetadataExtension();
        extension.UseTorrentFolder("/tmp/torrent-metadata-explicit");

        extension.ConfigureServices(new ServiceCollection(), Context());

        // Were this overwritten with the COVE_HOME-derived default, every test above would silently
        // start writing into the real data root instead of its scratch folder.
        Assert.Equal("/tmp/torrent-metadata-explicit", extension.TorrentFolder);
    }

    // ---------------------------------------------------------------------
    // Host
    // ---------------------------------------------------------------------

    private static async Task<UploadHost> StartAsync(bool withTorrentFolder = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "torrent-metadata-upload-tests", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "torrents");
        Directory.CreateDirectory(folder);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();

        // ConfigureServices registers the per-request services the other endpoints declare. They are
        // never resolved here, but minimal-API parameter binding needs to know they are services rather
        // than a second JSON body, so the real registration runs rather than a stand-in.
        var configured = new TorrentMetadataExtension();
        configured.UseTorrentFolder(folder);
        configured.ConfigureServices(builder.Services, Context());

        var extension = withTorrentFolder ? configured : new TorrentMetadataExtension();

        var app = builder.Build();
        extension.MapEndpoints(app);
        await app.StartAsync();

        return new UploadHost(app, extension, root, folder);
    }

    private static ExtensionContext Context() => new()
    {
        Configuration = new ConfigurationBuilder().Build(),
        DataDirectory = Path.GetTempPath(),
        CoveVersion = "test",
    };

    private static async Task<JsonElement> PostAsync(UploadHost host, params (string Name, byte[] Content)[] files)
    {
        var response = await host.Client.PostAsync(UploadUrl, Upload(files));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static MultipartFormDataContent Upload(params (string Name, byte[] Content)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, content) in files)
            form.Add(new ByteArrayContent(content), "files", name);
        return form;
    }

    private static List<string> Rejected(JsonElement body) =>
        [.. body.GetProperty("rejected").EnumerateArray().Select(entry => entry.GetString()!)];

    private sealed class UploadHost(WebApplication app, TorrentMetadataExtension extension, string root, string folder)
        : IAsyncDisposable
    {
        public TorrentMetadataExtension Extension { get; } = extension;

        /// <summary>Parent of the watched folder, so a test can assert nothing was written beside it.</summary>
        public string Root { get; } = root;

        public string Folder { get; } = folder;

        public HttpClient Client { get; } = app.GetTestClient();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover scratch directory in the temp folder is not worth failing a test over.
            }
        }
    }
}
