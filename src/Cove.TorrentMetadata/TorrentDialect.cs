namespace Cove.TorrentMetadata;

/// <summary>
/// What a tracker injected into a .torrent, extracted and stripped of how it was encoded.
///
/// The four fields are the whole contract between a dialect and the rest of the extension. A dialect
/// that finds nothing returns <see cref="None"/> rather than failing: a torrent from somewhere else
/// is still a valid torrent with matchable files, it just proposes nothing.
/// </summary>
public sealed record InjectedMetadata(
    string? Title,
    string? CoverUrl,
    string? Description,
    IReadOnlyList<string> TagList)
{
    public static InjectedMetadata None { get; } = new(null, null, null, []);
}

/// <summary>
/// Reads one tracker family's injected metadata out of a parsed torrent.
///
/// The extension supports a dialect, not a site. Everything downstream consumes
/// <see cref="TorrentRelease"/> and cannot tell which implementation produced it, so a tracker family
/// that injects comparable data becomes another implementation here rather than another extension —
/// and the classifier, matcher, index, review UI and batch pipeline are all reused as they are.
/// </summary>
public interface ITorrentDialect
{
    /// <summary>Name of the tracker family, recorded on the release for diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// True when this dialect's own metadata is present. Cheap and structural — it decides which
    /// dialect extracts, so it must not be a guess based on values.
    /// </summary>
    bool Recognises(BencodeTorrent torrent);

    /// <summary>
    /// Extracts what is there. Total rather than fallible: a missing key is a null field, never an
    /// error, because a half-populated block is still worth proposing.
    /// </summary>
    InjectedMetadata Extract(BencodeTorrent torrent);
}

/// <summary>
/// Luminance's tracker-injected metadata block.
///
/// Luminance writes a non-standard top-level <c>metadata</c> dictionary at upload time holding
/// exactly <c>title</c>, <c>cover url</c>, <c>description</c> and <c>taglist</c> —
/// <c>Torrent::set_metadata()</c> in <c>application/Legacy/classes/Torrent.php</c>. Because the
/// tracker writes it server-side rather than the uploader's client, all four keys are present
/// regardless of which torrent client produced the file, and that uniformity is what makes the tag
/// list dependable enough to build on.
///
/// It is core Luminance rather than any one site's patch, which is what makes "works with a
/// Luminance-based tracker" a supportable claim for the parse path. The tag *conventions* are a
/// separate question — <c>TagClassifier</c> and <c>PerformerMatcher</c> were tuned against one
/// deployment's corpus.
///
/// The underscore-to-dot normalisation of tag names also happens in that function, tracker-side, so
/// the dotted spelling this extension keys on is the tracker's own and not something inferred here.
///
/// <c>description</c> is the exception to "structured": it is freeform uploader-authored BBCode,
/// captured verbatim and never parsed for structure.
/// </summary>
public sealed class LuminanceDialect : ITorrentDialect
{
    /// <summary>The top-level key. Not part of BEP-3 — that is the point of it.</summary>
    public const string MetadataKey = "metadata";

    public string Name => "Luminance";

    public bool Recognises(BencodeTorrent torrent) =>
        torrent.Root[MetadataKey].Kind == BencodeValue.ValueKind.Dictionary;

    public InjectedMetadata Extract(BencodeTorrent torrent)
    {
        var metadata = torrent.Root[MetadataKey];
        return new InjectedMetadata(
            metadata["title"].AsString(),
            metadata["cover url"].AsString(),
            metadata["description"].AsString(),
            [.. metadata["taglist"].AsStringList()]);
    }
}
