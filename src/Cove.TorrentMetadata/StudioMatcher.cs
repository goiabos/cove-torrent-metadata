using System.Buffers;

namespace Cove.TorrentMetadata;

/// <summary>A studio the library holds, reduced to what the match needs.</summary>
public sealed record StudioCandidate(int Id, string Name);

/// <summary>A studio the reviewer could choose, and the site tag that found it.</summary>
/// <param name="Studio">The library row, named as the library names it.</param>
/// <param name="Key">The normalised key both sides were reduced to, so the caller can recover the
/// tracker's spelling of the domain without the matcher having to carry it.</param>
public sealed record StudioOption(StudioCandidate Studio, string Key);

/// <summary>
/// What the library had to say about a torrent's site tags.
///
/// One type rather than three calls because the three answers are one walk of the same data, and
/// because the states are mutually exclusive in a way separate nullable returns would not enforce:
/// a proposal is resolved, or choosable, or merely countable, never two of those.
/// </summary>
public sealed record StudioMatchResult
{
    /// <summary>The single unambiguous studio, or null. Exactly what the rule below proposes.</summary>
    public StudioCandidate? Resolved { get; init; }

    /// <summary>
    /// The two studios the reviewer could pick between, or empty.
    ///
    /// Populated only at **exactly two**, which is the cap the design study settled: a shortlist drawn
    /// from five would have to order it, and tag order is the defect this rule exists to kill. Three or more
    /// get <see cref="MatchCount"/> and no options.
    /// </summary>
    public IReadOnlyList<StudioOption> Choices { get; init; } = [];

    /// <summary>
    /// How many distinct studios in the library matched, for the line that reports a count when no
    /// choice can honestly be offered.
    /// </summary>
    public int MatchCount { get; init; }
}

