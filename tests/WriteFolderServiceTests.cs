using Cove.Core.Entities;
using Cove.Data;
using Cove.TorrentMetadata;
using Microsoft.EntityFrameworkCore;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// Listing and removing the torrents in the one folder this extension writes.
///
/// Two things are worth stating about what these cover. The listing reads the *folder*, not the index,
/// because the index carries no path and cannot say which of its torrents came from here — so the
/// filename, which is the identity a remove names and the one the user recognises, exists nowhere else.
/// And removal is checked for containment rather than trusted: the caller names a file, and a name is
/// the one part of this that arrives from a browser.
///
/// No test uses a real .torrent. <see cref="TorrentBytes"/> writes the bytes, as everywhere else here.
/// </summary>
public class WriteFolderServiceTests : IDisposable
{
    private const long SceneSize = 5_387_499_251L;

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "torrent-metadata-folder-tests", Guid.NewGuid().ToString("N"));

    public WriteFolderServiceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------------
    // Listing
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Lists_a_torrent_by_its_filename_and_its_release_name()
    {
        Write("dropped-file.torrent", TorrentBytes.SingleFile("Release Name.mp4", SceneSize));
        await using var db = CreateContext();

        var torrent = Assert.Single(await new WriteFolderService(db).ListAsync(_folder));

        // Two different strings, and the panel shows both: the file is what the user dragged in, the
        // name is what the batch page keys its rows on.
        Assert.Equal("dropped-file.torrent", torrent.File);
        Assert.Equal("Release Name.mp4", torrent.Name);
        Assert.Equal(1, torrent.VideoFiles);
    }

    [Fact]
    public async Task Lists_a_file_that_will_not_parse_rather_than_skipping_it()
    {
        Write("broken.torrent", "not bencode at all"u8.ToArray());
        await using var db = CreateContext();

        var torrent = Assert.Single(await new WriteFolderService(db).ListAsync(_folder));

        // The one kind of file in here that can never do anything useful. Hiding it would hide the only
        // entry whose removal is unambiguously right, and leave the user with a file nothing mentions.
        Assert.Equal("broken.torrent", torrent.File);
        Assert.Null(torrent.Name);
        Assert.Equal(0, torrent.VideoFiles);
        Assert.Equal(0, torrent.Applied);
    }

    [PermissionEnforcedFact("Creating a permission-denied file needs POSIX file modes. CI runs Linux.")]
    public async Task Lists_a_permission_denied_file_with_a_null_name_same_as_one_it_cannot_parse()
    {
        // Guarded again here, redundantly with the attribute's Skip, purely so the platform-compat
        // analyzer can see `SetUnixFileMode` is never reached on Windows — it cannot see through a
        // custom FactAttribute's runtime Skip.
        if (OperatingSystem.IsWindows())
            return;

        // The same defect ReloadIndex had: `UnauthorizedAccessException` is a `SystemException`,
        // not an `IOException`, so a permission-denied file used to escape `ListAsync` entirely instead
        // of being reported the same way a file that will not parse is.
        Write("locked.torrent", TorrentBytes.SingleFile("locked.mp4", SceneSize));
        var locked = Path.Combine(_folder, "locked.torrent");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            await using var db = CreateContext();

            var torrent = Assert.Single(await new WriteFolderService(db).ListAsync(_folder));

            Assert.Equal("locked.torrent", torrent.File);
            Assert.Null(torrent.Name);
            Assert.Equal(0, torrent.VideoFiles);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Orders_by_filename_so_the_panel_reads_the_same_way_twice()
    {
        Write("c.torrent", TorrentBytes.SingleFile("c.mp4", 3));
        Write("a.torrent", TorrentBytes.SingleFile("a.mp4", 1));
        Write("b.torrent", TorrentBytes.SingleFile("b.mp4", 2));
        await using var db = CreateContext();

        var files = (await new WriteFolderService(db).ListAsync(_folder)).Select(torrent => torrent.File);

        // The user scrolls and filters this list. An order that moves between loads is an order they
        // cannot use — and directory enumeration does not promise one.
        Assert.Equal(["a.torrent", "b.torrent", "c.torrent"], files);
    }

    [Fact]
    public async Task Counts_the_video_files_the_library_actually_holds()
    {
        Write("pack.torrent", TorrentBytes.MultiFile(
            "pack",
            ("Disc1/one.mp4", SceneSize),
            ("Disc2/two.mp4", SceneSize + 1),
            ("Disc3/three.mp4", SceneSize + 2)));

        await using var db = CreateContext();
        await SeedVideoAsync(db, SceneSize);
        await SeedVideoAsync(db, SceneSize + 2);

        var torrent = Assert.Single(await new WriteFolderService(db).ListAsync(_folder));

        Assert.Equal(3, torrent.VideoFiles);
        Assert.Equal(2, torrent.InLibrary);
    }

    [Fact]
    public async Task Counts_applied_from_the_links_rather_than_the_files()
    {
        Write("pack.torrent", TorrentBytes.MultiFile(
            "pack",
            TorrentBytes.CommentFor("9001"),
            [("one.mp4", SceneSize), ("two.mp4", SceneSize + 1), ("three.mp4", SceneSize + 2)]));

        await using var db = CreateContext();
        var first = await SeedVideoAsync(db, SceneSize);
        var second = await SeedVideoAsync(db, SceneSize + 1);
        await SeedVideoAsync(db, SceneSize + 2);
        await LinkAsync(db, first, "9001");
        await LinkAsync(db, second, "9001");

        var torrent = Assert.Single(await new WriteFolderService(db).ListAsync(_folder));

        // The fraction the panel shows. A single "applied" word here would be false for a pack, which
        // is the same reason completion was never a file-level flag.
        Assert.Equal("9001", torrent.TorrentId);
        Assert.Equal(3, torrent.VideoFiles);
        Assert.Equal(2, torrent.Applied);
    }

    [Fact]
    public async Task Counts_no_applied_for_a_torrent_carrying_no_tracker_id()
    {
        Write("no-id.torrent", TorrentBytes.SingleFile("scene.mp4", SceneSize));

        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SceneSize);
        await LinkAsync(db, videoId, "9001");

        var torrent = Assert.Single(await new WriteFolderService(db).ListAsync(_folder));

        // Without a comment URL there is no id, and an apply writes no link — so zero is the true
        // answer here even though the library holds a link for another torrent entirely.
        Assert.Null(torrent.TorrentId);
        Assert.Equal(0, torrent.Applied);
        Assert.Equal(1, torrent.InLibrary);
    }

    [Fact]
    public async Task Reports_nothing_for_a_folder_that_is_not_there()
    {
        await using var db = CreateContext();

        // A folder is created on first upload, so before anyone has dropped anything it does not exist.
        // The panel asks anyway, and an empty list is the honest answer rather than an error.
        Assert.Empty(await new WriteFolderService(db).ListAsync(Path.Combine(_folder, "never-created")));
        Assert.Empty(await new WriteFolderService(db).ListAsync(null));
    }

    // ---------------------------------------------------------------------
    // Removal
    // ---------------------------------------------------------------------

    [Fact]
    public void Removes_the_named_file()
    {
        Write("gone.torrent", TorrentBytes.SingleFile("scene.mp4", SceneSize));
        Write("kept.torrent", TorrentBytes.SingleFile("other.mp4", SceneSize + 1));

        var result = WriteFolderService.Remove(_folder, ["gone.torrent"]);

        Assert.Equal(1, result.Removed);
        Assert.Empty(result.Refused);
        Assert.False(File.Exists(Path.Combine(_folder, "gone.torrent")));
        Assert.True(File.Exists(Path.Combine(_folder, "kept.torrent")));
    }

    [Theory]
    [InlineData("../escaped.torrent")]
    [InlineData("../../escaped.torrent")]
    [InlineData("sub/../../escaped.torrent")]
    public void Refuses_a_name_that_climbs_out_of_the_folder(string name)
    {
        var outside = Path.Combine(Path.GetDirectoryName(_folder)!, "escaped.torrent");
        File.WriteAllBytes(outside, TorrentBytes.SingleFile("scene.mp4", SceneSize));

        try
        {
            var result = WriteFolderService.Remove(_folder, [name]);

            // Source folders are the operator's and are read-only. A remove that resolved outside our
            // own folder would delete from one of theirs, which is the whole reason the split exists.
            Assert.Equal(0, result.Removed);
            Assert.Single(result.Refused);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    /// <summary>
    /// A directory symlink inside the folder does not make a source folder addressable.
    ///
    /// `Path.GetFullPath` normalises `..` and nothing else — it does not resolve links — so
    /// `link/scene.torrent` stayed lexically under the write folder and passed containment, while
    /// `File.Delete` followed the link and deleted the operator's file. The rule this repo states about
    /// removal is that the one part of it arriving from a browser must not be able to address a source
    /// folder, and this was the way through.
    ///
    /// A symlinked *file* was never the hazard and still is not: deleting one unlinks the link and
    /// leaves its target alone. Only a directory component can redirect where the name lands.
    /// </summary>
    [Fact]
    public void Refuses_to_remove_through_a_directory_symlink()
    {
        var source = Path.Combine(Path.GetDirectoryName(_folder)!, $"source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        var victim = Path.Combine(source, "theirs.torrent");
        File.WriteAllBytes(victim, TorrentBytes.SingleFile("scene.mp4", SceneSize));

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_folder, "link"), source);

            var result = WriteFolderService.Remove(_folder, [Path.Combine("link", "theirs.torrent")]);

            Assert.Equal(0, result.Removed);
            Assert.Single(result.Refused);
            Assert.True(File.Exists(victim));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    /// <summary>
    /// The listing does not offer a torrent that is only reachable through a directory symlink.
    ///
    /// This is the half that makes the escape easy rather than crafted: the walk is
    /// `SearchOption.AllDirectories`, which descends into a symlinked directory, so every torrent in
    /// the operator's source folder was listed as one of ours. The user did not have to invent a
    /// hostile name — the panel handed them the name and the remove button beside it.
    /// </summary>
    [Fact]
    public async Task Does_not_list_a_torrent_reached_only_through_a_directory_symlink()
    {
        var source = Path.Combine(Path.GetDirectoryName(_folder)!, $"source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "theirs.torrent"), TorrentBytes.SingleFile("scene.mp4", SceneSize));
        Write("ours.torrent", TorrentBytes.SingleFile("mine.mp4", SceneSize));

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_folder, "link"), source);
            await using var db = CreateContext();

            var listed = await new WriteFolderService(db).ListAsync(_folder);

            Assert.Equal(["ours.torrent"], listed.Select(torrent => torrent.File));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public void Refuses_a_name_that_is_not_a_torrent()
    {
        var other = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(other, "mine");

        var result = WriteFolderService.Remove(_folder, ["notes.txt"]);

        // Only .torrent files are ever this extension's own, so nothing else in the folder is ours to
        // delete — even though the folder is.
        Assert.Equal(0, result.Removed);
        Assert.Single(result.Refused);
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Keeps_going_after_a_refusal_and_says_which_one_it_was()
    {
        Write("first.torrent", TorrentBytes.SingleFile("a.mp4", 1));
        Write("second.torrent", TorrentBytes.SingleFile("b.mp4", 2));

        var result = WriteFolderService.Remove(_folder, ["first.torrent", "../escaped.torrent", "second.torrent"]);

        // The same shape as every other bulk operation here: a caller that sent one bad name still gets
        // the rest done, and is told which one it was.
        Assert.Equal(2, result.Removed);
        Assert.Contains("escaped.torrent", Assert.Single(result.Refused));
    }

    [Fact]
    public void Counts_a_file_already_gone_as_removed()
    {
        // The panel's list can be a moment out of date — two tabs, or a rescan between. Deleting what is
        // already deleted is the outcome the caller asked for, not an error they can act on.
        var result = WriteFolderService.Remove(_folder, ["never-existed.torrent"]);

        Assert.Equal(1, result.Removed);
        Assert.Empty(result.Refused);
    }

    [Fact]
    public void Refuses_everything_when_no_folder_is_configured()
    {
        var result = WriteFolderService.Remove(null, ["anything.torrent"]);

        Assert.Equal(0, result.Removed);
        Assert.Single(result.Refused);
    }

    // ---------------------------------------------------------------------

    private void Write(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(_folder, name), bytes);

    private static async Task LinkAsync(CoveContext db, int videoId, string torrentId)
    {
        db.Set<VideoRemoteId>().Add(new VideoRemoteId
        {
            VideoId = videoId,
            Endpoint = TorrentApplyService.RemoteIdEndpoint,
            RemoteId = torrentId,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> SeedVideoAsync(CoveContext db, long size)
    {
        var video = new Video { Title = "video" };
        db.Videos.Add(video);
        await db.SaveChangesAsync();

        var folder = new Folder { Path = $"/library/{video.Id}" };
        db.Set<Folder>().Add(folder);
        await db.SaveChangesAsync();

        db.VideoFiles.Add(new VideoFile
        {
            Basename = $"video-{video.Id}.mp4",
            ParentFolderId = folder.Id,
            Size = size,
            VideoId = video.Id,
        });
        await db.SaveChangesAsync();
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
}
