namespace Cove.TorrentMetadata;

/// <summary>
/// One spelling a library performer can be found under — their name, or one of their aliases.
/// </summary>
/// <param name="Id">The performer. This is what an apply links; a name is only how we found them.</param>
/// <param name="Name">Their canonical name, whatever spelling matched.</param>
/// <param name="Spelling">The name or alias to index. Equal to <paramref name="Name"/> for the former.</param>
public sealed record PerformerVocabularyEntry(int Id, string Name, string Spelling);

/// <summary>A library performer found in a tag list.</summary>
/// <param name="Id">The row to link. Carried end to end so nothing has to re-resolve a name.</param>
/// <param name="Name">Their canonical name — the library is the authority on what they are called.</param>
/// <param name="MatchedVia">
/// The tag-list entry that found them, but only when an alias did and their name did not appear at
/// all. Null is the ordinary case and means the row needs no explanation; a value is what the review
/// dialog shows beside the name, so a match the reviewer could not otherwise account for says where
/// it came from.
/// </param>
public sealed record MatchedPerformer(int Id, string Name, string? MatchedVia);

/// <summary>The result of separating performer names out of a classified tag list.</summary>
/// <param name="Performers">Library performers, each resolved to a row.</param>
/// <param name="Tags">Content tags with the performer entries removed.</param>
public sealed record PerformerSplit(IReadOnlyList<MatchedPerformer> Performers, IReadOnlyList<ClassifiedTag> Tags);

/// <summary>
/// Every dotted permutation of the library's performer names, mapped back to the performer.
///
/// Built once and reused. It is a pure function of the vocabulary, and the vocabulary is loaded once
/// per request — but <see cref="PerformerMatcher.Split(IReadOnlyList{ClassifiedTag}, IEnumerable{PerformerVocabularyEntry})"/>
/// rebuilt it on every call, which meant once per row of the batch overview: O(rows x performers)
/// where O(rows + performers) will do. Against the real corpus that was 139,141 rebuilds of a
/// 2,495-name vocabulary for one page load.
/// </summary>
public sealed class PerformerLookup(IReadOnlyDictionary<string, PerformerMatch> byDottedForm)
{
    /// <summary>Resolves a dotted tag to the performer it names, if it names one.</summary>
    public bool TryResolve(string dotted, out PerformerMatch match) =>
        byDottedForm.TryGetValue(dotted, out match!);
}

/// <summary>What a dotted form resolves to: a performer, and whether an alias is what got us there.</summary>
public sealed record PerformerMatch(int Id, string Name, bool ViaAlias);

/// <summary>
/// Separates performer names from content tags by matching against performers Cove already knows.
///
/// Luminance trackers mix performer names into the tag list as <c>first.last</c>, usually alongside the
/// reversed <c>last.first</c>, and JAV releases can carry several permutations of one person. Nothing
/// about their shape distinguishes them from content tags: <c>oil.slick</c>, <c>first.frost</c>,
/// <c>big.black.cock</c> and <c>casey.storm.chaser</c> all read as plausible names. Detecting names
/// by pattern therefore produces constant false positives, and no amount of tuning fixes that.
///
/// Matching against a known set inverts the problem and makes it exact. The cost is a dependency on the
/// library already knowing the performer — which is why this is a subtraction pass over whatever is
/// there, not a precondition. Entries that look like names but match nothing are returned as tags and
/// can be surfaced as performer candidates instead, so a torrent can also help populate the library.
///
/// The known set is deliberately every performer in the library rather than only those already on the
/// video being matched. For a pack, whose tag list is the union across all its scenes, the narrower set
/// would leave dozens of other performers' names sitting in the tag list looking like content.
///
/// It resolves to an <em>id</em> rather than a name, and that is load-bearing rather than tidy. A name
/// is not an identity in Cove: aliases do not resolve performers at all, and a performer carrying a
/// disambiguation cannot be addressed by name even under their exact canonical spelling, so anything
/// downstream that still spoke names would create duplicates beside the rows it meant to link. That
/// became enforceable at 1.3, and the answer was to stop sending names rather than to guess what one
/// meant.
/// </summary>
public static class PerformerMatcher
{
    /// <summary>
    /// Splits <paramref name="tags"/> using <paramref name="vocabulary"/>, which should be every
    /// performer name and alias in the library. Only <see cref="TorrentTagKind.Content"/> entries
    /// are considered — a name cannot be hiding in a resolution or a codec.
    /// </summary>
    public static PerformerSplit Split(
        IReadOnlyList<ClassifiedTag> tags,
        IEnumerable<PerformerVocabularyEntry> vocabulary) =>
        Split(tags, BuildLookup(vocabulary));

