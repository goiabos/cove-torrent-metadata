namespace Cove.TorrentMetadata;

/// <summary>
/// The one walk over a folder's <c>.torrent</c> files, taken by the index rebuild and by the folder
/// fingerprint alike.
///
/// It exists because those were two separate <c>SearchOption.AllDirectories</c> calls, and that
/// overload throws <c>UnauthorizedAccessException</c> out of the *enumerator* when a subdirectory
/// will not open — outside any per-file guard, because there is no file yet. One locked directory
/// therefore aborted a whole reload, while on the fingerprint side the same throw was caught and
/// reported as "could not check", which <see cref="FolderSignature.DiffersFrom"/> answers with "not
/// changed". Both walks agreed the folder was fine, for the same wrong reason, and making one of
/// them resilient on its own would have produced two walks that disagree — which is the shape the
/// defect took.
///
/// <c>EnumerationOptions.IgnoreInaccessible</c> is the framework's answer and is only half of it: it
/// skips the directory and says nothing, and a rescan that silently omits part of a folder is what
/// counting the skips exists to stop. So the descent is explicit, that option is deliberately off, and they
/// are counted.
///
/// **A skipped directory is not a skipped file, and the counts stay apart for it.** The rescan line's
/// skip reasons are per file; this is per directory, and an unknown number of files sit behind it.
/// Folding one into the other is a unit mix-up.
/// </summary>
/// <param name="folder">The folder to walk. A folder that is not there fails the same way one that
/// will not open does, which is why both callers ask <c>Directory.Exists</c> first: a missing source
/// on an unmounted drive is a state of its own and neither an error nor an empty folder.</param>
internal sealed class TorrentFileWalk(string folder)
{
    /// <summary>
    /// Per-directory options for the descent. <c>RecurseSubdirectories</c> stays off because the
    /// recursion is this class's; the rest restores what <c>SearchOption.AllDirectories</c> did.
    ///
    /// Both non-default values are load-bearing rather than tidiness, and each restores what
    /// <c>SearchOption.AllDirectories</c> — that is, <c>EnumerationOptions.Compatible</c> — did:
    ///
    /// <c>AttributesToSkip</c> defaults to <c>Hidden | System</c> where the old overload skipped
    /// neither, and .NET reports every dot-prefixed path on Unix as <c>Hidden</c>. Leaving the default
    /// in would quietly stop indexing every source folder kept under one.
    ///
    /// <c>IgnoreInaccessible</c> defaults to true, which is the framework's answer to this whole
    /// problem and is the wrong half of it: it makes the directory vanish and says nothing, and a
    /// rescan that silently omits part of a folder is what this refusal exists to stop. Refusing it is what
    /// lets the <c>catch</c> below see the directory at all — with it on, a locked directory simply
    /// yields nothing and is counted as empty.
    /// </summary>
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
    };

    /// <summary>
    /// Directories the walk could not open, the folder itself included. Meaningful once
    /// <see cref="Files"/> has been enumerated — before that it describes the previous walk, and
    /// after an early break it describes only what was reached.
    /// </summary>
    public int Inaccessible { get; private set; }

    /// <summary>
    /// True when the folder itself would not open, so nothing at all under it was seen.
    ///
    /// Held apart from <see cref="Inaccessible"/> because the two mean different things to a
    /// fingerprint: a folder that could not be read yields no claim about its contents, while one
    /// with a locked subdirectory yields a perfectly good claim about the rest — and, being taken the
    /// same way on both sides, one that compares.
    /// </summary>
    public bool Failed { get; private set; }

    /// <summary>
    /// Every <c>.torrent</c> under the folder, depth-first, skipping directories that will not open.
    ///
    /// Lazy across directories so a caller that stops at a cap does not pay for the rest of the tree.
    /// Within one directory the entries are materialised, because C# forbids <c>yield return</c>
    /// inside a <c>try</c> that has a <c>catch</c> — and deferring the enumeration past the guard is
    /// exactly how the throw escaped in the first place.
    /// </summary>
    public IEnumerable<FileInfo> Files()
    {
        Inaccessible = 0;
        Failed = false;

        var root = new DirectoryInfo(folder);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            List<FileInfo> files;
            List<DirectoryInfo> subdirectories;
            try
            {
                // Two enumerations rather than one over `FileSystemInfo`, so the `*.torrent` filter
                // stays the framework's. Matching it ourselves would change which names match: the
                // Win32 pattern rules the old overload used are not `EndsWith`.
                //
                // `EnumerateFiles` off a `DirectoryInfo` yields entries whose length and timestamps
                // come from the directory walk itself, so a caller that needs either pays no extra
                // syscall for it.
                files = [.. directory.EnumerateFiles("*.torrent", Options)];
                subdirectories = [.. directory.EnumerateDirectories("*", Options)];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A permission, a share that went away, or a symlink loop deep enough that the OS
                // answers ELOOP. The old overload swallowed the loop and threw on the permission;
                // both are now one thing — a directory that would not be read — and both are counted.
                if (ReferenceEquals(directory, root))
                    Failed = true;

                Inaccessible++;
                continue;
            }

            foreach (var subdirectory in subdirectories)
                pending.Push(subdirectory);

            foreach (var file in files)
                yield return file;
        }
    }
}
