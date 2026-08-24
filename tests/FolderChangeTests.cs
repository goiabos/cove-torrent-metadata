using Cove.TorrentMetadata;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// What the folder probe notices between one scan and the next.
///
/// The index is rebuilt only on startup, on upload, or when the user presses Rescan, so a torrent
/// copied in by hand is invisible until they think to. A `FileSystemWatcher` was the obvious answer; measuring
/// first said otherwise — a stat-only sweep of the real folder (3,272 torrents) costs 8 ms warm
/// against 3.39 s merely to read and hash the same files cold, so the cheap half is *detecting* the
/// change and the expensive half stays where it was, behind a button.
///
/// These drive <see cref="TorrentMetadataExtension.ReloadIndex"/> and
/// <see cref="TorrentMetadataExtension.ProbeFolders"/> directly, the way `IndexReloadTests` drives the
/// walk: both are public, neither needs a service, and an HTTP pipeline between the assertion and the
/// comparison would only obscure it. The endpoint's own shape is asserted once, in
/// `EndpointContractTests`.
///
/// Every case here is a change the *obvious* implementation misses. A count plus the newest mtime is
/// what one reaches for first, and `cp -p`, `rsync -t` and most archive extractions preserve the
/// source mtime — so a torrent replaced under the same name lands with an unchanged count and an
/// unchanged or older timestamp, and nothing is noticed.
/// </summary>
public class FolderChangeTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "torrent-metadata-folder-tests", Guid.NewGuid().ToString("N"));

    private readonly TorrentMetadataExtension _extension = new();

    public FolderChangeTests()
    {
        Directory.CreateDirectory(_folder);
        _extension.UseTorrentFolder(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory in the temp folder is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------------
    // Silence when nothing has happened
    // ---------------------------------------------------------------------

    [Fact]
    public void Reports_no_change_immediately_after_a_scan()
    {
        Write("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 5_387_499_251L));
        _extension.ReloadIndex();

        var report = _extension.ProbeFolders();

        Assert.False(report.Changed);
        Assert.Empty(report.Removed);
        var folder = Assert.Single(report.Folders);
        Assert.True(folder.Exists);
        Assert.True(folder.Checked);
        Assert.False(folder.Changed);
    }

    [Fact]
    public void Ignores_a_file_that_is_not_a_torrent()
    {
        Write("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 5_387_499_251L));
        _extension.ReloadIndex();

        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "nothing to do with the index");

        // The walk that builds the index reads `*.torrent` only, so anything else appearing beside it
        // changes nothing a rescan could pick up. Reporting it would send the user to press a button
        // that does nothing.
        Assert.False(_extension.ProbeFolders().Changed);
    }

    // ---------------------------------------------------------------------
    // The additions
    // ---------------------------------------------------------------------

    [Fact]
    public void Sees_a_torrent_copied_in_after_the_scan()
    {
        Write("first.torrent", TorrentBytes.SingleFile("first.mp4", 1_000L));
        _extension.ReloadIndex();

        Write("second.torrent", TorrentBytes.SingleFile("second.mp4", 2_000L));

        Assert.True(_extension.ProbeFolders().Changed);
        Assert.True(Assert.Single(_extension.ProbeFolders().Folders).Changed);
    }

    [Fact]
    public void Sees_a_torrent_copied_into_a_subfolder()
    {
        Write("first.torrent", TorrentBytes.SingleFile("first.mp4", 1_000L));
        _extension.ReloadIndex();

        Directory.CreateDirectory(Path.Combine(_folder, "downloaded"));
        Write(Path.Combine("downloaded", "second.torrent"), TorrentBytes.SingleFile("second.mp4", 2_000L));

        // The index walks `AllDirectories`, so the probe has to as well or a whole subtree of torrents
        // would be indexed and then never watched.
        Assert.True(_extension.ProbeFolders().Changed);
    }

    // ---------------------------------------------------------------------
    // The replacements — where a count and a timestamp are not enough
    // ---------------------------------------------------------------------

    [Fact]
    public void Sees_a_replacement_that_kept_its_name_and_its_mtime()
    {
        var path = Write("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 1_000L));
        _extension.ReloadIndex();

        // Exactly what `cp -p` does: new bytes, original timestamp. The file count is unchanged and
        // the newest mtime in the folder is unchanged, so size is the only thing left to notice.
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, TorrentBytes.MultiFile("Pack", ("Pack/a.mp4", 1_000L), ("Pack/b.mp4", 2_000L)));
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.True(_extension.ProbeFolders().Changed);
    }

    [Fact]
    public void Sees_a_replacement_that_kept_its_name_and_its_size()
    {
        // Two torrents of identical encoded length: same structure, same-length names.
        var before = TorrentBytes.SingleFile("scene-a.mp4", 1_000L);
        var after = TorrentBytes.SingleFile("scene-b.mp4", 1_000L);
        Assert.Equal(before.Length, after.Length);

        var path = Write("scene.torrent", before);
        _extension.ReloadIndex();

        File.WriteAllBytes(path, after);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));

        Assert.True(_extension.ProbeFolders().Changed);
    }

    [Fact]
    public void Sees_a_rename()
    {
        var path = Write("old-name.torrent", TorrentBytes.SingleFile("scene.mp4", 1_000L));
        _extension.ReloadIndex();

        File.Move(path, Path.Combine(_folder, "new-name.torrent"));

        // Same bytes, same count, same timestamp — only the path moved. The index keys on contents, so
        // this changes nothing it holds; the probe cannot know that without reading the file, and
        // over-reporting costs a rescan that finds everything where it was.
        Assert.True(_extension.ProbeFolders().Changed);
    }

    [Fact]
    public void Sees_one_torrent_deleted_and_another_added_at_the_same_count()
    {
        Write("first.torrent", TorrentBytes.SingleFile("first.mp4", 1_000L));
        _extension.ReloadIndex();

        File.Delete(Path.Combine(_folder, "first.torrent"));
        Write("second.torrent", TorrentBytes.SingleFile("second.mp4", 2_000L));

        // The count is the same on both sides, which is the case a count-only check is blind to.
        Assert.True(_extension.ProbeFolders().Changed);
    }

    [Fact]
    public void Sees_a_torrent_deleted()
    {
        Write("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 1_000L));
        _extension.ReloadIndex();

        File.Delete(Path.Combine(_folder, "scene.torrent"));

        Assert.True(_extension.ProbeFolders().Changed);
    }

    [Fact]
    public void Does_not_see_a_rewrite_that_kept_the_name_the_size_and_the_mtime()
    {
        var before = TorrentBytes.SingleFile("scene-a.mp4", 1_000L);
        var after = TorrentBytes.SingleFile("scene-b.mp4", 1_000L);
        var path = Write("scene.torrent", before);
        _extension.ReloadIndex();

        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, after);
        File.SetLastWriteTimeUtc(path, stamp);

        // The blind spot, asserted rather than left to be discovered: nothing stat can see has moved.
        // Catching this means reading and hashing every file, which is the rescan this exists to avoid
        // running on its own. It is why the page may only say the folder *looks* unchanged.
        Assert.False(_extension.ProbeFolders().Changed);
    }

    // ---------------------------------------------------------------------
    // What the sweep counts on the way past
    // ---------------------------------------------------------------------

    [Fact]
    public void Counts_the_torrents_it_swept_so_a_waiting_panel_can_say_how_many()
    {
        Write("first.torrent", TorrentBytes.SingleFile("first.mp4", 1_000L));
        Write("second.torrent", TorrentBytes.SingleFile("second.mp4", 2_000L));
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not a torrent");
        _extension.ReloadIndex();

        // The number the settings panel needs before its listing arrives: the stat sweep costs
        // milliseconds where reading and parsing the same folder costs a second or more. It
        // counts `.torrent` files, the same set the listing will read — anything else beside them is
        // not something the panel is about to show.
        Assert.Equal(2, Assert.Single(_extension.ProbeFolders().Folders).Files);
    }

    [Fact]
    public void Counts_no_torrents_for_a_folder_that_is_not_there()
    {
        _extension.ReloadIndex();
        Directory.Delete(_folder, recursive: true);

        // Zero because nothing was seen, not as a claim that the folder is empty — `Exists` is what
        // says which of those it is.
        var folder = Assert.Single(_extension.ProbeFolders().Folders);
        Assert.Equal(0, folder.Files);
        Assert.False(folder.Exists);
    }

    // ---------------------------------------------------------------------
    // Folders, rather than the files in them
    // ---------------------------------------------------------------------

    [Fact]
    public void Reports_a_folder_that_has_gone_missing_rather_than_throwing()
    {
        Write("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 1_000L));
        _extension.ReloadIndex();

        Directory.Delete(_folder, recursive: true);

        var report = _extension.ProbeFolders();

        // A source can sit on a drive that is not always mounted, so this is a state rather than an
        // error — and it is a change, because everything the index holds from it is now unreachable.
        var folder = Assert.Single(report.Folders);
        Assert.False(folder.Exists);
        Assert.True(folder.Checked);
        Assert.True(folder.Changed);
        Assert.True(report.Changed);
    }

    [Fact]
    public void Tells_an_empty_folder_apart_from_one_that_has_gone_missing()
    {
        // Nothing written: both sides of the comparison see zero files and an empty digest, so the
        // folder's own existence is the only thing left that can differ. An unmounted drive would
        // otherwise read exactly like a folder the user had emptied, and only one of those means the
        // torrents in the index are unreachable.
        _extension.ReloadIndex();
        Directory.Delete(_folder, recursive: true);

        Assert.True(_extension.ProbeFolders().Changed);
    }

    [Fact]
    public void Reports_a_folder_the_last_scan_never_read()
    {
        Write("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 1_000L));
        _extension.ReloadIndex();

        // Standing in for the operator adding a source folder: settings deliberately do not rebuild
        // the index, so a folder can be configured and hold nothing indexed until a rescan.
        var second = Directory.CreateDirectory(Path.Combine(_folder, "..", Guid.NewGuid().ToString("N"))).FullName;
        _extension.UseTorrentFolder(second);

        try
        {
            var report = _extension.ProbeFolders();

            Assert.True(report.Changed);
            Assert.True(Assert.Single(report.Folders).Changed);
        }
        finally
        {
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void Names_a_folder_that_is_no_longer_configured()
    {
        Write("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 1_000L));
        _extension.ReloadIndex();

        var second = Directory.CreateDirectory(Path.Combine(_folder, "..", Guid.NewGuid().ToString("N"))).FullName;
        _extension.UseTorrentFolder(second);

        try
        {
            var report = _extension.ProbeFolders();

            // The torrents of a folder that has been dropped stay in the index until it is rebuilt, so
            // this is a staleness with no folder left to report it against. It reads backwards from
            // every other one: the rescan that fixes it *removes* rows.
            Assert.Equal([_folder], report.Removed);
            Assert.True(report.Changed);
        }
        finally
        {
            Directory.Delete(second, recursive: true);
        }
    }

    // ---------------------------------------------------------------------
    // The baseline is the scan's, not the probe's
    // ---------------------------------------------------------------------

    [Fact]
    public void Stops_reporting_a_change_once_the_index_has_been_rebuilt()
    {
        Write("first.torrent", TorrentBytes.SingleFile("first.mp4", 1_000L));
        _extension.ReloadIndex();
        Write("second.torrent", TorrentBytes.SingleFile("second.mp4", 2_000L));
        Assert.True(_extension.ProbeFolders().Changed);

        _extension.ReloadIndex();

        // Rescanning is what settles it. A probe that reset the baseline itself would clear the notice
        // by looking at it, and the second visit to the page would say the folder was up to date while
        // the index still knew nothing about the file.
        Assert.False(_extension.ProbeFolders().Changed);
    }

    // ---------------------------------------------------------------------
    // A directory that will not open
    // ---------------------------------------------------------------------

    [PermissionEnforcedFact("Creating a permission-denied directory needs POSIX file modes. CI runs Linux.")]
    public void Notices_a_new_torrent_beside_a_directory_it_cannot_open()
    {
        // Two walks, one folder, and before this they failed differently. `ReloadIndex` threw out of
        // the enumerator and never reached `_lastScan`; `FolderSignature.Compute` caught the same
        // throw and reported the folder unchecked, which `DiffersFrom` answers with "not changed". So
        // the reload was broken *and* the probe said the folder was fine, and a torrent copied in
        // beside the locked directory was invisible with nothing on the page hinting why.
        if (OperatingSystem.IsWindows())
            return;

        var locked = Directory.CreateDirectory(Path.Combine(_folder, "locked"));
        File.SetUnixFileMode(locked.FullName, UnixFileMode.None);

        try
        {
            Write("first.torrent", TorrentBytes.SingleFile("first.mp4", 1_000L));
            _extension.ReloadIndex();

            // Checked, not unchecked. Both walks skip the same directory, so the fingerprint describes
            // the readable part of the folder and is a claim that compares against the next one.
            var before = Assert.Single(_extension.ProbeFolders().Folders);
            Assert.True(before.Checked);
            Assert.False(before.Changed);

            Write("second.torrent", TorrentBytes.SingleFile("second.mp4", 2_000L));

            Assert.True(_extension.ProbeFolders().Changed);
        }
        finally
        {
            File.SetUnixFileMode(
                locked.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [PermissionEnforcedFact("Creating a permission-denied directory needs POSIX file modes. CI runs Linux.")]
    public void Reports_the_folder_itself_as_unchecked_when_it_will_not_open()
    {
        // The case that stays unchecked, and the reason the two are told apart at all: with the folder
        // itself refused there is no partial answer to compare — nothing under it was seen — so
        // claiming it was unchanged would be a guess, and claiming it emptied would be worse.
        if (OperatingSystem.IsWindows())
            return;

        Write("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 1_000L));
        _extension.ReloadIndex();
        File.SetUnixFileMode(_folder, UnixFileMode.None);

        try
        {
            var folder = Assert.Single(_extension.ProbeFolders().Folders);

            Assert.False(folder.Checked);
            Assert.False(folder.Changed);
        }
        finally
        {
            File.SetUnixFileMode(
                _folder,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Reports_a_change_before_any_scan_has_happened()
    {
        Write("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 1_000L));

        // Only reachable in a test — the host initializes the extension, which reloads — but the
        // direction is the one to hold: with no scan recorded, nothing in the folder is indexed, so
        // "changed" is the true answer and silence would be a lie.
        Assert.True(_extension.ProbeFolders().Changed);
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