    /// <summary>
    /// The same split against a lookup built once, for callers splitting many tag lists against one
    /// vocabulary. <see cref="BuildLookup(IEnumerable{PerformerVocabularyEntry})"/> is the only
    /// difference between the two overloads, and it is the expensive half.
    /// </summary>
    public static PerformerSplit Split(IReadOnlyList<ClassifiedTag> tags, PerformerLookup lookup)
    {
        var performers = new List<MatchedPerformer>();
        var positionById = new Dictionary<int, int>();
        var remaining = new List<ClassifiedTag>();

        foreach (var tag in tags)
        {
            if (tag.Kind == TorrentTagKind.Content
                && lookup.TryResolve(tag.Source.ToLowerInvariant(), out var match))
            {
                var via = match.ViaAlias ? tag.Source : null;

                // A performer routinely appears under several permutations, and may appear under an
                // alias as well as their name; collapse them to one entry.
                if (positionById.TryGetValue(match.Id, out var at))
                {
                    // Their own name beats an alias. `MatchedVia` exists to say "an alias is the only
                    // reason this row is here", which stops being true the moment the name shows up.
                    if (performers[at].MatchedVia is not null && via is null)
                        performers[at] = performers[at] with { MatchedVia = null };

                    continue;
                }

                positionById[match.Id] = performers.Count;
                performers.Add(new MatchedPerformer(match.Id, match.Name, via));
                continue;
            }

            remaining.Add(tag);
        }

        return new PerformerSplit(performers, remaining);
    }

    /// <summary>
    /// Maps every dotted permutation of a known spelling back to its performer. Both word orders are
    /// indexed because the tag list carries the reversed form as a separate tag.
    ///
    /// **The vocabulary is ordered before it is indexed, and that decides collisions**. Two
    /// people can share a name, and one person's alias can equal another's name — and since both word
    /// orders are indexed, so can `first.last` against `last.first`. <c>TryAdd</c> keeps the first
    /// arrival, so without a sort the winner was whichever row the database happened to return: the
    /// same review could name a different performer on two loads. <c>StudioMatcher</c>'s vocabulary had
    /// already been ordered for exactly this reason and this one had not.
    ///
    /// Two keys, not one. A performer's own **name** beats another performer's **alias**, which is the
    /// behaviour the loaders already produced by returning every name before every alias — a property
    /// of concatenation order rather than of anything stated, which is the same kind of accident this
    /// issue is about. Cove's own rule is the argument for keeping it: aliases never resolve identity,
    /// *"because aliases are intentionally non-unique"*. Id breaks the remaining tie, matching the
    /// studio path, and <c>OrderBy</c> is stable so a performer's own entries keep their order.
    ///
    /// Public so a caller with many tag lists and one vocabulary can pay for this once — see
    /// <see cref="PerformerLookup"/>.
    /// </summary>
    public static PerformerLookup BuildLookup(IEnumerable<PerformerVocabularyEntry> vocabulary)
    {
        var lookup = new Dictionary<string, PerformerMatch>(StringComparer.OrdinalIgnoreCase);
        var ordered = vocabulary
            .OrderBy(entry => IsAlias(entry) ? 1 : 0)
            .ThenBy(entry => entry.Id);

        foreach (var entry in ordered)
        {
            var words = entry.Spelling.Split(
                (char[])[' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2)
                continue; // A single-word name is too collision-prone to subtract on.

            var match = new PerformerMatch(entry.Id, entry.Name, IsAlias(entry));

            var forward = string.Join('.', words).ToLowerInvariant();
            var reversed = string.Join('.', words.Reverse()).ToLowerInvariant();
            lookup.TryAdd(forward, match);
            lookup.TryAdd(reversed, match);
        }

        return new PerformerLookup(lookup);
    }

    /// <summary>
    /// Whether this entry is one of the performer's aliases rather than their own name.
    ///
    /// The vocabulary carries no flag for it — an entry is a name when its spelling *is* the name — so
    /// this is the single definition of that question, used both to order the vocabulary and to fill
    /// <see cref="PerformerMatch.ViaAlias"/>. Two copies would be two chances for the sort and the
    /// answer it produces to disagree.
    /// </summary>
    private static bool IsAlias(PerformerVocabularyEntry entry) =>
        !string.Equals(entry.Spelling.Trim(), entry.Name.Trim(), StringComparison.OrdinalIgnoreCase);
}
