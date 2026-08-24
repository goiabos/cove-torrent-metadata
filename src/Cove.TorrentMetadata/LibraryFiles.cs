using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.TorrentMetadata;

/// <summary>One video file the library holds, with the video it belongs to.</summary>
public sealed record LibraryFile(long Size, string Basename, int VideoId);

/// <summary>
/// The library side of every torrent-to-video question, read once and resolved in memory.
///
/// This exists because there is exactly one rule for "which video does a file of this size belong to",
/// and it is user-visible: it decides which video a batch row is about, and — since the write folder — how many of
/// a torrent's files the folder listing calls matched. Two callers answering it two ways is the failure
/// That drift already cost this codebase once, on a question with three answers rather than two.
///
/// **The read carries no parameters and does not grow with the torrent folder.** That is the whole
/// point of loading the library wholesale and intersecting in memory rather than asking the database
/// for files whose size a torrent claims: the filter that reads as the cheap direction is the
/// expensive one, measured at 1375 ms against 10 ms for the same data. Do not reintroduce a
/// <c>WHERE Size IN (…)</c> here.
/// </summary>
public static class LibraryFiles
{
    /// <summary>Every library file attached to a video.</summary>
    public static async Task<IReadOnlyList<LibraryFile>> LoadAsync(CoveContext db, CancellationToken ct = default) =>
        await db.VideoFiles
            .AsNoTracking()
            .Where(file => file.VideoId != null)
            .Select(file => new LibraryFile(file.Size, file.Basename, file.VideoId!.Value))
            .ToListAsync(ct);

    /// <summary>
    /// The video each of <paramref name="sizes"/> resolves to, for the sizes the library actually holds.
    ///
    /// Lowest video id wins where two library files share a size. Which one it was used to rest on
    /// whatever order the database returned — undefined, and it decides which video a row is about,
    /// the kind of choice <see cref="TorrentEntryPreference"/> already refuses to leave to enumeration
    /// order. Size is not unique at corpus scale: 2.32% of sizes are shared, worst case 35 files on one
    /// size.
    /// </summary>
    public static Dictionary<long, int> VideoIdBySize(
        IEnumerable<LibraryFile> files,
        IReadOnlySet<long> sizes) =>
        files
            .Where(file => sizes.Contains(file.Size))
            .GroupBy(file => file.Size)
            .ToDictionary(group => group.Key, group => group.Min(file => file.VideoId));
}
