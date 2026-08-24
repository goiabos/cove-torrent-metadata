using System.Text.RegularExpressions;

namespace Cove.TorrentMetadata;

/// <summary>
/// What a .torrent offers the library: the tracker's metadata for the release, plus the identities of
/// the video files it carries.
///
/// Source-agnostic by construction. It is assembled from a <see cref="BencodeTorrent"/> (the generic
/// structure) and an <see cref="ITorrentDialect"/> (the tracker-specific part), and carries no trace
/// of either encoding — matching, review and apply consume this and never see bencode. That is the
/// seam: a new tracker family is a new <see cref="ITorrentDialect"/>, and nothing downstream changes.
/// </summary>
public sealed class TorrentRelease
{
    private static readonly Regex TorrentIdPattern = new(@"[?&]id=(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Dialects in priority order; the first that recognises a file extracts it.
    ///
    /// One entry today. It is a list rather than a hard-coded call because that is the whole point of
    /// the split — adding a family should be adding a class and a line here, not editing the parser.
    /// </summary>
    private static readonly ITorrentDialect[] Dialects = [new LuminanceDialect()];

    public required string Name { get; init; }
    public string? Title { get; init; }
    public string? CoverUrl { get; init; }
    public string? Description { get; init; }
    private readonly string? _comment;

    /// <summary>The tracker's comment URL, as the torrent carries it.</summary>
    public string? Comment
    {
        get => _comment;

        // `TorrentId` is derived here rather than on every read, because it is read constantly and the
        // derivation is a regex: `TorrentEntryPreference.Compare` reads it twice per comparison and
        // `Min` runs that once per candidate in a collision bucket.
        //
        // In the setter rather than in a lazily filled field, because this type is published into a
        // shared index and read from many requests at once. A cache is a value plus a "have I computed
        // it" flag, which is two writes for a reader to tear between; deriving at construction is
        // "compute once" with nothing to synchronise. It is a class rather than a record, so there is
        // no `with` that could copy the derived value away from the comment it came from.
        init
        {
            _comment = value;
            TorrentId = ExtractTorrentId(value);
        }
    }
    public IReadOnlyList<string> TagList { get; init; } = [];
    public IReadOnlyList<TorrentVideoFile> Videos { get; init; } = [];

    /// <summary>
    /// Which dialect produced the metadata, or null when none recognised the file.
    ///
    /// Null is not a failure: the release still carries its files and still matches, it simply has
    /// nothing to propose. Recording it is what makes "this torrent matched but offered nothing"
    /// distinguishable from "this torrent's metadata was empty".
    /// </summary>
    public string? Dialect { get; init; }

    /// <summary>
    /// The tracker's torrent id from the comment URL. Present on every sample torrent, and stable across
    /// re-uploads of the same release, so it is the natural key for a <c>VideoRemoteId</c> — which is what
    /// makes re-importing the same torrent idempotent rather than additive.
    ///
    /// Derived once, when <see cref="Comment"/> is set, and not settable on its own: the two must not be
    /// able to describe different torrents.
    /// </summary>
    public string? TorrentId { get; private init; }

    private static string? ExtractTorrentId(string? comment)
    {
        if (string.IsNullOrEmpty(comment))
            return null;

        var match = TorrentIdPattern.Match(comment);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// How many videos share this torrent's metadata. Greater than one means a pack or siterip, whose
    /// tag list is the union across every scene it contains — so the same metadata will be offered to
    /// each video and most of it will be wrong for any individual one. Surfaced so review can
    /// de-emphasise those matches rather than treating them like a single-scene hit.
    /// </summary>
    public int FanOut => Videos.Count;

    /// <summary>
    /// Torrents carrying no video are not rejected because they are packs — packs are supported — but
    /// because there is nothing to match against. Image sets, comics and audio-only releases land here.
    /// </summary>
    public bool HasVideo => Videos.Count > 0;

    /// <summary>
    /// Parses the structure, then lets the first dialect that recognises the file extract its metadata.
    ///
    /// Fails only where <see cref="BencodeTorrent.TryParse"/> fails. A torrent no dialect claims comes
    /// back with empty metadata and a null <see cref="Dialect"/> rather than being rejected — dropping
    /// it would make the watched folder silently lose files that are perfectly matchable.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out TorrentRelease release)
    {
        release = null!;
        if (!BencodeTorrent.TryParse(data, out var torrent))
            return false;

        release = From(torrent);
        return true;
    }

    /// <summary>Applies the dialects to an already-parsed torrent.</summary>
    public static TorrentRelease From(BencodeTorrent torrent)
    {
        var dialect = Array.Find(Dialects, candidate => candidate.Recognises(torrent));
        var metadata = dialect?.Extract(torrent) ?? InjectedMetadata.None;

        return new TorrentRelease
        {
            Name = torrent.Name,
            Comment = torrent.Comment,
            Videos = torrent.Videos,
            Dialect = dialect?.Name,
            Title = metadata.Title,
            CoverUrl = metadata.CoverUrl,
            Description = metadata.Description,
            TagList = metadata.TagList,
        };
    }
}
