using System.Globalization;
using System.Text.RegularExpressions;

namespace Cove.TorrentMetadata;

public enum TorrentTagKind
{
    /// <summary>A genuine descriptive tag. The only kind that becomes a Cove tag.</summary>
    Content,

    /// <summary>Resolution (<c>1080p</c>, <c>1920x1080</c>) — already known from the video file.</summary>
    Resolution,

    /// <summary>Codec or container (<c>h.265</c>, <c>x265.10bit</c>, <c>mp4</c>) — already known from the video file.</summary>
    CodecOrContainer,

    /// <summary>A release date (<c>2018.03.20</c>, <c>2016.06</c>, <c>2019</c>).</summary>
    Date,

    /// <summary>A studio's site domain (<c>lanternbay.com</c>, <c>velvet.xxx</c>).</summary>
    SiteOrStudio,

    /// <summary>Scene composition (<c>1on1</c>, <c>6m3f</c>, <c>1female.3males</c>).</summary>
    Configuration,

    /// <summary>Encoding provenance (<c>x265.reencode</c>, <c>ai.upscale</c>, <c>low.bitrate</c>).</summary>
    SourceQuality,

    /// <summary>A performer attribute (<c>23.years.old</c>, <c>born.in.1993</c>) rather than a scene property.</summary>
    PerformerAttribute,
}

/// <summary>
/// A classified tag list entry.
/// </summary>
/// <param name="Source">The entry exactly as it appeared in the torrent, e.g. <c>big.red.barn</c>.
/// Worth keeping even for tags that are applied: stored as a <c>TagAlias</c> it lets every later torrent
/// match by alias instead of being re-normalised and re-guessed.</param>
/// <param name="Kind">What the entry turned out to be.</param>
/// <param name="Value">The normalised value — dots become spaces only where dots were separators.</param>
public sealed record ClassifiedTag(string Source, TorrentTagKind Kind, string Value);

/// <summary>
/// Sorts tracker tag list entries into Cove's fields.
///
/// Roughly a ninth of a typical tag list is not descriptive at all: it encodes resolution, codec,
/// release date, studio domain, or performer age. Those belong in typed fields (or nowhere, when the
/// video file already carries the fact) rather than in the tag list.
///
/// The classification also has to happen *before* any dot-to-space rewrite. Dots are usually word
/// separators, but not in <c>h.265</c>, <c>lanternbay.com</c>, <c>2018.03.20</c>, <c>sammy.j</c> or
/// <c>2.man.crew</c> — rewriting those blindly corrupts them. So each protected shape is matched
/// first and only the remainder is treated as dot-separated words.
///
/// Performer names are deliberately *not* detected here. They appear in the tag list as
/// <c>first.last</c> (and often <c>last.first</c> too) and are indistinguishable by shape from content
/// tags — <c>oil.slick</c>, <c>first.frost</c> and <c>casey.storm.chaser</c> all look like names.
/// They are resolved by matching against known performers instead; see <see cref="PerformerMatcher"/>.
/// </summary>
public static class TagClassifier
{
    private static readonly Regex Resolution = new(@"^(?:\d{3,4}p|[48]k|\d{3,4}x\d{3,4})$", RegexOptions.Compiled);
    private static readonly Regex Codec = new(@"^(?:h\.26[45]|x26[45](?:\.\w+)*|av1|hevc|avc|vp9|xvid|divx|mp4|mkv|avi|wmv|\d+bit|\d+\.fps)$", RegexOptions.Compiled);
    private static readonly Regex Date = new(@"^(?<y>(?:19|20)\d{2})(?:\.(?<m>\d{2}))?(?:\.(?<d>\d{2}))?$", RegexOptions.Compiled);
    private static readonly Regex Site = new(@"\.(?:com|net|tv|org|xxx)$", RegexOptions.Compiled);
    private static readonly Regex PerformerAttribute = new(@"^(?:\d{2}\.years\.old|\d{2}\.plus|born\.in\.(?:19|20)\d{2})$", RegexOptions.Compiled);
    private static readonly Regex Configuration = new(@"^(?:\d+on\d+(?:\.only)?|\d+p\.\d+p|\d+females?\.\d+males?|\d+males?\.\d+females?|\d+m\d+f|\d+f\d+m)$", RegexOptions.Compiled);
    // `\bai\.` and not `ai\.`: the alternation is matched anywhere in the value, so an unanchored
    // `ai\.` claimed every name ending in "ai" before a dot. Measured over the 3,218-torrent corpus it
    // took 26 tags that are not provenance at all, 24 of them Japanese performer names shaped like
    // `mirai.hoshino`, `kai.rivers`, `sakai.rin` or `banzai.leo`, plus tags shaped like `bonsai.garden`. That is worse
    // than a mislabel: `PerformerMatcher` only ever looks at Content, so those performers could not be
    // matched at all, and this kind is now dropped from proposals entirely. The boundary frees exactly
    // those 26 and re-matches none, leaving `ai.upscale` (37) and `ai.passthrough` (3) where they were.
    //
    // The other alternatives stay unanchored deliberately: `x265.reencode` (221), `re.encoded` (145),
    // `hevc.reencode` (97), `bluray.remux` and `vr.reencode.to.normal` are all genuine provenance, and
    // anchoring the group would drop 522 applications of it to fix nothing.
    private static readonly Regex SourceQuality = new(@"(?:reencode|re\.encode|upscale|remux|webrip|web\.dl|bluray|dvdrip|low\.bitrate|\bai\.\w+)", RegexOptions.Compiled);