/// <summary>
/// Decides which studio a torrent's site tags name, or that they name none.
///
/// The counterpart to <see cref="PerformerMatcher"/>, and the same rule underneath: the library is the
/// authority, and nothing here is ever created. A studio is *linked* when it already exists and
/// otherwise skipped, because the tag list carries a bare lowercase domain and the domain is not the
/// studio's name. Creating from it claims the identity,
/// so the user can no longer add the studio properly and a merge is the only way back.
///
/// What this adds is that the library is also the **tiebreak**. A torrent naming several sites
/// used to resolve to whichever the tracker listed first, which is arbitrary: 957 of 3,218 torrents
/// carry two or more site tags, and the same network pair appears in both orders on different
/// torrents. Since a site tag matching nothing can never do anything anyway, the only candidates that
/// can matter are the ones the library holds — so resolving all of them and counting the distinct
/// studios answers the question without inventing a preference.
///
/// The megapack case falls out of that rather than needing a rule of its own: a torrent spanning
/// seventeen sites hits either none of them or several, and both answers are "propose nothing", which
/// is right because a release covering thirty-one scenes across seventeen sites has no studio. That is
/// the same argument that excludes packs from bulk apply.
///
/// Order does not enter into it, which is the whole point — <see cref="Resolve"/> reads every candidate
/// before answering, so the same tag list in either order gives the same studio.
/// </summary>
public static class StudioMatcher
{
    /// <summary>
    /// The key both sides of the comparison are reduced to before they are compared: lowercase, with
    /// every non-alphanumeric character removed.
    ///
    /// This is what lets a domain match a studio at all. A domain physically cannot contain a space and
    /// a curated studio name almost always does, so comparing <c>pierfidelity</c> to
    /// <c>Pier Fidelity</c> raw compares two things that can never agree — which is why three-quarters
    /// of torrents resolved no studio before this.
    ///
    /// It is exact matching modulo separators, **not a fuzzy search**, and the distinction is the whole
    /// licence for doing it: there is no score, no threshold and no nearly-right. A substring or
    /// edit-distance match would link <c>pier</c> to <c>pierhouse</c> and <c>sun</c> to
    /// <c>sunbeam</c>, and studios are never guessed.
    ///
    /// <para>
    /// The output can never be longer than <paramref name="value"/>, so the buffer used to be
    /// stack-allocated at exactly <c>value.Length</c>. That is fine for a library studio name, but this
    /// runs on a torrent's own tag-list values too — <see cref="Resolve"/>'s <c>candidates</c> and the
    /// site-tag values <c>TorrentMatchService</c> normalises for the domain chooser — and a bencode
    /// string tag entry is bounded only by <c>BencodeReader.MaxStringLength</c> (64 MB). Nothing else
    /// caps it for a file that merely sits in a watched source folder: a single ~1M-char tag value made
    /// this a multi-megabyte <c>stackalloc</c> on a ~1 MB thread stack, which is an <b>uncatchable</b>
    /// <see cref="StackOverflowException"/> that kills the entire Cove host process — from a file
    /// nobody had to open, let alone apply.
    /// </para>
    /// <para>
    /// The fix is a heap fallback, not a length cap: capping would change what a very long value
    /// normalises to (or refuse it outright), and that is a matching-rule change that would need its own
    /// argument about why no real studio name or site tag could ever be that long. A fallback changes
    /// nothing about the result for any input — short values still take the identical stack path, and
    /// anything over the threshold takes the same loop over rented heap memory instead
    /// (<see cref="ArrayPool{T}.Shared"/>, returned in every case via <c>finally</c>). The threshold
    /// itself (256 chars, 512 bytes on the stack) is generous for any domain or studio name and tiny next
    /// to the ~1 MB stack budget, so it costs nothing on the path that matters for latency.
    /// </para>
    /// <para>
    /// This is the only layer that has to hold: capping the value at the call site as well would be
    /// redundant defence against the same crash, and a value-length cap upstream is really a bound on
    /// what a bencode string may be at all — that is the read-size and node-count hardening tracked as
    /// this issue's companion, not a studio-matching concern.
    /// </para>
    /// </summary>
    public static string NormalizeKey(string value)
    {
        const int StackThreshold = 256;

        if (value.Length <= StackThreshold)
        {
            Span<char> buffer = stackalloc char[value.Length];
            return NormalizeInto(value, buffer);
        }

        var rented = ArrayPool<char>.Shared.Rent(value.Length);
        try
        {
            return NormalizeInto(value, rented);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static string NormalizeInto(string value, Span<char> buffer)
    {
        var length = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[length++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..length]);
    }

    /// <summary>
    /// What the library says about these site tags: one studio, two to choose between, or a count.
    ///
    /// The single studio is returned as the **library's own spelling**, not the domain that found it —
    /// the library names its own studios, exactly as a resolved tag keeps the library's spelling rather
    /// than the tracker's.
    ///
    /// <paramref name="studios"/> is the whole studio table. It used to be only the rows a SQL
    /// <c>WHERE</c> had already matched, which was cheaper and could not survive a multi-word name: the comparison
    /// is a .NET string reduction, and no provider-portable query expresses it. The same reasoning Cove
    /// 1.3 gives for evaluating its own namespace keys in memory rather than in SQL.
    /// </summary>
    public static StudioMatchResult Resolve(
        IEnumerable<string> candidates,
        IReadOnlyCollection<StudioCandidate> studios)
    {
        if (studios.Count == 0)
            return new StudioMatchResult();

        var byKey = new Dictionary<string, List<StudioCandidate>>(StringComparer.Ordinal);
        foreach (var studio in studios)
        {
            var key = NormalizeKey(studio.Name);
            if (key.Length == 0)
                continue;

            if (!byKey.TryGetValue(key, out var bucket))
                byKey[key] = bucket = [];
            bucket.Add(studio);
        }

        var options = new List<StudioOption>();
        var matchedIds = new HashSet<int>();
        // Two studios under one key is a library holding the same studio twice — "Pier Fidelity"
        // beside "Pier-Fidelity" — which normalising makes visible rather than creates. Such a key is
        // counted but never offered: which row the user meant is unanswerable, and picking between two
        // spellings of one studio is a library repair rather than a metadata decision.
        //
        // Still ordinarily reachable, which is worth saying because Cove's unique studio names look
        // like they closed it: EntityNameRules.StudioIdentityKey is trim-plus-lowercase while this key
        // keeps only alphanumerics, so one separator is all it takes to be two identities to the host
        // and one key here. The pair differing only by case is the half the host does prevent.
        var ambiguousKey = false;

        // De-duplicated on the same key the lookup uses, so two spellings of one site cannot read as two
        // candidates disagreeing.
        foreach (var key in candidates.Select(NormalizeKey).Where(key => key.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (!byKey.TryGetValue(key, out var matches))
                continue;

            foreach (var studio in matches)
                matchedIds.Add(studio.Id);

            if (matches.Count > 1)
                ambiguousKey = true;
            else
                options.Add(new StudioOption(matches[0], key));
        }

        return new StudioMatchResult
        {
            // One studio, found under one unambiguous key. Anything else is not a proposal.
            Resolved = !ambiguousKey && options.Count == 1 ? options[0].Studio : null,
            // Exactly two, each from its own key. The cap is the design decision, not a limit of the
            // walk above: offering three is a shortlist, and a shortlist has to be ordered.
            Choices = !ambiguousKey && options.Count == 2 ? options : [],
            MatchCount = matchedIds.Count,
        };
    }
}
