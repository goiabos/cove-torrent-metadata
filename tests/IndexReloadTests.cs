using Cove.TorrentMetadata;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// What a folder walk reports about the files it did *not* index.
///
/// `ReloadIndex` skips a file four ways, and it once recorded none of them. That is worse than
/// it sounds: a skipped file reaches no other number either. The batch page's `unmatched` count is per
/// indexed video file (`TorrentBatchService`), so a torrent that never entered the index is invisible
/// rather than unmatched, and the page reads identically in both cases. The user is then told nothing
/// at all about the only failures they could have fixed.
///
/// These drive `ReloadIndex()` directly rather than through the endpoint. It is a public method on the
/// extension, the folder walk needs no services, and the alternative — a test host per case — would
/// put an HTTP pipeline between the assertion and the loop it is about. The endpoint's own contract is
/// asserted once, in `EndpointContractTests`.
/// </summary>
public class IndexReloadTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "torrent-metadata-reload-tests", Guid.NewGuid().ToString("N"));

    public IndexReloadTests() => Directory.CreateDirectory(_folder);

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
    // Nothing is reported when nothing was skipped
    // ---------------------------------------------------------------------

    [Fact]
    public void Counts_no_skips_when_every_file_indexes()
    {
        Write("scene.torrent", TorrentBytes.SingleFile("scene.mp4", 5_387_499_251L));

        var report = Reload();

        Assert.Equal(1, report.Torrents);
        Assert.Equal(0, report.Skipped.Total);
        Assert.Equal(0, report.Skipped.Unreadable);
        Assert.Equal(0, report.Skipped.Malformed);
        Assert.Equal(0, report.Skipped.WithoutVideo);
        Assert.Equal(0, report.Skipped.Duplicates);
    }

    // ---------------------------------------------------------------------
    // The split the whole issue turns on
    // ---------------------------------------------------------------------

    [Fact]
    public void Separates_a_torrent_that_will_not_parse_from_one_that_simply_holds_no_video()
    {
        // These two used to be one short-circuited condition — `TryRead(...) && builder.Add(...)` —
        // so they could not be told apart even in principle. They are not the same event: a release
        // carrying no video is routine (image sets, comics, audio-only, which is what `HasVideo` is
        // for), and a file that will not parse is a defect in the folder.
        Write("broken.torrent", "this is not bencode"u8.ToArray());
        Write("gallery.torrent", TorrentBytes.MultiFile(
            "Photo Set",
            ("Photo Set/001.jpg", 900_000L),
            ("Photo Set/002.jpg", 910_000L)));

        var report = Reload();

        Assert.Equal(0, report.Torrents);
        Assert.Equal(1, report.Skipped.Malformed);
        Assert.Equal(1, report.Skipped.WithoutVideo);
        Assert.Equal(2, report.Skipped.Total);
    }

    // ---------------------------------------------------------------------
    // Duplicates
    // ---------------------------------------------------------------------

    [Fact]
    public void Counts_the_second_copy_of_a_torrent_rather_than_dropping_it_silently()
    {
        // Identity is the contents, not the path, so two names for the same bytes are one
        // torrent. That is right, and it is also why the count matters: without it a folder of
        // duplicates and a folder of one torrent report the same single indexed release.
        var bytes = TorrentBytes.SingleFile("scene.mp4", 5_387_499_251L);
        Write("scene.torrent", bytes);
        Write("scene (1).torrent", bytes);

        var report = Reload();

        Assert.Equal(1, report.Torrents);
        Assert.Equal(1, report.Skipped.Duplicates);
        Assert.Equal(1, report.Skipped.Total);
    }

    // ---------------------------------------------------------------------
    // Unreadable
    // ---------------------------------------------------------------------

    [UnixFact("Creating a dangling symlink needs developer mode on Windows. CI runs Linux.")]
    public void Counts_a_file_it_cannot_read_apart_from_one_it_cannot_parse()
    {
        // A dangling symlink is the reachable version of the real cause — a source folder on a drive
        // that went away, or a file the Cove process cannot open. `EnumerateFiles` still lists it,
        // because the name matches; the read is what fails.
        //
        // Separate from `Malformed` because the fixes are: one is a filesystem or permission problem,
        // the other is a bad file to replace.
        Write("readable.torrent", TorrentBytes.SingleFile("scene.mp4", 42L));
        File.CreateSymbolicLink(Path.Combine(_folder, "gone.torrent"), Path.Combine(_folder, "no-such-file"));

        var report = Reload();

        Assert.Equal(1, report.Torrents);
        Assert.Equal(1, report.Skipped.Unreadable);
        Assert.Equal(0, report.Skipped.Malformed);
        Assert.Equal(1, report.Skipped.Total);
    }

    [PermissionEnforcedFact("Creating a permission-denied file needs POSIX file modes. CI runs Linux.")]
    public void Counts_a_permission_denied_file_as_unreadable_not_malformed()
    {
        // The real cause behind that report, rather than the dangling-symlink stand-in above:
        // `UnauthorizedAccessException` derives from `SystemException`, not `IOException`, and a
        // permission error is the most likely reason a .torrent in an operator-chosen folder cannot be
        // read at all. Before the fix this exception escaped `ReloadIndex` uncaught.
        // Guarded again here, redundantly with the attribute's Skip, purely so the platform-compat
        // analyzer can see `SetUnixFileMode` is never reached on Windows — it cannot see through a
        // custom FactAttribute's runtime Skip.
        if (OperatingSystem.IsWindows())
            return;

        Write("readable.torrent", TorrentBytes.SingleFile("scene.mp4", 42L));
        var locked = Path.Combine(_folder, "locked.torrent");
        File.WriteAllBytes(locked, TorrentBytes.SingleFile("locked.mp4", 99L));
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            var report = Reload();

            Assert.Equal(1, report.Torrents);
            Assert.Equal(1, report.Skipped.Unreadable);
            Assert.Equal(0, report.Skipped.Malformed);
            Assert.Equal(1, report.Skipped.Total);
        }
        finally
        {
            // Restored so the fixture's own recursive delete in Dispose does not itself trip a
            // permission error while cleaning up.
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // ---------------------------------------------------------------------
    // Directories the walk cannot open
    // ---------------------------------------------------------------------

    [PermissionEnforcedFact("Creating a permission-denied directory needs POSIX file modes. CI runs Linux.")]
    public void Keeps_walking_past_a_directory_it_cannot_open_and_counts_it()
    {
        // The half deliberately left alone. Its guards are all per *file*, and
        // `SearchOption.AllDirectories` throws from the enumerator itself when a subdirectory will not
        // open — before there is any file to guard — so one locked directory aborted the whole reload
        // and the operator was told nothing about which one.
        if (OperatingSystem.IsWindows())
            return;

        Write("top.torrent", TorrentBytes.SingleFile("top.mp4", 11L));
        var open = Directory.CreateDirectory(Path.Combine(_folder, "open"));
        File.WriteAllBytes(Path.Combine(open.FullName, "nested.torrent"), TorrentBytes.SingleFile("nested.mp4", 22L));
        var locked = Directory.CreateDirectory(Path.Combine(_folder, "locked"));
        File.WriteAllBytes(Path.Combine(locked.FullName, "hidden.torrent"), TorrentBytes.SingleFile("hidden.mp4", 33L));
        File.SetUnixFileMode(locked.FullName, UnixFileMode.None);

        try
        {
            var report = Reload();

            // Both readable torrents, including the one below the readable subdirectory — the walk has
            // to descend past the locked sibling rather than stop at it.
            Assert.Equal(2, report.Torrents);
            Assert.Equal(1, report.UnreadableDirectories);

            // And *not* as a skipped file. The walk never opened the directory, so it does not know
            // how many torrents are behind it; counting it as one would state a number it cannot have,
            // in a record whose unit is files.
            Assert.Equal(0, report.Skipped.Unreadable);
            Assert.Equal(0, report.Skipped.Total);
        }
        finally
        {
            File.SetUnixFileMode(
                locked.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Counts_no_unreadable_directories_when_every_folder_opens()
    {
        // The other side of the boundary, and the reason it is worth a test of its own: the count is
        // rendered only above zero, so a walk that reported one for an ordinary nested folder would
        // put a permissions warning on a rescan that had nothing wrong with it.
        Write("top.torrent", TorrentBytes.SingleFile("top.mp4", 11L));
        var nested = Directory.CreateDirectory(Path.Combine(_folder, "nested"));
        File.WriteAllBytes(Path.Combine(nested.FullName, "deep.torrent"), TorrentBytes.SingleFile("deep.mp4", 22L));

        var report = Reload();

        Assert.Equal(2, report.Torrents);
        Assert.Equal(0, report.UnreadableDirectories);
    }

    // ---------------------------------------------------------------------
    // Oversized
    // ---------------------------------------------------------------------

    [Fact]
    public void Skips_a_file_over_the_size_cap_before_reading_it_and_counts_it_apart_from_unreadable()
    {
        // The upload endpoint has always capped a single .torrent at 8 MB; the folder-path read had no
        // cap at all, so a multi-gigabyte file dropped in a watched folder was read whole into memory.
        // The oversized fixture is otherwise perfectly valid bencode — an over-long but well-formed
        // `info.name` — so a failure here could only be the size check, never a parse failure dressed
        // up as one.
        Write("normal.torrent", TorrentBytes.SingleFile("scene.mp4", 42L));

        var huge = TorrentBytes.SingleFile(new string('a', 9 * 1024 * 1024), 1L); // ~9 MiB, past the cap.
        Write("huge.torrent", huge);

        var report = Reload();

        Assert.Equal(1, report.Torrents);
        Assert.Equal(1, report.Skipped.Oversized);
        Assert.Equal(0, report.Skipped.Unreadable);
        Assert.Equal(0, report.Skipped.Malformed);
        Assert.Equal(1, report.Skipped.Total);
    }

    [Fact]
    public void Indexes_a_file_comfortably_under_the_size_cap()
    {
        // The other side of the boundary: an ordinary-sized file must not be touched by the new check.
        Write("normal.torrent", TorrentBytes.SingleFile("scene.mp4", 42L));

        var report = Reload();

        Assert.Equal(1, report.Torrents);
        Assert.Equal(0, report.Skipped.Oversized);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private void Write(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(_folder, name), bytes);

    private TorrentMetadataExtension.IndexReloadReport Reload()
    {
        var extension = new TorrentMetadataExtension();
        extension.UseTorrentFolder(_folder);
        return extension.ReloadIndex();
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that reports itself skipped off Unix, with the reason.
///
/// xunit v2 has no dynamic skip, and a test that quietly returns is a test that reports success
/// without running — the failure that once hid sixteen of them. Setting <c>Skip</c> in the constructor is the
/// v2 way to be honest about it: the run says "skipped, because …" rather than counting a pass.
/// </summary>
internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute(string reason)
    {
        if (OperatingSystem.IsWindows())
            Skip = reason;
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for a case that needs the OS to actually enforce a permission it was
/// told to refuse.
///
/// Skipped off Unix for the same reason <see cref="UnixFactAttribute"/> is, and skipped *again* when
/// the process is privileged: root ignores the read bit on Linux, so `File.SetUnixFileMode(..., None)`
/// would silently fail to reproduce the permission error this test exists to pin. Reported as a named
/// skip rather than a quiet pass for the same reason — a test that cannot
/// exercise its guard must say so, not look like coverage it is not providing.
/// </summary>
internal sealed class PermissionEnforcedFactAttribute : FactAttribute
{
    public PermissionEnforcedFactAttribute(string reason)
    {
        if (OperatingSystem.IsWindows())
            Skip = reason;
        else if (Environment.IsPrivilegedProcess)
            Skip = "Running as root: file permission bits are not enforced, so this guard cannot be exercised here.";
    }
}