    public static ClassifiedTag Classify(string tag)
    {
        var source = tag.Trim();
        var normalized = source.ToLowerInvariant();

        // Order matters: every protected shape must be tested before the dot-to-space fallback.
        if (Site.IsMatch(normalized))
            return new ClassifiedTag(source, TorrentTagKind.SiteOrStudio, StripTld(normalized));
        if (Resolution.IsMatch(normalized))
            return new ClassifiedTag(source, TorrentTagKind.Resolution, normalized);
        // Before the codec test: the codec pattern's optional suffix also matches "x265.reencode", which
        // is provenance rather than a codec — and classifying it as one would split it from the
        // identically-shaped "av1.reencode" and "hevc.reencode".
        if (SourceQuality.IsMatch(normalized))
            return new ClassifiedTag(source, TorrentTagKind.SourceQuality, DotsToSpaces(normalized));
        if (Codec.IsMatch(normalized))
            return new ClassifiedTag(source, TorrentTagKind.CodecOrContainer, normalized);
        if (Date.IsMatch(normalized))
            return new ClassifiedTag(source, TorrentTagKind.Date, normalized);
        if (PerformerAttribute.IsMatch(normalized))
            return new ClassifiedTag(source, TorrentTagKind.PerformerAttribute, DotsToSpaces(normalized));
        if (Configuration.IsMatch(normalized))
            return new ClassifiedTag(source, TorrentTagKind.Configuration, DotsToSpaces(normalized));

        return new ClassifiedTag(source, TorrentTagKind.Content, DotsToSpaces(normalized));
    }

    /// <summary>
    /// Classifies a whole tag list, dropping entries that are not tags at all.
    ///
    /// A bencode list can hold an empty or whitespace-only string, and nothing upstream removes one —
    /// a tracker that emits a trailing separator is enough. Carried through, it classifies as Content
    /// with an empty value, is proposed as a tag named the empty string, and the apply creates it.
    /// Cove trims a canonical name and turns an empty one into the literal <c>&lt;empty&gt;</c>, so the
    /// row that lands permanently claims that name in a namespace it enforces.
    ///
    /// Dropped here rather than in <see cref="Classify"/> because an empty entry is not a
    /// classification question: <see cref="Classify"/> stays a total function from one string to one
    /// answer, and this is the choke point both callers of a whole tag list already go through. It is
    /// not the only guard — the apply refuses a blank name too, since the request comes from a browser
    /// and never passes through here.
    /// </summary>
    public static IReadOnlyList<ClassifiedTag> ClassifyAll(IEnumerable<string> tags) =>
        tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(Classify).ToList();

    /// <summary>
    /// The most specific release date in the tag list, if any. Tag lists routinely carry both a bare
    /// year and a full date; only a complete year-month-day is precise enough to write to a video.
    /// </summary>
    public static DateOnly? ExtractDate(IEnumerable<ClassifiedTag> tags)
    {
        foreach (var tag in tags.Where(t => t.Kind == TorrentTagKind.Date))
        {
            var match = Date.Match(tag.Value);
            if (!match.Success || !match.Groups["m"].Success || !match.Groups["d"].Success)
                continue;

            if (DateOnly.TryParseExact(
                    $"{match.Groups["y"].Value}-{match.Groups["m"].Value}-{match.Groups["d"].Value}",
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date))
            {
                return date;
            }
        }

        return null;
    }

    /// <summary>
    /// Every studio hint in the tag list, each a site domain minus its TLD, in the order the tracker
    /// wrote them.
    ///
    /// **All of them, not the first.** Nearly a third of the corpus — 957 of 3,218 torrents — carries
    /// two or more site tags, and the order is not stable: the same network pair appears both ways
    /// round on different torrents, following the uploader's title rather than anything about the
    /// release. Taking the first therefore assigned two studios to two releases of one network. Which
    /// candidate is right, if any, is a question about the library and is <see cref="StudioMatcher"/>'s
    ///.
    ///
    /// Left lowercase because the original camel casing ("BigTitsRoundAsses") is not recoverable from
    /// the domain. That is also why nothing here is ever created as a studio: the domain is not the
    /// studio's name, only a handle for finding one.
    /// </summary>
    public static IReadOnlyList<string> ExtractStudioCandidates(IEnumerable<ClassifiedTag> tags) =>
        [.. tags
            .Where(tag => tag.Kind == TorrentTagKind.SiteOrStudio)
            .Select(tag => tag.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string StripTld(string domain)
    {
        var lastDot = domain.LastIndexOf('.');
        return lastDot > 0 ? domain[..lastDot] : domain;
    }

    private static string DotsToSpaces(string tag) => tag.Replace('.', ' ');
}
