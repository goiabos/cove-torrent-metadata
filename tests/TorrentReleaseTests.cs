using System.Collections.Concurrent;
using Cove.TorrentMetadata;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// Exercises the torrent reader, the index and the classifier.
///
/// The corpus is the specification here: the invariants asserted below (a tracker-injected metadata
/// block on every file, one distinct size per video, dots that are not always word separators) are what
/// the parser and classifier were designed around, so a regression in any of them means the design
/// premise no longer holds and not merely that a test needs updating.
///
/// Everything here builds its own input; the two corpus canaries at the bottom are the exception and
/// fail loudly rather than returning when no corpus is pointed at.
/// </summary>
public class TorrentReleaseTests
{
    // ---------------------------------------------------------------------
    // Bencode reader
    // ---------------------------------------------------------------------

    [Fact]
    public void Parses_the_bencode_primitives()
    {
        var value = BencodeReader.Parse("d3:agei30e4:nameli1ei2ee6:nestedd1:ai1eeee"u8);

        Assert.Equal(30L, value["age"].AsInteger());
        Assert.Equal(new long?[] { 1L, 2L }, value["name"].AsList().Select(item => item.AsInteger()).ToArray());
        Assert.Equal(1L, value["nested"]["a"].AsInteger());
    }

    [Fact]
    public void Reads_length_prefixed_strings_without_leaking_the_prefix()
    {
        // The digits before each string are bencode's length prefix, not part of the value. Read from a
        // hex view they look like trailing tracker ids on the tags, which is what this guards against.
        var value = BencodeReader.Parse("l5:1080p2:4k12:big.red.barne"u8);

        Assert.Equal(new[] { "1080p", "4k", "big.red.barn" }, value.AsStringList().ToArray());
    }

    [Theory]
    [InlineData("d3:key")]          // unterminated dictionary
    [InlineData("l")]               // unterminated list
    [InlineData("i42")]             // unterminated integer
    [InlineData("99:short")]        // length exceeds available data
    [InlineData("")]                // empty input
    public void Rejects_malformed_bencode_without_throwing(string malformed)
    {
        Assert.False(BencodeReader.TryParse(System.Text.Encoding.UTF8.GetBytes(malformed), out _));
    }

    [Fact]
    public void Rejects_a_torrent_with_no_info_dictionary()
    {
        Assert.False(TorrentRelease.TryRead("d7:comment3:abce"u8, out _));
    }

    [Fact]
    public void Reads_a_payload_path_whose_first_segment_is_empty()
    {
        // Real siterips do this: the path list opens with an empty segment, so joining the segments
        // yields "/name.mp4". 141 of the 139,141 video files in the bookmark corpus are shaped this
        // way. The leading slash must not swallow the basename, which is the only name-side signal
        // matching has once size fails.
        Assert.True(TorrentRelease.TryRead(
            System.Text.Encoding.UTF8.GetBytes(
                "d4:infod5:filesld6:lengthi4242e4:pathl0:13:rws_scene.mp4eee4:name7:siteripee"),
            out var torrent));

        var video = Assert.Single(torrent.Videos);
        Assert.Equal("/rws_scene.mp4", video.Path);
        Assert.Equal("rws_scene.mp4", video.Basename);
        Assert.Equal(4242L, video.Length);
    }

    // ---------------------------------------------------------------------
    // The dialect seam
    //
    // Parsing is split in two: BencodeTorrent reads the BEP-3 structure every torrent has, and an
    // ITorrentDialect reads whatever a tracker family injected on top of it. These pin the split
    // itself — which dialect claimed a file, what happens when none does, and that the record
    // match/apply consume carries no bencode.
    // ---------------------------------------------------------------------

    [Fact]
    public void Extracts_the_luminance_metadata_block_and_records_which_dialect_read_it()
    {
        Assert.True(TorrentRelease.TryRead(TorrentBytes(LuminanceDialect.MetadataKey), out var release));

        Assert.Equal("Luminance", release.Dialect);
        Assert.Equal("Scene Title", release.Title);
        Assert.Equal("https://img.example/cover.jpg", release.CoverUrl);
        Assert.Equal("[b]freeform[/b]", release.Description);
        Assert.Equal(["big.red.barn", "1080p"], release.TagList);
    }

    [Fact]
    public void Keeps_a_torrent_no_dialect_recognises_instead_of_dropping_its_files()
    {
        // A plain BitTorrent file, or one from a tracker family not read yet. It still carries a
        // matchable video, so refusing it here would silently lose files out of the watched folder.
        // It simply has nothing to propose, which is a different thing from being unreadable.
        Assert.True(TorrentRelease.TryRead(TorrentBytes(injectedKey: null), out var release));

        Assert.Null(release.Dialect);
        Assert.Null(release.Title);
        Assert.Empty(release.TagList);
        Assert.Equal(4242L, Assert.Single(release.Videos).Length);
    }

