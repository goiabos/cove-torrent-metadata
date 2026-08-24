using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.TorrentMetadata;

/// <summary>
/// One torrent sitting in the extension's own folder, as the settings panel lists it.
/// </summary>
/// <param name="File">Path relative to the folder — the identity a remove request names, and the name
/// the user recognises, because it is what they dragged in.</param>
/// <param name="Name">The release name inside the file, or null when it will not parse. A different
/// string from <paramref name="File"/>, and the one the batch page keys its rows on.</param>
/// <param name="TorrentId">The tracker's id, or null when the file carries no recognisable comment URL.
/// Without one an apply writes no link, so <paramref name="Applied"/> can only be zero.</param>
/// <param name="VideoFiles">Video files the release carries. More than one is a pack.</param>
/// <param name="InLibrary">Of those, how many the library holds a file of that exact size for.</param>
/// <param name="Applied">Videos linked to this torrent — the count that makes a pack's partial
/// completion expressible, which is the whole reason completion was never a filesystem fact.</param>
public sealed record FolderTorrent(
    string File,
    string? Name,
    string? TorrentId,
    int VideoFiles,
    int InLibrary,
    int Applied);

/// <summary>What a remove request actually did.</summary>
/// <param name="Removed">Files deleted from disk.</param>
/// <param name="Refused">One line per file that was not, saying why.</param>
public sealed record FolderRemoveResult(int Removed, IReadOnlyList<string> Refused);

