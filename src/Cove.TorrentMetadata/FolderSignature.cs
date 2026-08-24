using System.Security.Cryptography;
using System.Text;

namespace Cove.TorrentMetadata;

/// <summary>
/// A stat-only fingerprint of the <c>.torrent</c> files under one folder, taken so a later sweep can
/// say whether that folder has changed since the index was last built.
///
/// **It never opens a file.** That is the entire point: measured over the real folder — 3,272
/// torrents, 528 MB — a sweep of this kind costs 8 ms warm and 37 ms cold, against 0.28 s warm and
/// 3.39 s cold merely to read and hash the same files, before any bencode is parsed. Detection is
/// two orders of magnitude cheaper than acting on it, which is what lets the page check on every
/// visit and still leave the rebuild manual.
///
/// **The fingerprint is the whole stat set, not a count and a newest timestamp.** That pairing is
/// the obvious cheap answer and it is wrong here: <c>cp -p</c>, <c>rsync -t</c> and most archive
/// extractions preserve the source mtime, so a torrent replaced under the same name routinely lands
/// with an *older* timestamp than the one it replaced. The count is unchanged and the newest mtime
/// is unchanged or goes backwards, so nothing is noticed. Folding size and mtime *per path* catches
/// that, and catches renames and delete-one-add-one at an unchanged count with it.
///
/// What it cannot see: a file rewritten to the same length under the same name with its mtime
/// restored. That needs deliberate effort and is invisible to anything short of reading the bytes,
/// which is the cost this type exists to avoid. Callers must therefore claim only that the folder
/// looks unchanged, never that the index is up to date.
/// </summary>
/// <param name="Path">The folder, as configured.</param>
/// <param name="Exists">Whether the folder was there. A source can sit on a drive that is not
/// mounted, which is a state of its own rather than an error or an empty folder.</param>
/// <param name="Checked">False when the sweep could not be completed — an unreadable path, or a
/// share that answered with an error. A folder that could not be checked is never reported as
/// changed: the honest answer is that we do not know.</param>
/// <param name="Files">How many <c>.torrent</c> files were seen. Held apart from the digest so an
/// added file is still caught if two digests ever collided, and because it is the cheaper half of
/// the comparison.</param>
/// <param name="Digest">The folded per-file hashes, hex. Empty when nothing was seen.</param>
public sealed record FolderSignature(string Path, bool Exists, bool Checked, int Files, string Digest)
{
    /// <summary>
    /// Fingerprints one folder, walking it the same way the index does — recursively, and
    /// <c>.torrent</c> only.
    ///
    /// "The same way" is <see cref="TorrentFileWalk"/> rather than a matching pair of arguments,
    /// because the two walks have to skip the *same* directories: one of them tolerating a locked
    /// subdirectory while the other aborts on it produces two answers about one folder.
    ///
    /// Deliberately still a second *pass* rather than something <see cref="TorrentMetadataExtension.ReloadIndex"/>
    /// accumulates as it goes. That pass stops at the index cap and skips a file for four different
    /// reasons, so a fingerprint taken from it would describe what was *indexed* rather than what is
    /// *there*, and comparing it against a full sweep would report a folder as permanently changed.
    /// Two definitions of "what is in this folder" is the drift worth 8 ms.
    /// </summary>
    public static FolderSignature Compute(string folder)
    {
        if (!Directory.Exists(folder))
            return new FolderSignature(folder, Exists: false, Checked: true, Files: 0, Digest: "");

        // Folded with XOR so the result does not depend on enumeration order, which the filesystem
        // does not promise and which differs between a fresh walk and one over a folder that has been
        // written to since. Sorting the paths first would answer the same question and cost a full
        // materialisation of every path in the folder.
        var accumulator = new byte[32];
        var files = 0;

        var walk = new TorrentFileWalk(folder);
        foreach (var file in walk.Files())
        {
            byte[] entry;
            try
            {
                entry = SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{System.IO.Path.GetRelativePath(folder, file.FullName)}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}"));
            }
            catch (IOException)
            {
                // The file went away between the walk yielding it and this reading its metadata.
                // Skipping it leaves the digest describing a folder that no longer exists, so the
                // next comparison reports a change — which is exactly what happened.
                continue;
            }

            for (var i = 0; i < accumulator.Length; i++)
                accumulator[i] ^= entry[i];
            files++;
        }

        if (walk.Failed)
        {
            // The folder is there but would not be opened at all — a permission, or a share that has
            // gone away mid-sweep. Reported as unchecked rather than as an empty folder, which would
            // read as "every torrent in it disappeared".
            //
            // A *subdirectory* that would not open is deliberately not this. The walk skips it and
            // says so, and the index walk skips the same one, so the digest describes the readable
            // part of the folder and the two sides still compare. Calling the whole folder unchecked
            // for it would answer `DiffersFrom` with "not changed" forever, which is how a locked
            // directory used to hide every edit made beside it.
            return new FolderSignature(folder, Exists: true, Checked: false, Files: 0, Digest: "");
        }

        // An empty folder digests as "" rather than as 32 zero bytes, so that the digest describes the
        // files and nothing else. Otherwise a folder that has gone missing would differ from an empty
        // one by the *shape* of its digest, and whether the folder exists at all — the thing that
        // separates an unmounted drive from one the user emptied — would never have to be compared.
        return new FolderSignature(
            folder,
            Exists: true,
            Checked: true,
            files,
            files == 0 ? "" : Convert.ToHexString(accumulator));
    }

    /// <summary>Fingerprints every folder, in the order given.</summary>
    public static IReadOnlyList<FolderSignature> Snapshot(IEnumerable<string> folders) =>
        [.. folders.Select(Compute)];

    /// <summary>
    /// Whether this folder looks different from <paramref name="other"/>.
    ///
    /// Either side being unchecked answers false. A folder we could not read is not evidence of a
    /// change, and treating it as one would nag every time a network share was slow — the user would
    /// rescan, learn nothing, and be told the same thing again.
    /// </summary>
    public bool DiffersFrom(FolderSignature other) =>
        Checked && other.Checked
        && (Exists != other.Exists || Files != other.Files || !string.Equals(Digest, other.Digest, StringComparison.Ordinal));
}
