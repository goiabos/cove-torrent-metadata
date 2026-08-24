using Cove.TorrentMetadata;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// <see cref="StudioMatcher.NormalizeKey"/> in isolation.
///
/// <see cref="TorrentMatchServiceTests"/> already exercises the matching *rule* end to end (exact,
/// modulo separators, never a substring). This file is narrower: it pins the crash fix and
/// re-states, directly against the function rather than through a whole proposal build, that the
/// heap fallback the fix adds changes no normalisation result.
///
/// <see cref="StudioMatcher.NormalizeKey"/> runs on a torrent's own tag-list values
/// (<c>TorrentMatchService</c> normalises every site tag to build the domain chooser, and
/// <see cref="StudioMatcher.Resolve"/>'s <c>candidates</c> parameter is exactly that), and a bencode
/// string tag entry is bounded only by <c>BencodeReader.MaxStringLength</c> (64 MB) — nothing else
/// caps it for a file that merely sits in a watched source folder. The function used to
/// <c>stackalloc</c> a buffer sized at the input's length, so one long tag value was a multi-megabyte
/// stack allocation on a ~1 MB thread stack: an uncatchable <c>StackOverflowException</c> that would
/// have taken the entire Cove host process down, from a file nobody had to open.
/// </summary>
public class StudioMatcherTests
{
    /// <summary>
    /// Large enough to provably cross the 256-char stack/heap threshold in <c>NormalizeKey</c> (by
    /// roughly three orders of magnitude), while staying small enough that the test runs instantly and
    /// allocates nothing that would itself be a burden — the point is to cross the branch boundary the
    /// fix introduces, not to reproduce the original multi-megabyte crash input.
    /// </summary>
    private const int OversizedLength = 200_000;

    [Fact]
    public void Normalizes_an_oversized_value_without_crashing_and_agrees_with_the_short_path()
    {
        // Built from a short, ordinary pattern repeated past the threshold, so the "same answer the
        // short path would give" claim is checkable: the oversized value is exactly the short value
        // repeated, and NormalizeKey is a per-character filter, so the expected result is the short
        // answer repeated the same number of times.
        const string unit = "Site-Name_42.";
        var oversized = string.Concat(Enumerable.Repeat(unit, OversizedLength / unit.Length));

        var actual = StudioMatcher.NormalizeKey(oversized);

        var expectedUnit = StudioMatcher.NormalizeKey(unit);
        var expected = string.Concat(Enumerable.Repeat(expectedUnit, OversizedLength / unit.Length));
        Assert.Equal(expected, actual);
        // Sanity: the fixture is actually exercising the heap path, not accidentally sitting under it.
        Assert.True(oversized.Length > 256);
    }

    [Fact]
    public void Resolve_still_finds_the_studio_when_a_candidate_is_oversized()
    {
        // The crash was reachable through Resolve's candidates, fed from a torrent's own tag
        // list — so the regression test for the fix has to go through Resolve, not just NormalizeKey,
        // to pin that the call site is actually safe end to end.
        // Resolve's candidates arrive already TLD-stripped (TagClassifier.ExtractStudioCandidates does
        // that before StudioMatcher ever sees a value), so the ordinary candidate here is the bare
        // domain, matching what a real call site hands in.
        var studios = new[] { new StudioCandidate(Id: 1, Name: "Lanternbay") };
        var oversizedNoise = new string('.', OversizedLength);

        var result = StudioMatcher.Resolve(["lanternbay", oversizedNoise], studios);

        Assert.Equal("Lanternbay", result.Resolved?.Name);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(257)]
    public void Normalizes_the_same_way_exactly_at_and_just_past_the_stack_threshold(int length)
    {
        // Both sides of the branch the fix adds, back to back: 256 chars takes the stackalloc path and
        // 257 takes the ArrayPool path (the threshold is internal to NormalizeKey, not part of its
        // contract — this test pins that crossing it is invisible from the outside, which is the whole
        // point of a fallback over a cap).
        var value = string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + i % 26)));

        var actual = StudioMatcher.NormalizeKey(value);

        Assert.Equal(value.ToLowerInvariant(), actual);
        Assert.Equal(length, actual.Length);
    }

    [Fact]
    public void Keeps_only_letters_and_digits_and_lowercases_them()
    {
        // The ordinary case, unchanged by the fix: separators are dropped, casing is folded, and
        // nothing else about the rule moves.
        Assert.Equal("pierfidelity", StudioMatcher.NormalizeKey("Pier Fidelity"));
        // NormalizeKey itself does not strip a TLD — that happens upstream, in
        // TagClassifier.ExtractStudioCandidates, before a site tag's value ever reaches this function.
        Assert.Equal("pierfidelitycom", StudioMatcher.NormalizeKey("pierfidelity.com"));
        Assert.Equal("boatsonbays", StudioMatcher.NormalizeKey("Boats_On-Bays!"));
    }

    [Fact]
    public void Empty_input_normalizes_to_an_empty_key()
    {
        Assert.Equal(string.Empty, StudioMatcher.NormalizeKey(string.Empty));
    }

    /// <summary>
    /// The domain guard, restated directly against the function this issue touches: normalisation is
    /// exact matching modulo separators, never a substring or edit-distance match. See
    /// <c>TorrentMatchServiceTests.Does_not_match_a_domain_that_is_merely_contained_in_a_studio_name</c>
    /// for the same guarantee pinned through a full proposal.
    /// </summary>
    [Fact]
    public void Does_not_normalize_a_prefix_to_the_same_key_as_the_word_it_prefixes()
    {
        Assert.NotEqual(StudioMatcher.NormalizeKey("pier"), StudioMatcher.NormalizeKey("pierhouse"));
        Assert.NotEqual(StudioMatcher.NormalizeKey("sun"), StudioMatcher.NormalizeKey("sunbeam"));
    }
}
