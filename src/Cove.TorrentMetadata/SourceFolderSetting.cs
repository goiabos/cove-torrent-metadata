namespace Cove.TorrentMetadata;

/// <summary>
/// Reading and writing the list of folders torrents are read from.
///
/// Stored newline-separated, the same shape as <see cref="CoverHostSetting"/> and for the same
/// reason: a value the operator may one day have to read or repair by hand should look like what
/// they typed.
///
/// Newlines are the *only* separator here, unlike the host list. A path legitimately contains spaces,
/// and on Windows it contains a colon; splitting on those would cut working folders in half and give
/// back two entries that address nothing.
/// </summary>
public static class SourceFolderSetting
{
    public const string SettingKey = "sourceFolders";

    private static readonly char[] Separators = ['\n', '\r'];

    /// <summary>Splits a stored or submitted value into normalised, de-duplicated folders.</summary>
    public static IReadOnlyList<string> Parse(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : Clean(value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Normalises and de-duplicates already-separated entries, dropping any it cannot use.</summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string> tokens)
    {
        var folders = new List<string>();
        foreach (var token in tokens)
        {
            var folder = Normalise(token);
            if (folder is not null && !folders.Contains(folder, PathComparer))
                folders.Add(folder);
        }

        return folders;
    }

    public static string Serialize(IEnumerable<string> folders) => string.Join('\n', folders);

    /// <summary>
    /// Case-sensitivity follows the platform: two entries differing only in case are one folder on
    /// Windows and two on Linux, and treating them alike on Linux would silently drop a real folder.
    /// </summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The comparison behind <see cref="PathComparer"/>, for callers that need a prefix
    /// test rather than an equality one — containment of a path inside a folder, in particular.</summary>
    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Reduces an entry to a usable absolute folder, or null if it is not one.
    ///
    /// **Relative paths are refused** rather than resolved. They would resolve against the server
    /// process's working directory, which is not a place the operator chose and not one they can see —
    /// a setting that silently addresses somewhere else is worse than a setting that refuses.
    ///
    /// **A filesystem root is refused** too. The index enumerates with
    /// <c>SearchOption.AllDirectories</c>, so a root would walk the whole disk on every reload; a
    /// mistyped path should not be able to turn a rescan into a filesystem crawl.
    /// </summary>
    private static string? Normalise(string token)
    {
        var path = token.Trim();
        if (path.Length == 0 || !Path.IsPathRooted(path))
            return null;

        string full;
        try
        {
            // Collapses "..", doubled separators and a trailing one, so two spellings of a folder are
            // stored once rather than indexed twice.
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            // Invalid characters, a path too long for the platform: not a folder we can offer to read.
            return null;
        }

        if (full.Length == 0 || string.Equals(full, Path.GetPathRoot(full), PathComparison))
            return null;

        return full;
    }
}
