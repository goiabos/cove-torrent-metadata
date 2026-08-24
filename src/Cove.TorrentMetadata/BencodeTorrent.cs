namespace Cove.TorrentMetadata;

/// <summary>A video file inside a torrent payload. <see cref="Length"/> is the match key.</summary>
public sealed record TorrentVideoFile(string Path, long Length)
{
    public string Basename => Path.AsSpan()[(Path.LastIndexOf('/') + 1)..].ToString();
}

/// <summary>
/// A .torrent read as bencode and nothing more: the BEP-3 structure every torrent has, with no
/// interpretation of anything a tracker added on top of it.
///
/// This is the dialect-free half of the parse. Which keys a particular tracker family injects, and
/// what they mean, is knowledge that lives in an <see cref="ITorrentDialect"/> — which reads them off
/// <see cref="Root"/>. Splitting it here is what lets a second tracker family become one more class
/// rather than a second parser: everything downstream consumes <see cref="TorrentRelease"/> and never
/// sees a <see cref="BencodeValue"/>.
/// </summary>
public sealed class BencodeTorrent
{
    private static readonly string[] VideoExtensions =
        [".mp4", ".mkv", ".avi", ".wmv", ".m4v", ".mov", ".ts", ".flv", ".mpg", ".mpeg", ".webm"];

    /// <summary>The payload's name — <c>info.name</c>, the file for a single-file torrent.</summary>
    public required string Name { get; init; }

    /// <summary>Top-level <c>comment</c>. Trackers put the release's own URL here.</summary>
    public string? Comment { get; init; }

    public IReadOnlyList<TorrentVideoFile> Videos { get; init; } = [];

    /// <summary>
    /// The top-level dictionary, for a dialect to read tracker-injected keys out of.
    ///
    /// Deliberately not exposed beyond <see cref="TorrentRelease.TryRead"/> and the dialects: a
    /// consumer that reaches in here has reintroduced the coupling this split removes.
    /// </summary>
    public required BencodeValue Root { get; init; }

    /// <summary>
    /// Reads the structure. Fails only on things that make a file not a torrent at all — unreadable
    /// bencode, no <c>info</c> dictionary, no name. A torrent no dialect recognises still parses; it
    /// simply has nothing to propose.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out BencodeTorrent torrent)
    {
        torrent = null!;
        if (!BencodeReader.TryParse(data, out var root) || root.Kind != BencodeValue.ValueKind.Dictionary)
            return false;

        var info = root["info"];
        if (info.Kind != BencodeValue.ValueKind.Dictionary)
            return false;

        var name = info["name"].AsString();
        if (string.IsNullOrEmpty(name))
            return false;

        torrent = new BencodeTorrent
        {
            Name = name,
            Comment = root["comment"].AsString(),
            Videos = ReadVideoFiles(info, name),
            Root = root,
        };
        return true;
    }

    /// <summary>
    /// Every video in the payload becomes a match candidate, not just the largest one. A pack must be
    /// able to match each of its scenes independently; keying on a single "main" file would let a
    /// fifty-scene torrent match exactly one video.
    /// </summary>
    private static List<TorrentVideoFile> ReadVideoFiles(BencodeValue info, string name)
    {
        var videos = new List<TorrentVideoFile>();

        // Single-file torrent: `info.name` is the file itself and `info.length` its size.
        if (!info.Has("files"))
        {
            if (info["length"].AsInteger() is { } length && IsVideo(name))
                videos.Add(new TorrentVideoFile(name, length));
            return videos;
        }

        foreach (var file in info["files"].AsList())
        {
            if (file["length"].AsInteger() is not { } length)
                continue;

            var segments = file["path"].AsStringList().ToList();
            if (segments.Count == 0)
                continue;

            var path = string.Join('/', segments);
            if (IsVideo(path))
                videos.Add(new TorrentVideoFile(path, length));
        }

        return videos;
    }

    private static bool IsVideo(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension)
            && VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