    [Fact]
    public void Refuses_a_metadata_key_that_is_not_a_dictionary()
    {
        // Recognition is structural rather than "the key is present". A torrent whose `metadata` is a
        // string must not be claimed and then read as though it held the four keys — the dialect would
        // be recorded on a release whose metadata is entirely absent.
        Assert.True(BencodeTorrent.TryParse(
            TorrentBytes(LuminanceDialect.MetadataKey, injectedIsDictionary: false),
            out var torrent));

        Assert.False(new LuminanceDialect().Recognises(torrent));
        Assert.Null(TorrentRelease.From(torrent).Dialect);
    }

    [Fact]
    public void Keeps_bencode_out_of_the_record_that_match_and_apply_consume()
    {
        // The seam's actual contract, and a one-property change away from being undone: reaching a
        // BencodeValue through the release would let a downstream consumer read tracker-specific keys
        // directly, and the next dialect would then have to satisfy them too.
        Assert.DoesNotContain(
            typeof(TorrentRelease).GetProperties(),
            property => property.PropertyType == typeof(BencodeValue));
    }

    // ---------------------------------------------------------------------
    // Tag classification
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("big.red.barn", "big red barn")]
    [InlineData("body.to.body.massage", "body to body massage")]
    [InlineData("69", "69")]
    [InlineData("2.man.crew", "2 man crew")]  // leading numeral is part of the phrase
    [InlineData("sammy.j", "sammy j")]              // single-letter segment is an initial
    public void Treats_dots_as_word_separators_in_content_tags(string tag, string expected)
    {
        var classified = TagClassifier.Classify(tag);

        Assert.Equal(TorrentTagKind.Content, classified.Kind);
        Assert.Equal(expected, classified.Value);
    }

    [Theory]
    [InlineData("h.264", TorrentTagKind.CodecOrContainer)]
    [InlineData("h.265", TorrentTagKind.CodecOrContainer)]
    [InlineData("x265.10bit", TorrentTagKind.CodecOrContainer)]
    [InlineData("lanternbay.com", TorrentTagKind.SiteOrStudio)]
    [InlineData("velvet.xxx", TorrentTagKind.SiteOrStudio)]
    [InlineData("2018.03.20", TorrentTagKind.Date)]
    [InlineData("1080p", TorrentTagKind.Resolution)]
    [InlineData("1920x1080", TorrentTagKind.Resolution)]
    [InlineData("born.in.1993", TorrentTagKind.PerformerAttribute)]
    [InlineData("1on1", TorrentTagKind.Configuration)]
    public void Protects_dots_that_are_not_word_separators(string tag, TorrentTagKind expected)
    {
        Assert.Equal(expected, TagClassifier.Classify(tag).Kind);
    }

    /// <summary>
    /// A name that merely ends in "ai" before a dot is not encoding provenance.
    ///
    /// The `ai\.\w+` alternative was unanchored, and the group is matched anywhere in the value, so
    /// `ai.` matched inside `mirai.hoshino` and `bonsai.garden`. Measured over the 3,218-torrent corpus
    /// it claimed 26 tags, 24 of them Japanese performer names.
    ///
    /// The consequence is bigger than a wrong label: `PerformerMatcher.Split` only
    /// considers Content, so every one of those performers was invisible to matching — and with
    /// SourceQuality no longer imported, the tag is not offered either. A word boundary is the whole
    /// fix, because there is none between the `c` of `acai` and the `ai` that follows it.
    /// </summary>
    [Theory]
    [InlineData("mirai.hoshino")]
    [InlineData("kai.rivers")]
    [InlineData("sakai.rin")]
    [InlineData("banzai.leo")]
    [InlineData("acai.bowl")]
    [InlineData("bonsai.garden")]
    public void Does_not_read_a_name_ending_in_ai_as_provenance(string tag)
    {
        Assert.Equal(TorrentTagKind.Content, TagClassifier.Classify(tag).Kind);
    }

    /// <summary>
    /// The boundary does not cost the rule what it exists for — `ai.upscale` is 37 applications in the
    /// corpus and `ai.passthrough` 3, and both still classify as provenance.
    /// </summary>
    [Theory]
    [InlineData("ai.upscale")]
    [InlineData("ai.passthrough")]
    [InlineData("4k.ai.upscale")]
    public void Still_reads_an_ai_prefixed_tag_as_provenance(string tag)
    {
        Assert.Equal(TorrentTagKind.SourceQuality, TagClassifier.Classify(tag).Kind);
    }

    [Theory]
    [InlineData("x265.reencode")]
    [InlineData("av1.reencode")]
    [InlineData("hevc.reencode")]
    public void Classifies_the_whole_reencode_family_as_provenance(string tag)
    {
        // The codec pattern's optional suffix also matches "x265.reencode"; without explicit ordering it
        // would be called a codec while its identically-shaped siblings were called provenance.
        Assert.Equal(TorrentTagKind.SourceQuality, TagClassifier.Classify(tag).Kind);
    }

    [Fact]
    public void Keeps_the_original_dotted_form_for_alias_seeding()
    {
        // Storing the source spelling as a TagAlias is what lets later torrents match exactly instead of
        // being re-normalised and re-guessed each time.
        Assert.Equal("Big.Red.Barn", TagClassifier.Classify("Big.Red.Barn").Source);
    }

    /// <summary>
    /// An empty tag-list entry is not a tag, and must not survive classification.
    ///
    /// A bencode list can hold an empty or whitespace-only string and nothing upstream strips one — a
    /// trailing separator in whatever wrote the torrent is enough. Left in, it classifies as Content
    /// with an empty value and is proposed as a tag named the empty string; Cove maps that to the
    /// literal `&lt;empty&gt;` on save and enforces the namespace, so the row permanently claims a name
    /// nobody asked for.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Drops_a_tag_list_entry_that_is_not_a_tag(string blank)
    {
        var tags = TagClassifier.ClassifyAll(["kissing", blank, "vintage"]);

        Assert.Equal(["kissing", "vintage"], tags.Select(tag => tag.Value));
    }

    [Fact]
    public void Extracts_only_a_complete_release_date()
    {
        // Tag lists routinely carry a bare year alongside a full date; only the full date is precise
        // enough to write to a video.
        var tags = TagClassifier.ClassifyAll(["2018", "2018.03", "2018.03.20"]);

        Assert.Equal(new DateOnly(2018, 3, 20), TagClassifier.ExtractDate(tags));
        Assert.Null(TagClassifier.ExtractDate(TagClassifier.ClassifyAll(["2018", "2018.03"])));
    }

    [Fact]
    public void Extracts_the_studio_domain_without_its_tld()
    {
        Assert.Equal(["lanternbay"], TagClassifier.ExtractStudioCandidates(TagClassifier.ClassifyAll(["big.waves", "lanternbay.com"])));
    }

    /// <summary>
    /// Every site tag, in the tracker's order — not the first one.
    ///
    /// 957 of the 3,218-torrent corpus carry two or more, and this shape (network plus imprint) is the
    /// common one. Returning the first made the answer depend on how an uploader wrote a title.
    /// </summary>
    [Fact]
    public void Extracts_every_studio_domain_a_torrent_names()
    {
        Assert.Equal(
            ["lanternbay", "bigblueboats"],
            TagClassifier.ExtractStudioCandidates(
                TagClassifier.ClassifyAll(["lanternbay.com", "vintage", "bigblueboats.com"])));
    }

    [Fact]
    public void Repeats_no_studio_domain_it_has_already_named()
    {
        // Two spellings of one site would otherwise read downstream as two candidates disagreeing,
        // which is the one thing that turns a resolvable studio into no studio at all.
        Assert.Equal(
            ["lanternbay"],
            TagClassifier.ExtractStudioCandidates(TagClassifier.ClassifyAll(["lanternbay.com", "Lanternbay.net"])));
    }

    // ---------------------------------------------------------------------
    // Performer subtraction
    // ---------------------------------------------------------------------

    /// <summary>
    /// A vocabulary of plain names, one performer each, ids assigned by position. Enough for the tests
    /// that care which performer was found rather than which row they are.
    /// </summary>
    /// <summary>
    /// Two performers whose spellings collide resolve to the same one whichever order they arrive in
    ///.
    ///
    /// `BuildLookup` fills its dictionary with `TryAdd` over whatever sequence it is handed, and the
    /// vocabulary is two unordered queries concatenated — so the winner was whichever row the database
    /// returned first. The studio sibling had already been given `OrderBy(Id)` for exactly this, and
    /// this one had not: the same hazard, guarded in one place and not the other.
    ///
    /// A collision is not exotic. A performer's name in dotted form is `first.last`, both word orders
    /// are indexed, and an alias is indexed the same way — so any two people sharing a name, or one
    /// person's alias equal to another's name, land on one key.
    /// </summary>
    [Fact]
    public void Resolves_a_collided_spelling_the_same_way_in_either_order()
    {
        PerformerVocabularyEntry[] ascending = [new(3, "Jane Doe", "Jane Doe"), new(7, "Jane Doe", "Jane Doe")];
        PerformerVocabularyEntry[] descending = [ascending[1], ascending[0]];

        Assert.True(PerformerMatcher.BuildLookup(ascending).TryResolve("jane.doe", out var first));
        Assert.True(PerformerMatcher.BuildLookup(descending).TryResolve("jane.doe", out var second));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(3, first.Id);
    }

    /// <summary>
    /// A performer's own name beats another performer's alias for the same spelling.
    ///
    /// This is what the vocabulary's shape already produced — the loaders return every name and then
    /// every alias, so a name reached `TryAdd` first — and it was a property of concatenation order
    /// rather than of anything stated. Cove's own rule is the argument for keeping it: performer
    /// aliases never resolve identity, "because aliases are intentionally non-unique". Making the sort
    /// say so means a loader that ever returns its two queries the other way round cannot quietly
    /// invert it.
    ///
    /// The lower id here belongs to the *alias*, so an id-only tiebreak would pick it and this test
    /// would fail — which is why the ordering has two keys rather than one.
    /// </summary>
    [Fact]
    public void Prefers_a_name_over_another_performers_alias_for_one_spelling()
    {
        PerformerVocabularyEntry[] vocabulary =
        [
            new(2, "Someone Else", "Jane Doe"),
            new(8, "Jane Doe", "Jane Doe"),
        ];

        Assert.True(PerformerMatcher.BuildLookup(vocabulary).TryResolve("jane.doe", out var match));

        Assert.Equal(8, match.Id);
        Assert.False(match.ViaAlias);
    }

    private static PerformerVocabularyEntry[] Vocabulary(params string[] names) =>
        [.. names.Select((name, index) => new PerformerVocabularyEntry(index + 1, name, name))];

    private static string[] NamesOf(PerformerSplit split) =>
        [.. split.Performers.Select(performer => performer.Name)];

    [Fact]
    public void Subtracts_known_performers_in_either_word_order()
    {
        var tags = TagClassifier.ClassifyAll(["noa.amane", "amane.noa", "big.waves", "kissing"]);

        var split = PerformerMatcher.Split(tags, Vocabulary("Noa Amane"));

        // Both permutations are the same person and must collapse to one name.
        Assert.Equal(new[] { "Noa Amane" }, NamesOf(split));
        Assert.Equal(new[] { "big waves", "kissing" }, split.Tags.Select(tag => tag.Value).ToArray());
    }

    [Fact]
    public void Splits_the_same_way_against_a_prebuilt_lookup()
    {
        var tags = TagClassifier.ClassifyAll(["noa.amane", "amane.noa", "big.waves", "kissing"]);
        var names = Vocabulary("Noa Amane");

        // The two overloads have to agree, because callers now choose between them on cost grounds
        // alone: a batch builds the lookup once and reuses it across every row, while a single
        // match still passes the raw vocabulary. A divergence would mean the overview and the review
        // dialog disagreed about what is a performer.
        var direct = PerformerMatcher.Split(tags, names);
        var prebuilt = PerformerMatcher.Split(tags, PerformerMatcher.BuildLookup(names));

        Assert.Equal(direct.Performers, prebuilt.Performers);
        Assert.Equal(direct.Tags.Select(tag => tag.Value), prebuilt.Tags.Select(tag => tag.Value));
        Assert.Equal(new[] { "Noa Amane" }, NamesOf(prebuilt));
    }

    [Fact]
    public void Reuses_one_lookup_across_many_tag_lists()
    {
        var lookup = PerformerMatcher.BuildLookup(Vocabulary("Noa Amane", "Angela Frost"));

        // The lookup is shared state now, so it has to stay a pure read: splitting one tag list must
        // not change what the next one resolves to. Nothing else in the suite would notice, because
        // every other test builds a lookup and uses it once.
        var first = PerformerMatcher.Split(TagClassifier.ClassifyAll(["noa.amane", "kissing"]), lookup);
        var second = PerformerMatcher.Split(TagClassifier.ClassifyAll(["angela.frost", "noa.amane"]), lookup);

        Assert.Equal(new[] { "Noa Amane" }, NamesOf(first));
        Assert.Equal(new[] { "Angela Frost", "Noa Amane" }, NamesOf(second));
    }

    [Fact]
    public void Leaves_name_shaped_tags_alone_when_no_performer_matches()
    {
        // "oil.slick" and "first.frost" are indistinguishable from names by shape, which is exactly why
        // detection is done by matching a known set instead of by pattern.
        var tags = TagClassifier.ClassifyAll(["oil.slick", "first.frost", "casey.storm.chaser"]);

        var split = PerformerMatcher.Split(tags, Vocabulary("Angela Frost"));

        Assert.Empty(split.Performers);
        Assert.Equal(3, split.Tags.Count);
    }

    [Fact]
    public void Resolves_an_alias_to_the_performer_it_belongs_to()
    {
        // The library knows her as "Angela Frost" and also holds "Angela Blanche" as an alias. The
        // torrent writes the alias. The match once produced the *alias string*, which the apply
        // then handed to Cove to resolve — and from 1.3 that resolves to nothing and creates a second
        // performer. Carrying the id means nothing downstream has to ask what the name means.
        var vocabulary = new[]
        {
            new PerformerVocabularyEntry(7, "Angela Frost", "Angela Frost"),
            new PerformerVocabularyEntry(7, "Angela Frost", "Angela Blanche"),
        };

        var split = PerformerMatcher.Split(TagClassifier.ClassifyAll(["angela.blanche", "kissing"]), vocabulary);

        var performer = Assert.Single(split.Performers);
        Assert.Equal(7, performer.Id);
        Assert.Equal("Angela Frost", performer.Name);
        // The reviewer is shown a name the torrent never wrote, so the row says where it came from.
        Assert.Equal("angela.blanche", performer.MatchedVia);
    }

    [Fact]
    public void Prefers_the_performers_own_name_over_an_alias_that_found_them_too()
    {
        var vocabulary = new[]
        {
            new PerformerVocabularyEntry(7, "Angela Frost", "Angela Frost"),
            new PerformerVocabularyEntry(7, "Angela Frost", "Angela Blanche"),
        };

        // Both spellings are in the tag list, alias first. They are one performer, and `MatchedVia`
        // means "an alias is the only reason this row is here" — which stopped being true on the
        // second tag, so the dialog must not go on offering an explanation for a plain match.
        var split = PerformerMatcher.Split(
            TagClassifier.ClassifyAll(["angela.blanche", "angela.frost"]), vocabulary);

        var performer = Assert.Single(split.Performers);
        Assert.Equal(7, performer.Id);
        Assert.Null(performer.MatchedVia);
    }

    [Fact]
    public void Leaves_a_plain_name_match_unexplained()
    {
        var split = PerformerMatcher.Split(
            TagClassifier.ClassifyAll(["noa.amane", "amane.noa"]), Vocabulary("Noa Amane"));

        // Including the reversed permutation, which is not an alias — the tag list simply carries both
        // word orders. Flagging that would put a chip on almost every performer row in the corpus.
        Assert.Null(Assert.Single(split.Performers).MatchedVia);
    }

    [Fact]
    public void Ignores_single_word_performer_names()
    {
        // A one-word name is too collision-prone to subtract on: "deeper" is both a studio and a word.
        var tags = TagClassifier.ClassifyAll(["deeper", "kissing"]);

        Assert.Empty(PerformerMatcher.Split(tags, Vocabulary("Deeper")).Performers);
    }

    // ---------------------------------------------------------------------
    // Index and matching
    // ---------------------------------------------------------------------

    [Fact]
    public void Indexes_every_video_in_a_pack_not_just_the_largest()
    {
        // A pack has to match each of its scenes independently. Keying on one "main" file per torrent
        // would let a multi-scene release match a single video.
        var pack = BuildTorrent(("scene1.mp4", 1000L), ("scene2.mp4", 2000L), ("scene3.mp4", 3000L));
        var index = new TorrentIndex();

        Assert.True(index.Add(pack));
        Assert.Equal(3, index.Count);
        Assert.Equal(3, index.Find(1000L, null)!.FanOut);
        Assert.NotNull(index.Find(2000L, null));
        Assert.NotNull(index.Find(3000L, null));
    }

    [Fact]
    public void Prefers_single_scene_metadata_over_pack_metadata_for_the_same_file()
    {
        // Pack metadata is a union across every scene it contains, so it is mostly wrong for any one
        // video. When both describe the same file, the single-scene torrent is the better answer.
        var index = new TorrentIndex();
        index.Add(BuildTorrent(("a.mp4", 500L), ("b.mp4", 600L)));
        index.Add(BuildTorrent(("a.mp4", 500L)));

        Assert.Equal(1, index.Find(500L, null)!.FanOut);
    }

    [Fact]
    public void Matches_on_size_before_basename()
    {
        var index = new TorrentIndex();
        index.Add(BuildTorrent(("shared-name.mp4", 111L)));
        index.Add(BuildTorrent(("shared-name.mp4", 222L)));

        // An exact size match must never be overridden by a coincidental name collision.
        Assert.Equal(222L, index.Find(222L, "/library/shared-name.mp4")!.Video.Length);
    }

    [Fact]
    public void Falls_back_to_basename_when_a_known_size_matches_nothing()
    {
        var index = new TorrentIndex();
        index.Add(BuildTorrent(("renamed.mp4", 777L)));

        // The production case, and the one `Falls_back_to_basename_when_size_is_unknown` does not
        // reach: callers always pass a real `VideoFile.Size`, never null, so the fallback fires when a
        // size is *known and misses* — the user holding a re-encode under the same name. It is what
        // makes the batch page's name-only count a claim about something reachable.
        Assert.Equal(777L, index.Find(778L, "/library/renamed.mp4")!.Video.Length);
        Assert.Null(index.Find(778L, "/library/absent.mp4"));
    }

    [Fact]
    public void Falls_back_to_basename_when_size_is_unknown()
    {
        var index = new TorrentIndex();
        index.Add(BuildTorrent(("renamed.mp4", 777L)));

        Assert.NotNull(index.Find(null, "/library/renamed.mp4"));
        Assert.Null(index.Find(null, "/library/absent.mp4"));
    }

    [Fact]
    public void Refuses_to_index_a_torrent_with_no_video()
    {
        // Image sets, comics and audio-only releases have nothing to match against.
        Assert.False(new TorrentIndex().Add(BuildTorrent(("chapter.cbz", 900L))));
    }

    // ---------------------------------------------------------------------
    // Index concurrency
    //
    // The index is a singleton and two endpoints rebuild it — the reload endpoint, and the tail of
    // every upload — so two writers overlapping is reachable rather than theoretical. The index used
    // to mutate in place: a non-atomic Count++, plain lists inside a concurrent dictionary, and a
    // clear-then-refill that let a scrape match against an empty index mid-rebuild.
    //
    // These drive real threads rather than reasoning about the shape of the code. Removing the write
    // gate from TorrentIndex.Add turns Concurrent_adds_lose_nothing red immediately, which is what
    // makes it cover rather than decoration.
    // ---------------------------------------------------------------------

    [Fact]
    public void Concurrent_adds_lose_nothing()
    {
        const int writers = 8;
        const int perWriter = 40;

        var index = new TorrentIndex();

        // Every writer indexes the same sizes and the same basenames, so they contend on the same
        // buckets instead of each filling its own. That is the case that used to corrupt a List<T>.
        RunConcurrently(writers, _ =>
        {
            for (var i = 0; i < perWriter; i++)
                Assert.True(index.Add(BuildTorrent(($"scene{i}.mp4", 1000L + i))));
        });

        Assert.Equal(writers * perWriter, index.Count);
        Assert.Equal(writers * perWriter, index.All().Count);
        for (var i = 0; i < perWriter; i++)
            Assert.NotNull(index.Find(1000L + i, null));
    }

    [Fact]
    public void A_rebuild_is_never_observed_half_built()
    {
        const int entries = 200;

        var index = new TorrentIndex();
        index.Replace(BuildIndexOf(entries));

        var partial = new ConcurrentBag<int>();
        using var stop = new CancellationTokenSource();

        // A scrape matching throughout a reload. Under the old clear-then-refill it saw an empty or
        // half-filled index; a swap publishes one whole build at a time and has no such window.
        var reader = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var seen = index.All().Count;
                if (seen != entries)
                    partial.Add(seen);
                if (index.Find(1000L, null) is null)
                    partial.Add(-1);
            }
        });
        reader.Start();

        for (var round = 0; round < 200; round++)
            index.Replace(BuildIndexOf(entries));

        stop.Cancel();
        reader.Join();

        Assert.Empty(partial);
    }

    [Fact]
    public void Concurrent_rebuilds_leave_one_whole_index_and_not_a_merge()
    {
        const int entries = 60;

        var index = new TorrentIndex();

        // Two uploads finishing at once, or an upload racing a reload. A losing rebuild is discarded
        // entirely — what must never happen is the two of them interleaving into a longer index.
        RunConcurrently(6, _ =>
        {
            for (var round = 0; round < 20; round++)
                index.Replace(BuildIndexOf(entries));
        });

        Assert.Equal(entries, index.Count);
        Assert.Equal(entries, index.All().Count);
    }

    [Fact]
    public void Replace_swaps_the_whole_index_rather_than_merging_into_it()
    {
        var index = new TorrentIndex();
        index.Add(BuildTorrent(("old.mp4", 111L)));

        var builder = new TorrentIndexBuilder();
        builder.Add(BuildTorrent(("new.mp4", 222L)));
        Assert.Equal(1, builder.Count);
        index.Replace(builder);

        // A rebuild is a replacement, not an append — the previous contents go, by both keys.
        Assert.Null(index.Find(111L, "/library/old.mp4"));
        Assert.NotNull(index.Find(222L, null));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Refuses_to_reuse_a_builder_that_has_already_been_published()
    {
        var builder = new TorrentIndexBuilder();
        builder.Add(BuildTorrent(("a.mp4", 1L)));

        var index = new TorrentIndex();
        index.Replace(builder);

        // Replace takes ownership of the builder's collections rather than copying them, which is what
        // makes a rebuild one pass instead of two. Adding afterwards would be writing into a live index
        // behind its readers, so it is refused rather than silently allowed.
        Assert.Throws<InvalidOperationException>(() => builder.Add(BuildTorrent(("b.mp4", 2L))));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void A_builder_refuses_a_torrent_with_no_video_just_as_the_index_does()
    {
        var builder = new TorrentIndexBuilder();

        Assert.False(builder.Add(BuildTorrent(("chapter.cbz", 900L))));
        Assert.Equal(0, builder.Count);
    }

    /// <summary>A complete index of <paramref name="entries"/> videos, ready to swap in.</summary>
    private static TorrentIndexBuilder BuildIndexOf(int entries)
    {
        var builder = new TorrentIndexBuilder();
        for (var i = 0; i < entries; i++)
            builder.Add(BuildTorrent(($"scene{i}.mp4", 1000L + i)));

        return builder;
    }

    /// <summary>
    /// Runs <paramref name="body"/> on <paramref name="threads"/> dedicated threads that all start
    /// together, and rethrows whatever any of them threw.
    ///
    /// Dedicated threads and a barrier rather than <c>Parallel.For</c>: the thread pool is free to run
    /// the iterations one after another, which would make a race test pass by never racing.
    /// </summary>
    private static void RunConcurrently(int threads, Action<int> body)
    {
        using var start = new Barrier(threads);
        var failures = new ConcurrentBag<Exception>();
        var running = new Thread[threads];

        for (var i = 0; i < threads; i++)
        {
            var ordinal = i;
            running[i] = new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    body(ordinal);
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            });
            running[i].Start();
        }

        foreach (var thread in running)
            thread.Join();

        if (!failures.IsEmpty)
            throw new AggregateException(failures);
    }

    [Fact]
    public void Reads_the_torrent_id_from_the_comment_url()
    {
        var torrent = new TorrentRelease
        {
            Name = "scene",
            Comment = "https://tracker.example/torrents.php?id=1133888",
        };

        Assert.Equal("1133888", torrent.TorrentId);
    }

    // ---------------------------------------------------------------------
    // Opt-in corpus canaries
    //
    // These are the only assertions here that cannot be made synthetically: they are empirical
    // claims about what a real tracker actually emits, so building the input would make them circular. Their
    // value is as a canary on the tracker's format, not as regression cover.
    //
    // No torrent is committed to this repo, so they read a corpus from TORRENT_CORPUS_DIR and
    // *skip* when it is unset — never silently pass. Everything else in this file is synthetic and
    // runs everywhere.
    // ---------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Corpus")]
    public void Every_sample_torrent_carries_the_tracker_metadata_block()
    {
        var files = CorpusFiles();
        Assert.False(files.Count == 0, NoCorpus);

        foreach (var file in files)
        {
            Assert.True(TorrentRelease.TryRead(File.ReadAllBytes(file), out var torrent), $"failed to parse {Path.GetFileName(file)}");
            // The block is injected server-side at upload, which is why it survives 29 different torrent
            // clients across the corpus. If this ever fails, the tag list is no longer dependable.
            Assert.NotEmpty(torrent.TagList);
            Assert.False(string.IsNullOrWhiteSpace(torrent.Title));
            Assert.NotNull(torrent.TorrentId);
        }
    }

    [Fact]
    public void Admits_any_torrent_that_contains_a_video_however_many_files_it_carries()
    {
        var index = new TorrentIndex();

        // File count is not a pack filter. The largest real sample was 354 files — one ordinary scene
        // plus a 353-image set — and it must be admitted on the strength of the one video. Only a
        // payload with no video at all is rejected.
        var scenePlusImageSet = BuildTorrent(
            [("scene.mp4", 900L), .. Enumerable.Range(0, 353).Select(i => ($"stills/{i:D3}.jpg", 10L + i))]);
        var imagesOnly = BuildTorrent([("stills/001.jpg", 10L), ("stills/002.jpg", 11L)]);

        Assert.True(index.Add(scenePlusImageSet));
        Assert.False(index.Add(imagesOnly));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    [Trait("Category", "Corpus")]
    public void No_content_tag_in_the_corpus_keeps_a_dot()
    {
        var files = CorpusFiles();
        Assert.False(files.Count == 0, NoCorpus);

        foreach (var file in files)
        {
            if (!TorrentRelease.TryRead(File.ReadAllBytes(file), out var torrent))
                continue;

            foreach (var tag in TagClassifier.ClassifyAll(torrent.TagList).Where(t => t.Kind == TorrentTagKind.Content))
                Assert.DoesNotContain('.', tag.Value);
        }
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// A single-file torrent, optionally carrying a tracker-injected block under
    /// <paramref name="injectedKey"/>.
    ///
    /// Written out as real bytes rather than constructing a <see cref="TorrentRelease"/> directly,
    /// because these tests are about the parse and the dialect choice, which a hand-built record would
    /// skip entirely. Invented rather than transcribed: no test may depend on a real torrent,
    /// and a two-tag list where each tag drives one assertion reads better than a real forty-tag one.
    /// </summary>
    private static byte[] TorrentBytes(string? injectedKey = null, bool injectedIsDictionary = true)
    {
        var buffer = new MemoryStream();
        buffer.WriteByte((byte)'d');

        WriteBencodeString(buffer, "comment");
        WriteBencodeString(buffer, "https://tracker.example/torrents.php?id=1133888");

        WriteBencodeString(buffer, "info");
        buffer.WriteByte((byte)'d');
        WriteBencodeString(buffer, "length");
        buffer.Write(System.Text.Encoding.ASCII.GetBytes("i4242e"));
        WriteBencodeString(buffer, "name");
        WriteBencodeString(buffer, "scene.mp4");
        buffer.WriteByte((byte)'e');

        if (injectedKey is not null)
        {
            WriteBencodeString(buffer, injectedKey);
            if (injectedIsDictionary)
            {
                buffer.WriteByte((byte)'d');
                WriteBencodeString(buffer, "cover url");
                WriteBencodeString(buffer, "https://img.example/cover.jpg");
                WriteBencodeString(buffer, "description");
                WriteBencodeString(buffer, "[b]freeform[/b]");
                WriteBencodeString(buffer, "taglist");
                buffer.WriteByte((byte)'l');
                WriteBencodeString(buffer, "big.red.barn");
                WriteBencodeString(buffer, "1080p");
                buffer.WriteByte((byte)'e');
                WriteBencodeString(buffer, "title");
                WriteBencodeString(buffer, "Scene Title");
                buffer.WriteByte((byte)'e');
            }
            else
            {
                WriteBencodeString(buffer, "not a dictionary");
            }
        }

        buffer.WriteByte((byte)'e');
        return buffer.ToArray();
    }

    /// <summary>Bencode length-prefixes in bytes, not characters.</summary>
    private static void WriteBencodeString(Stream to, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        to.Write(System.Text.Encoding.ASCII.GetBytes($"{bytes.Length}:"));
        to.Write(bytes);
    }

    private static TorrentRelease BuildTorrent(params (string Path, long Length)[] files) => new()
    {
        Name = "test",
        Videos = [.. files
            .Where(file => file.Path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            .Select(file => new TorrentVideoFile(file.Path, file.Length))],
    };

    private const string NoCorpus =
        "TORRENT_CORPUS_DIR is not set to a folder of .torrent files. The corpus canaries are excluded from "
        + "a default run; you have asked for them explicitly, so this is a failure rather than a pass.";

    /// <summary>
    /// The opt-in corpus, from <c>TORRENT_CORPUS_DIR</c>.
    ///
    /// This used to walk up from the build output looking for a <c>resources/</c> directory, which was
    /// silently fragile: moving the test binary or the corpus broke it, and the callers returned early
    /// rather than failing, so the suite stayed green while testing nothing. Both happened. An
    /// explicit environment variable cannot break by accident — it is either set or it is not.
    /// </summary>
    private static List<string> CorpusFiles()
    {
        var directory = Environment.GetEnvironmentVariable("TORRENT_CORPUS_DIR");
        return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
            ? []
            : [.. Directory.GetFiles(directory, "*.torrent")];
    }
}