/// <summary>
/// Listing and removing the torrents in the one folder this extension writes.
///
/// It reads the folder rather than the index, and it has to: <see cref="TorrentIndexEntry"/> carries no
/// path and no source folder, so the index cannot say which of its torrents came from here — nor what
/// any of them are called on disk, which is the identity this panel names. The cost is one parse per
/// file per listing, which is a rescan of one folder rather than of every configured folder.
///
/// **Only this folder.** Source folders are the operator's and are read-only; nothing here may be
/// pointed at one, which is why every path is resolved against the folder and refused if it lands
/// outside.
///
/// Removing a torrent destroys no work. The <c>VideoRemoteId</c> Cove stores and the baseline the
/// extension stores are both keyed by (video, torrent id) and neither is touched here, so re-adding the
/// file restores the row exactly as it was — applied, with nothing left to apply. That is already what
/// happens when someone deletes a torrent from a source folder of their own; this is a second route to
/// the same effect, on the only folder we are entitled to delete from.
/// </summary>
public sealed class WriteFolderService(CoveContext db)
{
    /// <summary>
    /// Every torrent in <paramref name="folder"/>, with what the library already knows about each.
    ///
    /// A file that will not parse is listed with a null <see cref="FolderTorrent.Name"/> rather than
    /// skipped. It is the one kind of file in here that can never do anything useful, so hiding it
    /// would hide the only entry whose removal is unambiguously right.
    /// </summary>
    public async Task<IReadOnlyList<FolderTorrent>> ListAsync(string? folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return [];

        var parsed = new List<(string File, TorrentRelease? Release)>();
        // Reparse points are skipped, which is what keeps this walk inside the folder. The
        // `SearchOption.AllDirectories` overload descends into a symlinked directory, so a link here
        // pointing at a source folder made every torrent in it list as one of ours — and the panel
        // then offers a remove button beside each. The escape needed no crafted name at all; the
        // listing handed it over. Symlinked *files* are skipped by the same flag, which is right for
        // the same reason: a file that lives somewhere else is not ours to offer.
        var walk = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var path in Directory.EnumerateFiles(folder, "*.torrent", walk))
        {
            var relative = Path.GetRelativePath(folder, path);
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path, ct);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // `UnauthorizedAccessException` is a `SystemException`, not an `IOException`, and a
                // permission error is a real way for a file in this folder to be unreadable. The
                // shape mirrors `ReloadIndex`'s own read guard, which has the same defect and the same
                // fix.
                parsed.Add((relative, null));
                continue;
            }

            parsed.Add((relative, TorrentRelease.TryRead(bytes, out var release) ? release : null));
        }

        // Ordinal, so the panel's order is the same on every read. It reaches the user through a list
        // they scroll and filter, and an order that moves between loads is an order they cannot use.
        parsed.Sort((left, right) => string.CompareOrdinal(left.File, right.File));

        var sizes = parsed
            .Where(entry => entry.Release is not null)
            .SelectMany(entry => entry.Release!.Videos)
            .Select(video => video.Length)
            .ToHashSet();

        var videoIdBySize = sizes.Count == 0
            ? []
            : LibraryFiles.VideoIdBySize(await LibraryFiles.LoadAsync(db, ct), sizes);

        // Counted straight off the links rather than joined through the size match, because that is
        // what "applied" *is*. It is also why the number is right for a torrent this folder shares with
        // a source folder: the link belongs to (video, torrent id), not to a copy of a file.
        var appliedByTorrentId = await db.Set<VideoRemoteId>()
            .AsNoTracking()
            .Where(link => link.Endpoint == TorrentApplyService.RemoteIdEndpoint)
            .GroupBy(link => link.RemoteId)
            .Select(group => new { RemoteId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.RemoteId, row => row.Count, ct);

        var torrents = new List<FolderTorrent>(parsed.Count);
        foreach (var (file, release) in parsed)
        {
            if (release is null)
            {
                torrents.Add(new FolderTorrent(file, null, null, 0, 0, 0));
                continue;
            }

            var inLibrary = release.Videos.Count(video => videoIdBySize.ContainsKey(video.Length));
            var applied = release.TorrentId is { } id && appliedByTorrentId.TryGetValue(id, out var count)
                ? count
                : 0;

            torrents.Add(new FolderTorrent(
                file,
                release.Name,
                release.TorrentId,
                release.Videos.Count,
                inLibrary,
                // A link can outlive the library file it was written against, and a pack can be applied
                // to videos this copy no longer describes. Reporting more applied than the release has
                // files would read as a defect in the count rather than in the library, so it is capped
                // at what this torrent could possibly account for.
                Math.Min(applied, release.Videos.Count)));
        }

        return torrents;
    }

    /// <summary>
    /// Deletes the named files from <paramref name="folder"/>, refusing anything that is not in it.
    ///
    /// Every name is resolved against the folder and checked for containment afterwards, so
    /// <c>../../etc/passwd</c>, an absolute path and a symlink-shaped name all land in the same refusal.
    /// Refusing per file rather than failing the request is the shape every other bulk operation here
    /// already has: a caller that sent one bad name still gets the rest done, and is told which.
    /// </summary>
    public static FolderRemoveResult Remove(string? folder, IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var refused = new List<string>();
        if (string.IsNullOrWhiteSpace(folder))
            return new FolderRemoveResult(0, ["The extension's folder is not configured."]);

        var root = Path.GetFullPath(folder);
        var removed = 0;

        foreach (var name in files)
        {
            var full = Resolve(root, name);
            if (full is null)
            {
                refused.Add($"{name}: not a torrent in the extension's folder");
                continue;
            }

            try
            {
                // Deleting something already gone is the outcome the caller asked for, and File.Delete
                // agrees — it does not throw on a missing file. A stale list should not produce an error
                // the user cannot act on.
                File.Delete(full);
                removed++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                refused.Add($"{name}: {error.Message}");
            }
        }

        return new FolderRemoveResult(removed, refused);
    }

    /// <summary>
    /// The absolute path <paramref name="name"/> means inside <paramref name="root"/>, or null if it
    /// means anywhere else.
    /// </summary>
    private static string? Resolve(string root, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // Checked before the path is resolved and again as containment below. Only this extension's
        // own files may go, and only .torrent files are ever its own.
        if (!name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
            return null;

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, name));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // Lexical containment first: it costs no disk access and it is what rejects the `..` climb.
        if (!Contains(root, full))
            return null;

        // Then physical containment, because the check above is not one. `Path.GetFullPath` normalises
        // `..` and resolves nothing else, so a *directory* symlink inside the folder still reads as
        // inside it — and `File.Delete` follows it, deleting the operator's file out of a source folder
        //. The one part of a removal that comes from a browser has to be unable to address one.
        //
        // A symlinked file is deliberately still allowed through: deleting one unlinks the link and
        // leaves its target alone, so only a directory component can redirect where a name lands.
        var physicalRoot = PhysicalDirectory(root);
        var physicalParent = PhysicalDirectory(Path.GetDirectoryName(full)!);
        if (physicalRoot is null || physicalParent is null || !Contains(physicalRoot, physicalParent))
            return null;

        return Path.Combine(physicalParent, Path.GetFileName(full));
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> itself or sits under it.</summary>
    private static bool Contains(string root, string path)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var candidate = path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, SourceFolderSetting.PathComparison);
    }

    /// <summary>
    /// Where <paramref name="directory"/> physically lands, with every symlink it crosses resolved, or
    /// null if that cannot be worked out.
    ///
    /// Walked a component at a time from the filesystem root rather than resolved in one call, because
    /// .NET has no `realpath`: <c>ResolveLinkTarget</c> answers for the entry it is given and says
    /// nothing about the ancestors above it, and it is an ancestor that redirects a name.
    ///
    /// A link's target is read as the raw <see cref="FileSystemInfo.LinkTarget"/> string and rebased on
    /// the link's own parent. <c>DirectoryInfo.FullName</c> of a resolved target would resolve a
    /// relative link against the *process working directory* instead, which is not where the link is.
    ///
    /// The hop budget is for a link cycle, which the filesystem allows and this walk would otherwise
    /// follow forever.
    /// </summary>
    private static string? PhysicalDirectory(string directory)
    {
        const int MaxLinkHops = 40;

        var root = Path.GetPathRoot(directory);
        if (string.IsNullOrEmpty(root))
            return null;

        var current = root;
        var hops = 0;

        try
        {
            var parts = Path.GetRelativePath(root, directory)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                if (part == ".")
                    continue;

                current = Path.Combine(current, part);

                while (new DirectoryInfo(current).LinkTarget is { } target)
                {
                    if (++hops > MaxLinkHops)
                        return null;

                    var parent = Path.GetDirectoryName(current);
                    if (parent is null)
                        return null;

                    current = Path.GetFullPath(target, parent);
                }
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return current;
    }
}
