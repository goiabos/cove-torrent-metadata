using System.Globalization;
using Cove.Core.Entities;
using Cove.Data;
using Cove.TorrentMetadata;
using Microsoft.EntityFrameworkCore;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// Drives <see cref="TorrentMatchService"/> against a real <see cref="CoveContext"/> and an
/// in-code torrent — the path the extension's match endpoint takes.
///
/// The unit tests in <c>TorrentReleaseTests</c> cover parsing and classification in isolation; this
/// covers identifying a library video by file size, resolving the torrent's tags and performers against
/// rows that actually exist, and labelling each one "matches existing" or "will create". Those labels
/// are the whole point of the review step, so they are asserted rather than assumed.
/// </summary>
public class TorrentMatchServiceTests
{
    private const long SampleVideoSize = 5_387_499_251L;
    private const string SampleVideoName = "sample-scene.mp4";
    private const string SampleTitle = "[SAMPLE-001] A Sample Scene";
    private const string SampleTorrentId = "1133888";
    private const string PackName = "sample-siterip";
    private const string PackOtherFileName = "another-scene.mp4";

    [Fact]
    public async Task Identifies_a_video_by_exact_file_size()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var service = new TorrentMatchService(db, IndexOf(torrent), Settings());

        var proposal = Matched(await service.MatchAsync(videoId));

        Assert.NotNull(proposal);
        Assert.Equal(videoId, proposal.VideoId);
        Assert.Equal("file size", proposal.MatchedOn);
        Assert.Equal(1, proposal.FanOut);
        Assert.Equal(SampleTitle, proposal.Title);
        Assert.Equal(SampleTorrentId, proposal.TorrentId);
    }

    [Theory]
    // The three states a reviewer can be in, answered before they tick anything.
    [InlineData("https://img.example/cover.jpg", "img.example", true)]
    [InlineData("https://img.example/cover.jpg", "other.example", false)]
    // A subdomain is admitted only where the operator marked the entry for it. That used to be
    // automatic; it is opt-in now, and the proposal has to agree with the apply on both rows —
    // the dialog disables the cover toggle off this answer.
    [InlineData("https://cdn.img.example/cover.jpg", "img.example", false)]
    [InlineData("https://cdn.img.example/cover.jpg", "*.img.example", true)]
    public async Task Says_whether_the_cover_host_is_one_covers_may_be_fetched_from(
        string coverUrl, string configuredHost, bool expected)
    {
        var torrent = SampleTorrentWithCover(coverUrl);

        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var service = new TorrentMatchService(
            db, IndexOf(torrent), Settings(), new CoverHostAllowlist([configuredHost]));

        var proposal = Matched(await service.MatchAsync(videoId));

        // Review answers this, rather than leaving it to the apply result, so the dialog can disable
        // the toggle and offer the fix instead of letting the user tick a box that cannot work.
        Assert.Equal(expected, proposal!.CoverHostAllowed);
    }

    [Fact]
    public async Task Reports_the_cover_host_as_disallowed_when_no_allowlist_is_wired()
    {
        var torrent = SampleTorrentWithCover("https://img.example/cover.jpg");

        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(torrent), Settings()).MatchAsync(videoId));

        // The optional dependency fails the same direction the apply path does. Promising a cover the
        // apply would then refuse is the one answer that must not happen.
        Assert.False(proposal!.CoverHostAllowed);
    }

    [Theory]
    [InlineData("blob-1", true)]
    [InlineData(null, false)]
    public async Task Says_whether_the_video_already_has_artwork(string? blobId, bool expected)
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize, imageBlobId: blobId);

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(SampleTorrent()), Settings()).MatchAsync(videoId));

        // The dialog opens its cover comparison unprompted when there is nothing to compare against,
        // and it used to learn that by rendering an image and waiting for the 404.
        Assert.Equal(expected, proposal!.VideoHasImage);
    }

    [Fact]
    public async Task Reports_that_no_torrent_describes_the_video()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, "unrelated.mp4", 4242L);
        var service = new TorrentMatchService(db, IndexOf(torrent), Settings());

        // A size that matches nothing must produce no proposal rather than a wrong one.
        var outcome = await service.MatchAsync(videoId);

        Assert.Equal(TorrentMatchStatus.NoTorrentMatched, outcome.Status);
        Assert.Null(outcome.Proposal);
    }

    [Fact]
    public async Task Matches_on_the_file_name_when_the_size_misses_and_says_which_it_was()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        // The same file name at a different size: the user holds a re-encode, or a different release
        // of the same scene.
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize - 1);
        var service = new TorrentMatchService(db, IndexOf(torrent), Settings());

        var outcome = await service.MatchAsync(videoId);

        // This path was live, user-visible and uncovered. `TorrentIndex.Find` falls back to the
        // basename when the size finds nothing, so the proposal is offered — and the header has to say
        // it was the name, because the guarantee the rest of the extension rests on is an exact byte
        // count and this is not it.
        Assert.Equal(TorrentMatchStatus.Matched, outcome.Status);
        Assert.Equal("file name", outcome.Proposal!.MatchedOn);
    }

    [Fact]
    public async Task Prefers_the_size_over_the_name_when_both_would_match()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var service = new TorrentMatchService(db, IndexOf(torrent), Settings());

        var outcome = await service.MatchAsync(videoId);

        // The fallback is a fallback. A coincidental name collision must never take priority over an
        // exact byte count, and the header must not describe an exact match as a heuristic one.
        Assert.Equal("file size", outcome.Proposal!.MatchedOn);
    }

    [Fact]
    public async Task Reports_a_missing_video_as_missing_rather_than_as_unmatched()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();

        var outcome = await new TorrentMatchService(db, IndexOf(torrent), Settings()).MatchAsync(9999);

        // These two used to be one bare null, which the endpoint could only report one way: someone
        // whose video had been deleted in another tab was told their torrent folder had nothing for it,
        // and sent to fix the one thing that was not wrong.
        Assert.Equal(TorrentMatchStatus.VideoNotFound, outcome.Status);
        Assert.Null(outcome.Proposal);
    }

    [Fact]
    public async Task Reports_a_missing_video_as_missing_even_when_a_torrent_would_have_matched()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        // The indexed torrent describes a file of exactly this size, so the folder is not the problem
        // under any reading. Only the video's absence can be.
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var service = new TorrentMatchService(db, IndexOf(torrent), Settings());
        db.Videos.Remove(await db.Videos.SingleAsync(video => video.Id == videoId));
        await db.SaveChangesAsync();

        var outcome = await service.MatchAsync(videoId);

        Assert.Equal(TorrentMatchStatus.VideoNotFound, outcome.Status);
    }

    [Fact]
    public async Task Labels_tags_that_already_exist_in_the_library()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        // "deep.blue.sea" is in the sample's tag list; "kissing" is too. Seed one so the proposal has
        // to distinguish an existing tag from one that would be created.
        db.Tags.Add(new Tag { Name = "Deep Blue Sea" });
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var service = new TorrentMatchService(db, IndexOf(torrent), Settings());

        var proposal = Matched(await service.MatchAsync(videoId));

        var existing = proposal!.Tags.Single(tag => tag.MatchesExisting && tag.Name == "Deep Blue Sea");
        // The library's own spelling wins over the torrent's normalised form.
        Assert.Equal("deep.blue.sea", existing.Source);
        Assert.Contains(proposal.Tags, tag => !tag.MatchesExisting);
    }

    [Fact]
    public async Task Resolves_a_tag_through_an_existing_alias()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        // The library already carries a rich alias vocabulary, so a torrent tag often resolves to an
        // existing tag under a different primary name. That must read as "matches existing".
        db.Tags.Add(new Tag { Name = "Sunshine", Aliases = [new TagAlias { Alias = "sunbeam" }] });
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var service = new TorrentMatchService(db, IndexOf(torrent), Settings());

        var proposal = Matched(await service.MatchAsync(videoId));

        Assert.Contains(proposal!.Tags, tag => tag.MatchesExisting && tag.Name == "Sunshine");
    }

    // ---------------------------------------------------------------------
    // What one apply leaves behind, the next match has to find
    //
    // These go through `TorrentApplyService` rather than seeding rows by hand, because the defect was
    // exactly that the two halves disagreed about which spelling a tag is stored under. A fixture that
    // writes the row itself would have been free to pick the spelling that works, and every alias
    // assertion in this suite did — they all used single words, where the tracker's spelling and the
    // normalised one are the same string.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Resolves_a_tag_it_created_itself_under_the_dotted_style()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var settings = new TorrentMetadataSettings();
        await settings.SetTagNameStyleAsync(TagNameStyle.Dotted);

        // The dotted style names a created tag by the tracker's own spelling, so this apply leaves a
        // tag called "big.red.barn" in the library.
        await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["big.red.barn"],
            TagSources = { ["big.red.barn"] = "big.red.barn" },
        });

        var proposal = Matched(await new TorrentMatchService(
            db, IndexOf(TorrentTagged("big.red.barn")), settings).MatchAsync(videoId));

        // Matching normalises to "big red barn" before resolving, which found nothing — so the tag
        // this extension had just created was offered back as one that would be created, and a bulk
        // apply with "create new tags" off would have declined to apply it at all.
        var tag = Assert.Single(proposal!.Tags);
        Assert.True(tag.MatchesExisting);
        Assert.Equal("big.red.barn", tag.Name);
        Assert.Single(await db.Tags.ToListAsync());
    }

    [Fact]
    public async Task Resolves_through_an_alias_an_earlier_apply_seeded()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        // The tag is named one thing and the tracker spells it another, which is the case the alias
        // seeding exists for: the source form is recorded so the next torrent resolves it.
        await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["Sunshine"],
            TagSources = { ["Sunshine"] = "sun.beam" },
        });
        Assert.True(await db.Set<TagAlias>().AnyAsync(alias => alias.Alias == "sun.beam"));

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(TorrentTagged("sun.beam")), Settings())
            .MatchAsync(videoId));

        // The assertion the suite never had. The alias is stored in source form and matching used to
        // ask only for the normalised one, so the row was written and then never read by anything.
        var tag = Assert.Single(proposal!.Tags);
        Assert.True(tag.MatchesExisting);
        Assert.Equal("Sunshine", tag.Name);
    }

    // ---------------------------------------------------------------------
    // Two entries, one name
    //
    // A mature library holds several spellings of one tag, so more than one tag-list entry routinely
    // resolves to the same row. Left as two entries the dialog gives them one React key and one
    // checkbox — `selection.tags` is a set of names — and counts a tag the apply would fold anyway.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Proposes_one_row_when_two_entries_resolve_to_the_same_library_tag()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag
        {
            Name = "Sunshine",
            Aliases = [new TagAlias { Alias = "sunbeam" }, new TagAlias { Alias = "sunray" }],
        });
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(TorrentTagged("sunbeam", "sunray")), Settings())
            .MatchAsync(videoId));

        var single = Assert.Single(proposal!.Tags);
        Assert.Equal("Sunshine", single.Name);
    }

    [Fact]
    public async Task Keeps_the_first_spelling_when_two_entries_collapse()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag
        {
            Name = "Sunshine",
            Aliases = [new TagAlias { Alias = "sunbeam" }, new TagAlias { Alias = "sunray" }],
        });
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(TorrentTagged("sunbeam", "sunray")), Settings())
            .MatchAsync(videoId));

        // Only one source survives, and it is the tag list's own order — `TagSources` carries one
        // spelling per name at both ends of the wire, so which one is a decision, not an accident.
        Assert.Equal("sunbeam", proposal!.Tags[0].Source);
    }

    [Fact]
    public async Task Collapses_entries_the_tracker_spelled_differently()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(TorrentTagged("kissing", "KISSING")), Settings())
            .MatchAsync(videoId));

        // Nothing resolves here: the classifier lowercases before styling, so two spellings of one word
        // reach the styler as one value and leave it as one name. The dedupe is the only thing standing
        // between that and two identical rows.
        var single = Assert.Single(proposal!.Tags);
        Assert.Equal("Kissing", single.Name);
    }

    [Fact]
    public async Task Collapses_entries_that_differ_only_in_case_when_the_style_keeps_the_tracker_spelling()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var settings = new TorrentMetadataSettings();
        await settings.SetTagNameStyleAsync(TagNameStyle.Dotted);

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(TorrentTagged("kissing", "KISSING")), settings)
            .MatchAsync(videoId));

        // The dotted style names a tag by the tracker's own spelling, which is the one place two names
        // can differ by case alone. They are still one tag — `ApplyTagsAsync` resolves case-insensitively
        // — so an ordinal comparer here would show a choice the apply does not offer.
        var single = Assert.Single(proposal!.Tags);
        Assert.Equal("kissing", single.Name);
    }

    [Fact]
    public async Task Leaves_genuinely_different_tags_as_separate_rows()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Sunshine", Aliases = [new TagAlias { Alias = "sunbeam" }] });
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db, IndexOf(TorrentTagged("sunbeam", "kissing", "moonlight")), Settings()).MatchAsync(videoId));

        // The failure worth guarding against is the opposite one: a fold that swallows real choices is
        // worse than the duplicates it was added to remove.
        Assert.Equal(3, proposal!.Tags.Count);
        Assert.Equal(["Sunshine", "Kissing", "Moonlight"], proposal.Tags.Select(tag => tag.Name));
    }

    [Fact]
    public async Task Proposes_an_alias_matched_performer_under_the_name_the_library_uses()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        // The library calls her "Jane Roe" and holds "jane doe" as an alias; the torrent writes both
        // spellings of the alias and neither of her own name. The proposal once carried the
        // alias string, which is what the apply then sent to Cove — and from 1.3 that resolves to
        // nothing and creates a second performer beside her.
        var performer = new Performer { Name = "Jane Roe", Aliases = [new PerformerAlias { Alias = "jane doe" }] };
        db.Performers.Add(performer);
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var service = new TorrentMatchService(db, IndexOf(torrent), Settings());

        var proposal = Matched(await service.MatchAsync(videoId));

        var proposed = Assert.Single(proposal!.Performers);
        Assert.Equal(performer.Id, proposed.Id);
        Assert.Equal("Jane Roe", proposed.Name);
        // Her own name is in the tag list too ("jane.roe"), so the alias is not the only route to her
        // and the row needs no explanation.
        Assert.Null(proposed.Source);
    }

    [Fact]
    public async Task Says_which_tag_found_a_performer_the_torrent_never_named()
    {
        // Only the alias spelling, in both word orders. Nothing in the tag list is her name.
        var torrent = new TorrentRelease
        {
            Name = "sample-scene",
            TagList = ["jane.doe", "doe.jane", "kissing"],
            Videos = [new TorrentVideoFile(SampleVideoName, SampleVideoSize)],
        };

        await using var db = CreateContext();
        db.Performers.Add(new Performer
        {
            Name = "Jane Roe",
            Aliases = [new PerformerAlias { Alias = "jane doe" }],
        });
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var service = new TorrentMatchService(db, IndexOf(torrent), Settings());

        var proposal = Matched(await service.MatchAsync(videoId));

        var proposed = Assert.Single(proposal!.Performers);
        Assert.Equal("Jane Roe", proposed.Name);
        // A reviewer reading "Jane Roe" cannot otherwise account for it — the torrent never said that.
        Assert.Equal("jane.doe", proposed.Source);
    }

    [Fact]
    public async Task Reports_a_performer_the_video_already_carries_as_applied()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        var performer = new Performer { Name = "Jane Doe" };
        db.Performers.Add(performer);
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        db.Set<VideoPerformer>().Add(new VideoPerformer { VideoId = videoId, PerformerId = performer.Id });
        await db.SaveChangesAsync();
        var service = new TorrentMatchService(db, IndexOf(torrent), Settings());

        var proposal = Matched(await service.MatchAsync(videoId));

        // Answered from the video's own PerformerIds, which is the same question the apply asks when it
        // decides whether the link already exists — where before the two compared names.
        Assert.True(Assert.Single(proposal!.Performers).AlreadyApplied);
    }

    [Fact]
    public async Task Lifts_known_performers_out_of_the_tag_list()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        db.Performers.Add(new Performer { Name = "Jane Doe" });
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var service = new TorrentMatchService(db, IndexOf(torrent), Settings());

        var proposal = Matched(await service.MatchAsync(videoId));

        // The tag list carries four permutations across two spellings of the same person:
        // jane.doe / doe.jane, and jane.roe / roe.jane. Only the first pair matches the name the
        // library knows, and both collapse to a single performer.
        // No "matches existing" to assert any more: a proposed performer is a library row by
        // construction, and it carries the id the apply will link.
        Assert.Contains(proposal!.Performers, performer => performer.Name == "Jane Doe" && performer.Id > 0);
        Assert.Single(proposal.Performers, performer => performer.Name == "Jane Doe");
        var tagNames = proposal.Tags.Select(tag => tag.Name).ToList();
        Assert.DoesNotContain("jane doe", tagNames);
        Assert.DoesNotContain("doe jane", tagNames);

        // The other spelling is an alias the library has never seen, so it stays in the tag list rather
        // than being guessed at — that is what makes it available as a performer candidate. It is spelled
        // by the configured style, since it would be created rather than matched.
        Assert.Contains("Jane Roe", tagNames);
    }

    [Fact]
    public async Task Lifts_every_permutation_once_the_alias_is_known()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        db.Performers.Add(new Performer
        {
            Name = "Jane Doe",
            Aliases = [new PerformerAlias { Alias = "Jane Roe" }],
        });
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(torrent), Settings()).MatchAsync(videoId));

        // With the alias recorded, all four permutations resolve to the one performer and none is left
        // in the tag list. This is the payoff for seeding aliases as they are confirmed.
        Assert.DoesNotContain(proposal!.Tags, tag => tag.Name.Contains("jane", StringComparison.OrdinalIgnoreCase)
            || tag.Name.Contains("doe", StringComparison.OrdinalIgnoreCase)
            || tag.Name.Contains("roe", StringComparison.OrdinalIgnoreCase));
        Assert.Single(proposal.Performers, performer => performer.Name == "Jane Doe");
    }

    [Theory]
    [InlineData(TagNameStyle.TitleCase, "Deep Blue Sea")]
    [InlineData(TagNameStyle.Spaced, "deep blue sea")]
    [InlineData(TagNameStyle.Dotted, "deep.blue.sea")]
    public async Task Spells_new_tags_using_the_configured_style(TagNameStyle style, string expected)
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var settings = new TorrentMetadataSettings();
        await settings.SetTagNameStyleAsync(style);

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(torrent), settings).MatchAsync(videoId));

        Assert.Contains(expected, proposal!.Tags.Select(tag => tag.Name));
    }

    [Fact]
    public async Task Keeps_the_library_spelling_for_a_tag_that_already_exists()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        // The style must not restyle a tag that resolves: the library is the authority on its own names.
        db.Tags.Add(new Tag { Name = "DEEP blue SEA" });
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var settings = new TorrentMetadataSettings();
        await settings.SetTagNameStyleAsync(TagNameStyle.Dotted);

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(torrent), settings).MatchAsync(videoId));

        Assert.Contains("DEEP blue SEA", proposal!.Tags.Select(tag => tag.Name));
        Assert.DoesNotContain("deep.blue.sea", proposal.Tags.Select(tag => tag.Name));
    }

    [Fact]
    public async Task Reports_current_field_values_for_comparison()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        var studio = new Studio { Name = "Existing Studio" };
        db.Studios.Add(studio);
        await db.SaveChangesAsync();

        var video = new Video { Title = "Existing title", Date = new DateOnly(2020, 5, 4), StudioId = studio.Id };
        db.Videos.Add(video);
        await db.SaveChangesAsync();
        await AttachFileAsync(db, video.Id, SampleVideoName, SampleVideoSize);
        db.Set<VideoUrl>().Add(new VideoUrl { VideoId = video.Id, Url = "https://example.invalid/existing" });
        await db.SaveChangesAsync();

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(torrent), Settings()).MatchAsync(video.Id));

        // Review needs the current values to show alongside the torrent's, so the user compares rather
        // than accepting blind.
        Assert.Equal("Existing title", proposal!.CurrentTitle);
        Assert.Equal("2020-05-04", proposal.CurrentDate);
        Assert.Equal("Existing Studio", proposal.CurrentStudioName);
        Assert.Contains("https://example.invalid/existing", proposal.CurrentUrls);
    }

    [Fact]
    public async Task Marks_relations_the_video_already_carries_as_no_ops()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        var tag = new Tag { Name = "Kissing" };
        db.Tags.Add(tag);
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        db.Set<VideoTag>().Add(new VideoTag { VideoId = videoId, TagId = tag.Id });
        await db.SaveChangesAsync();

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(torrent), Settings()).MatchAsync(videoId));

        // Already on the video: review can hide it rather than offering a change that does nothing.
        Assert.Contains(proposal!.Tags, entry => entry.Name == "Kissing" && entry.AlreadyApplied);
    }

    [Fact]
    public async Task Excludes_container_facts_from_the_proposal()
    {
        var torrent = SampleTorrent();

        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(torrent), Settings()).MatchAsync(videoId));

        var names = proposal!.Tags.Select(tag => tag.Name).ToList();
        Assert.DoesNotContain("1080p", names);
        Assert.DoesNotContain("h 265", names);
        Assert.Contains("69", names);
    }

    [Fact]
    public async Task Reports_pack_fan_out_so_review_can_de_emphasise_it()
    {
        await using var db = CreateContext();
        var pack = new TorrentRelease
        {
            Name = "siterip",
            TagList = ["big.waves", "vintage"],
            Videos =
            [
                new TorrentVideoFile("scene1.mp4", 111L),
                new TorrentVideoFile("scene2.mp4", 222L),
            ],
        };
        var videoId = await SeedVideoAsync(db, "scene1.mp4", 111L);

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(pack), Settings()).MatchAsync(videoId));

        // Metadata shared by two videos is a union across both, so most of it is wrong for either one.
        Assert.Equal(2, proposal!.FanOut);
    }

    // --- Forcing a torrent -----------------------------------------------
    //
    // The three-argument form of MatchAsync is the drop-a-torrent flow: the user found a specific
    // .torrent for this video and handed it over, so their choice outranks whatever the folder's
    // file-size lookup would have picked. Everything below is that path.

    [Fact]
    public async Task Uses_the_forced_torrent_over_the_one_file_size_would_have_chosen()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var index = IndexOf(SampleTorrent(), PackContainingTheSampleVideo());

        // Unforced, the pack loses: Find breaks a size tie by lowest fan-out. Asserted here so the
        // forced result below cannot be read as the outcome that would have happened anyway.
        var automatic = Matched(await new TorrentMatchService(db, index, Settings()).MatchAsync(videoId));
        Assert.Equal("sample-scene", automatic!.TorrentName);

        var forced = Matched(await new TorrentMatchService(db, index, Settings())
            .MatchAsync(videoId, PackName, SampleVideoName));

        // Someone attaching a pack on purpose gets the pack, tie-break or not.
        Assert.Equal(PackName, forced!.TorrentName);
        Assert.Equal(SampleVideoName, forced.FileName);
        Assert.Equal(2, forced.FanOut);
    }

    [Fact]
    public async Task Calls_a_forced_match_file_size_when_the_torrent_describes_one_of_the_videos_files()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        // The single-scene torrent is indexed too, so the automatic path would answer with that one:
        // a proposal naming the pack can only have come from the forced branch.
        var index = IndexOf(SampleTorrent(), PackContainingTheSampleVideo());

        var proposal = Matched(await new TorrentMatchService(db, index, Settings())
            .MatchAsync(videoId, PackName, SampleVideoName));

        // Forced, but the sizes really do agree — so this is a verified match and says so.
        Assert.Equal(PackName, proposal!.TorrentName);
        Assert.Equal("file size", proposal.MatchedOn);
    }

    [Fact]
    public async Task Calls_a_forced_match_your_selection_when_no_file_size_agrees()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var unrelated = new TorrentRelease
        {
            Name = "unrelated-release",
            TagList = ["kissing"],
            Videos = [new TorrentVideoFile("unrelated.mp4", SampleVideoSize + 1)],
        };

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(unrelated), Settings())
            .MatchAsync(videoId, "unrelated-release", "unrelated.mp4"));

        // Nothing about this torrent identifies the video — only the user's say-so. That label is the
        // one thing stopping a deliberate override from being presented as a verified match, so it
        // must not read "file size".
        Assert.Equal("your selection", proposal!.MatchedOn);
    }

    [Fact]
    public async Task Returns_nothing_when_the_forced_torrent_is_not_in_the_index()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var outcome = await new TorrentMatchService(db, IndexOf(SampleTorrent()), Settings())
            .MatchAsync(videoId, "a-torrent-nobody-indexed");

        // No silent fallback to the size lookup: SampleTorrent would have matched, and answering with
        // it would tell the user their chosen torrent was found when it was not.
        //
        // The video is there, so this is the folder's answer and not the library's — the distinction
        // the outcome exists to carry.
        Assert.Equal(TorrentMatchStatus.NoTorrentMatched, outcome.Status);
        Assert.Null(outcome.Proposal);
    }

    [Fact]
    public async Task Takes_the_file_whose_size_this_video_has_when_no_file_name_is_given()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        // Again indexed against the single-scene torrent that would otherwise win the size tie, so the
        // torrent assertion below fails if the forced name stops being honoured.
        var index = IndexOf(SampleTorrent(), PackContainingTheSampleVideo());

        var proposal = Matched(await new TorrentMatchService(db, index, Settings())
            .MatchAsync(videoId, PackName));

        // The pack's other scene sorts first and was indexed first, so both orders it could have been
        // taken in are wrong here. Handing that scene over was the defect: the drop zone uploads a pack
        // and names the torrent, and the video being reviewed is somewhere in the middle of it.
        Assert.Equal(PackName, proposal!.TorrentName);
        Assert.Equal(SampleVideoName, proposal.FileName);

        // And because the file really does describe this video, the label is the verified one.
        Assert.Equal("file size", proposal.MatchedOn);
    }

    [Fact]
    public async Task Keeps_the_named_file_even_when_another_in_the_torrent_matches_the_size()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var index = IndexOf(PackContainingTheSampleVideo());

        var proposal = Matched(await new TorrentMatchService(db, index, Settings())
            .MatchAsync(videoId, PackName, PackOtherFileName));

        // Naming a file is a choice, and the size preference above must not overrule it — the sample
        // file inside this same pack matches the video's bytes and still does not win. Someone who says
        // "this scene" means it; only a caller that cannot know, like the drop zone, leaves it null.
        Assert.Equal(PackOtherFileName, proposal!.FileName);
        Assert.Equal("your selection", proposal.MatchedOn);
    }

    [Fact]
    public async Task Still_answers_from_the_forced_torrent_when_no_file_in_it_matches_the_size()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);
        var unrelated = new TorrentRelease
        {
            Name = "unrelated-pack",
            TagList = ["kissing"],
            Videos =
            [
                new TorrentVideoFile("z-scene.mp4", SampleVideoSize + 1),
                new TorrentVideoFile("a-scene.mp4", SampleVideoSize + 2),
            ],
        };

        var proposal = Matched(await new TorrentMatchService(db, IndexOf(unrelated), Settings())
            .MatchAsync(videoId, "unrelated-pack"));

        // Preferring a size match must not become *requiring* one: the user handed over this torrent, so
        // a proposal is still owed. It just cannot claim to be verified.
        //
        // Which of the two files comes back is not asserted. The service orders candidates by
        // `TorrentEntryPreference` so the answer is reproducible rather than resting on index.All()'s
        // undefined order, but that ordering cannot be pinned from here — both files are in one torrent,
        // so they share every key but the basename, and removing the sort leaves this same file first
        // anyway. An assertion naming one would pass whether the sort existed or not. The two tests
        // below pin it where it can actually be seen.
        Assert.Equal("unrelated-pack", proposal!.TorrentName);
        Assert.Equal("your selection", proposal.MatchedOn);
    }

    [Fact]
    public async Task Prefers_the_single_scene_when_two_torrents_share_the_forced_name_and_file()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        // One torrent name, one basename, two payloads. That pair is the key the batch overview builds
        // its rows from, and it is not unique — so the overview could show the scene's row and clicking
        // it could open the pack.
        var pack = SharedName("the pack", "9001", SampleVideoName, PackOtherFileName);
        var scene = SharedName("the scene", "9002", SampleVideoName);

        // Indexed pack first, so the entry `All()` reaches first is the wrong one: this fails on
        // enumeration order rather than agreeing with it.
        var proposal = Matched(await new TorrentMatchService(db, IndexOf(pack, scene), Settings())
            .MatchAsync(videoId, SharedTorrentName, SampleVideoName));

        // The same preference `TorrentIndex.Find` and the bulk apply already used: a single-scene
        // torrent's tag list is about this video, a pack's is the union over its whole release.
        Assert.Equal("the scene", proposal.Title);
        Assert.Equal(1, proposal.FanOut);
    }

    [Fact]
    public async Task Answers_a_forced_pair_the_same_way_whichever_order_the_folder_was_indexed_in()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        // Two uploads of the same release: same name, same file, same fan-out. Nothing meaningful
        // separates them, which is exactly when the answer has to stop depending on dictionary order.
        var first = SharedName("first upload", "1000", SampleVideoName);
        var second = SharedName("second upload", "2000", SampleVideoName);

        var oneWay = Matched(await new TorrentMatchService(db, IndexOf(first, second), Settings())
            .MatchAsync(videoId, SharedTorrentName, SampleVideoName));
        var theOther = Matched(await new TorrentMatchService(db, IndexOf(second, first), Settings())
            .MatchAsync(videoId, SharedTorrentName, SampleVideoName));

        // Which one wins is arbitrary and deliberately not asserted. That it is the *same* one is the
        // guarantee: a video's proposed metadata must not change because the folder was re-read.
        Assert.Equal(oneWay.TorrentId, theOther.TorrentId);
        Assert.Equal(oneWay.Title, theOther.Title);
    }

    /// <summary>
    /// Two files of one pack sharing a basename tie on every key but the path — the only tie the real
    /// corpus actually contains, measured at 53 basename buckets and 4 size buckets over 3,202 torrents
    /// and 139,142 video files, with not one tie between two *different* torrents.
    ///
    /// Driven against the comparer rather than through <see cref="TorrentMatchService"/>, which is the
    /// unusual part of this file and is deliberate: both tied entries carry the same
    /// <see cref="TorrentRelease"/>, and all three callers consume only <c>entry.Torrent</c> once the
    /// tie is resolved. So the service cannot see the difference — the proposal is identical whichever
    /// entry wins — and a test written through it would pass with the key removed. What is being pinned
    /// is the total order <see cref="TorrentEntryPreference"/>'s own summary claims, before a future
    /// consumer reads <c>Video.Path</c> or <c>Video.Length</c> off the winner and inherits dictionary
    /// enumeration order without knowing it.
    /// </summary>
    [Fact]
    public void Separates_two_files_of_one_pack_that_share_a_basename()
    {
        var pack = SharedName("a two-disc pack", "9100", "Disc1/01.mp4", "Disc2/01.mp4");
        var first = new TorrentIndexEntry(pack.Videos[0], pack);
        var second = new TorrentIndexEntry(pack.Videos[1], pack);

        // Same release, so fan-out, torrent name and tracker id are equal by construction; `Basename`
        // strips the directory, so the fourth key is equal too. Asserting that keeps this test honest —
        // it fails if a change makes the entries differ some other way and stops exercising the tie.
        Assert.Equal(first.Torrent.Name, second.Torrent.Name);
        Assert.Equal(first.Video.Basename, second.Video.Basename);

        Assert.True(TorrentEntryPreference.Instance.Compare(first, second) < 0);
        Assert.True(TorrentEntryPreference.Instance.Compare(second, first) > 0);
    }

    [Fact]
    public void Picks_the_same_file_of_a_pack_whichever_order_its_scenes_are_enumerated_in()
    {
        var pack = SharedName("a two-disc pack", "9100", "Disc1/01.mp4", "Disc2/01.mp4");
        var first = new TorrentIndexEntry(pack.Videos[0], pack);
        var second = new TorrentIndexEntry(pack.Videos[1], pack);

        // `Enumerable.Min` keeps the earlier element on a tie, so before the path key these two answers
        // disagreed — which is the whole failure: the entries reach `Best` through a dictionary whose
        // order .NET does not define.
        Assert.Same(
            TorrentEntryPreference.Best([first, second]),
            TorrentEntryPreference.Best([second, first]));
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// The proposal from a match every test below expects to succeed.
    ///
    /// It asserts the status on the way through rather than only the nullability, so a test about tag
    /// resolution cannot start passing for the wrong reason — a `VideoNotFound` outcome and a matched
    /// one are both non-null objects now, and only this tells them apart.
    /// </summary>
    // ---------------------------------------------------------------------
    // Studio
    // ---------------------------------------------------------------------

    /// <summary>
    /// The library names its own studios. The tag list carries a bare lowercase domain, so proposing
    /// that spelling would show the reviewer a name nobody chose — and it is the library's row that
    /// gets linked either way.
    /// </summary>
    [Fact]
    public async Task Proposes_the_librarys_own_spelling_of_a_studio_a_site_tag_names()
    {
        await using var db = CreateContext();
        db.Studios.Add(new Studio { Name = "Lanternbay" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db, IndexOf(TorrentTagged("vintage", "lanternbay.com")), Settings()).MatchAsync(videoId));

        Assert.Equal("Lanternbay", proposal!.StudioName);
    }

    /// <summary>
    /// A studio the library does not have is not proposed at all.
    ///
    /// It used to be — the bare domain went into the proposal, <c>buildFields</c> rendered a Studio row
    /// for it and <c>defaultSelection</c> pre-ticked it, and then the apply's lookup missed and did
    /// nothing without saying so. On a fresh library that is every torrent carrying a site tag: a
    /// pre-ticked control that cannot work, reporting success.
    /// </summary>
    [Fact]
    public async Task Proposes_no_studio_when_the_library_has_none_by_that_name()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db, IndexOf(TorrentTagged("vintage", "lanternbay.com")), Settings()).MatchAsync(videoId));

        Assert.Null(proposal!.StudioName);
    }

    /// <summary>
    /// The reason three-quarters of torrents resolved no studio at all: a domain has no spaces
    /// and a curated studio name almost always does, so the two sides were compared in units that could
    /// never agree.
    /// </summary>
    [Theory]
    [InlineData("Pier Fidelity", "pierfidelity.com")]
    [InlineData("Regatta Kings", "regattakings.com")]
    [InlineData("Boats On Bays", "boatsonbays.com")]
    [InlineData("E-BODY", "ebody.com")]
    [InlineData("Jules Jordan Video", "julesjordanvideo.net")]
    public async Task Matches_a_domain_to_a_studio_named_with_separators(string studioName, string siteTag)
    {
        await using var db = CreateContext();
        db.Studios.Add(new Studio { Name = studioName });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db, IndexOf(TorrentTagged("vintage", siteTag)), Settings()).MatchAsync(videoId));

        Assert.Equal(studioName, proposal!.StudioName);
    }

    /// <summary>
    /// The line the normalisation must not cross. Reducing both sides to alphanumerics is exact matching
    /// modulo separators; matching on a substring or an edit distance is a guess, and studios are never
    /// guessed. <c>pier</c> is a prefix of <c>pierhouse</c> and <c>sun</c> of <c>sunbeam</c>, so a
    /// containment rule would link the wrong studio to a video with no way for the reviewer to tell.
    /// </summary>
    [Theory]
    [InlineData("Pier House", "pier.com")]
    [InlineData("Sun Beam", "sun.com")]
    [InlineData("Lanternbay Extra", "lanternbay.com")]
    public async Task Does_not_match_a_domain_that_is_merely_contained_in_a_studio_name(string studioName, string siteTag)
    {
        await using var db = CreateContext();
        db.Studios.Add(new Studio { Name = studioName });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db, IndexOf(TorrentTagged("vintage", siteTag)), Settings()).MatchAsync(videoId));

        Assert.Null(proposal!.StudioName);
    }

    /// <summary>
    /// Normalising makes an existing library problem visible rather than creating one: two spellings of
    /// one studio now collapse to a single key, and the duplicate rule refuses them. Before separator-insensitive matching
    /// neither spelling matched a domain at all, so the collision could not arise.
    /// </summary>
    [Fact]
    public async Task Refuses_a_studio_the_library_holds_under_two_spellings()
    {
        await using var db = CreateContext();
        await SeedDuplicateStudiosAsync(db, "Boats On Bays", "Boats on Bays");
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db, IndexOf(TorrentTagged("boatsonbays.com")), Settings()).MatchAsync(videoId));

        Assert.Null(proposal!.StudioName);
    }

    /// <summary>
    /// Two site tags naming two studios the library curates is a genuine ambiguity, and guessing is
    /// what the defect was about. The reviewer chooses between them instead.
    /// </summary>
    [Fact]
    public async Task Proposes_no_studio_when_two_site_tags_name_two_the_library_has()
    {
        await using var db = CreateContext();
        db.Studios.Add(new Studio { Name = "Lanternbay" });
        db.Studios.Add(new Studio { Name = "BigBlueBoats" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db, IndexOf(TorrentTagged("lanternbay.com", "bigblueboats.com")), Settings()).MatchAsync(videoId));

        Assert.Null(proposal!.StudioName);
    }

    /// <summary>
    /// The network-plus-imprint shape, which is 679 torrents of the corpus: two site tags, one of them
    /// a studio the library holds. That one is the answer, and it is the answer whichever order the
    /// tracker listed them in — which is the defect this fixes, since the same pair appears both ways
    /// round on different torrents.
    /// </summary>
    [Theory]
    [InlineData("lanternbay.com", "bigblueboats.com")]
    [InlineData("bigblueboats.com", "lanternbay.com")]
    public async Task Reads_every_site_tag_before_answering_so_their_order_cannot_decide(string first, string second)
    {
        await using var db = CreateContext();
        db.Studios.Add(new Studio { Name = "Lanternbay" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db, IndexOf(TorrentTagged(first, second)), Settings()).MatchAsync(videoId));

        Assert.Equal("Lanternbay", proposal!.StudioName);
    }

    /// <summary>
    /// Two curated studios both matching is the state the chooser exists for: the extension proposes neither,
    /// and hands the reviewer the pair instead of spending the decision in silence.
    /// </summary>
    [Fact]
    public async Task Offers_both_studios_when_the_library_holds_two_the_torrent_names()
    {
        await using var db = CreateContext();
        db.Studios.Add(new Studio { Name = "Harbour Lights" });
        db.Studios.Add(new Studio { Name = "Pier House" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db, IndexOf(TorrentTagged("harbourlights.com", "pierhouse.com")), Settings()).MatchAsync(videoId));

        // Still no proposal: the two are alternatives, and a field cannot be both proposed and asked about.
        Assert.Null(proposal!.StudioName);
        Assert.Equal(2, proposal.StudioMatchCount);
        Assert.Equal(["Harbour Lights", "Pier House"], proposal.StudioChoices.Select(choice => choice.Name));

        // Each option carries the tracker's spelling of the domain that found it — network and imprint
        // differ by domain rather than by how the library spells them.
        Assert.Equal(["harbourlights.com", "pierhouse.com"], proposal.StudioChoices.Select(choice => choice.Source));
    }

    /// <summary>
    /// Three or more matching gets a count and no shortlist. A shortlist would have to be ordered, and
    /// ordering site tags is the defect the studio rule exists to kill.
    /// </summary>
    [Fact]
    public async Task Counts_the_studios_it_will_not_offer_once_more_than_two_match()
    {
        await using var db = CreateContext();
        db.Studios.Add(new Studio { Name = "Harbour Lights" });
        db.Studios.Add(new Studio { Name = "Pier House" });
        db.Studios.Add(new Studio { Name = "Anchor Row" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db,
            IndexOf(TorrentTagged("harbourlights.com", "pierhouse.com", "anchorrow.com")),
            Settings()).MatchAsync(videoId));

        Assert.Null(proposal!.StudioName);
        Assert.Empty(proposal.StudioChoices);
        Assert.Equal(3, proposal.StudioMatchCount);
    }

    /// <summary>
    /// One studio the library holds twice counts but is never offered: picking between two spellings of
    /// the same studio is a library repair, not a metadata decision.
    /// </summary>
    [Fact]
    public async Task Never_offers_a_choice_between_two_rows_of_one_studio()
    {
        await using var db = CreateContext();
        await SeedDuplicateStudiosAsync(db, "Harbour Lights", "harbour lights");
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db, IndexOf(TorrentTagged("harbourlights.com")), Settings()).MatchAsync(videoId));

        Assert.Null(proposal!.StudioName);
        Assert.Empty(proposal.StudioChoices);
        Assert.Equal(2, proposal.StudioMatchCount);
    }

    /// <summary>
    /// A megapack naming many sites has no studio, and needs no rule of its own to be told so: it hits
    /// either none of them or several, and both answers are the same one.
    /// </summary>
    [Fact]
    public async Task Proposes_no_studio_for_a_torrent_spanning_many_sites()
    {
        await using var db = CreateContext();
        db.Studios.Add(new Studio { Name = "Lanternbay" });
        db.Studios.Add(new Studio { Name = "Seabreeze" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db,
            IndexOf(TorrentTagged("lanternbay.com", "seabreeze.net", "paperboats.com", "willowway.com")),
            Settings()).MatchAsync(videoId));

        Assert.Null(proposal!.StudioName);
    }

    /// <summary>
    /// Two studios sharing a name is a library broken underneath the ORM — see the fixture. Which one
    /// the user meant is unanswerable, so it is refused rather than resolved by whichever row came
    /// back first.
    /// </summary>
    [Fact]
    public async Task Proposes_no_studio_when_the_library_holds_that_name_twice()
    {
        await using var db = CreateContext();
        await SeedDuplicateStudiosAsync(db, "Lanternbay", "lanternbay");
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db, IndexOf(TorrentTagged("lanternbay.com")), Settings()).MatchAsync(videoId));

        Assert.Null(proposal!.StudioName);
    }

    /// <summary>
    /// Encoding provenance is classified so it can be dropped, not so it can be imported.
    ///
    /// `docs/DESIGN-DECISIONS.md` §"Technical tags are dropped" has said so since the design was
    /// written, and both services imported it anyway — with nothing pinning the destination either way,
    /// which is how the two disagreed for as long as they did. Ruled 2026-08-20 in the doc's favour.
    ///
    /// Asserted as *absence from a proposal that still carries its content tag*, so the test cannot
    /// pass by the whole proposal being empty for some unrelated reason.
    /// </summary>
    [Theory]
    [InlineData("x265.reencode")]
    [InlineData("bluray")]
    [InlineData("ai.upscale")]
    [InlineData("low.bitrate")]
    public async Task Never_proposes_encoding_provenance_as_a_tag(string provenance)
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db, IndexOf(TorrentTagged(provenance, "kissing")), Settings()).MatchAsync(videoId));

        Assert.Equal(["Kissing"], proposal!.Tags.Select(tag => tag.Name));
    }

    /// <summary>
    /// A performer whose name ends in "ai" is matched like any other (and the reason it is not
    /// merely cosmetic).
    ///
    /// `mirai.hoshino` classified as SourceQuality, and `PerformerMatcher.Split` only looks at Content —
    /// so the library could hold this performer, the torrent could name her twice in both word orders,
    /// and nothing would link. The corpus carries 24 distinct names in this shape.
    /// </summary>
    [Fact]
    public async Task Matches_a_performer_whose_name_ends_in_ai()
    {
        await using var db = CreateContext();
        db.Performers.Add(new Performer { Name = "Mirai Hoshino" });
        var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

        var proposal = Matched(await new TorrentMatchService(
            db, IndexOf(TorrentTagged("mirai.hoshino", "kissing")), Settings()).MatchAsync(videoId));

        Assert.Equal(["Mirai Hoshino"], proposal!.Performers.Select(performer => performer.Name));
        // And it is subtracted from the tag list rather than proposed as both.
        Assert.Equal(["Kissing"], proposal.Tags.Select(tag => tag.Name));
    }

    /// <summary>
    /// A release date survives the round trip under a culture whose calendar is not the Gregorian one
    ///.
    ///
    /// Both ends read the ambient culture. `DateOnly.ToString("yyyy-MM-dd")` formats in the *culture's*
    /// calendar, so under `th-TH` a 2018 date renders as `2561-…`; `DateOnly.TryParse` then reads the
    /// value back under the same culture, and the two do not agree. The reviewer sees a plausible date,
    /// ticks it, and the apply silently writes nothing — the one failure mode with no error anywhere.
    ///
    /// The round trip is the assertion rather than either half alone, because either half alone can be
    /// made to look right: a proposal that formats wrongly and a parse that reads that same wrongness
    /// back would agree with each other and still put the wrong year in the library.
    ///
    /// `th-TH` specifically because its default calendar is Thai Buddhist — a culture that merely
    /// reorders the components would still round-trip through a fixed-format string and prove nothing.
    ///
    /// Only <see cref="CultureInfo.CurrentCulture"/> is set, deliberately, and never
    /// <c>DefaultThreadCurrentCulture</c>. xUnit gives each test class its own collection and runs
    /// collections in parallel, so the process-wide default would put `th-TH` under whatever else
    /// happened to be running. The thread-local one is enough because it flows across `await` with the
    /// execution context — verified by reverting the fix with only this line in place and watching the
    /// test go red for the right reason.
    /// </summary>
    [Fact]
    public async Task Applies_the_release_date_under_a_non_gregorian_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");

            await using var db = CreateContext();
            var videoId = await SeedVideoAsync(db, SampleVideoName, SampleVideoSize);

            var proposal = Matched(await new TorrentMatchService(
                db, IndexOf(TorrentTagged("2018.03.20")), Settings()).MatchAsync(videoId));

            Assert.Equal("2018-03-20", proposal!.Date);

            await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
            {
                VideoId = videoId,
                Date = proposal.Date,
            });

            db.ChangeTracker.Clear();
            Assert.Equal(new DateOnly(2018, 3, 20), (await db.Videos.SingleAsync(video => video.Id == videoId)).Date);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    private static TorrentMatchProposal Matched(TorrentMatchOutcome outcome)
    {
        Assert.Equal(TorrentMatchStatus.Matched, outcome.Status);
        Assert.NotNull(outcome.Proposal);
        return outcome.Proposal;
    }

    private const string SharedTorrentName = "shared-release-name";

    /// <summary>
    /// A torrent under a name another one also uses, distinguished only by its title and upload id.
    ///
    /// Two of these are the collision `TorrentEntryPreference` exists to resolve: the (torrent name,
    /// file name) pair that the batch overview keys its rows on, and that a forced match is given, is
    /// not unique.
    /// </summary>
    private static TorrentRelease SharedName(string title, string torrentId, params string[] basenames) => new()
    {
        Name = SharedTorrentName,
        Title = title,
        Comment = $"https://tracker.invalid/torrents.php?id={torrentId}",
        TagList = ["kissing"],
        // Sizes ascend from the video's own, so the first file always matches it and the rest do not.
        Videos = [.. basenames.Select((basename, offset) =>
            new TorrentVideoFile(basename, SampleVideoSize + offset))],
    };

    /// <summary>Default settings: new tags get Title Case, which is what the extension ships with.</summary>
    private static TorrentMetadataSettings Settings() => new();

    /// <summary>
    /// Builds an index over one or more torrents. More than one matters when two describe the same
    /// file: that is where <see cref="TorrentIndex.Find"/>'s lowest-fan-out preference — and the forced
    /// path's override of it — become visible.
    /// </summary>
    private static TorrentIndex IndexOf(params TorrentRelease[] torrents)
    {
        var index = new TorrentIndex();
        foreach (var torrent in torrents)
            index.Add(torrent);
        return index;
    }

    private static async Task<int> SeedVideoAsync(
        CoveContext db,
        string basename,
        long size,
        string? imageBlobId = null)
    {
        var video = new Video { Title = basename, ImageBlobId = imageBlobId };
        db.Videos.Add(video);
        await db.SaveChangesAsync();
        await AttachFileAsync(db, video.Id, basename, size);
        return video.Id;
    }

    private static async Task AttachFileAsync(CoveContext db, int videoId, string basename, long size)
    {
        var folder = new Folder { Path = "/library" };
        db.Set<Folder>().Add(folder);
        await db.SaveChangesAsync();

        db.VideoFiles.Add(new VideoFile
        {
            Basename = basename,
            ParentFolderId = folder.Id,
            Size = size,
            VideoId = videoId,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds two studios whose names differ only by case — the state three tests here exist to
    /// describe, and the one an ordinary save will not create.
    ///
    /// <c>SaveChanges</c> enforces a case-insensitive unique-name namespace, so adding the pair and
    /// saving throws <c>EntityNameConflictException</c>. **The behaviour still matters, and not for
    /// the reason this comment used to give.** An upgraded library cannot carry duplicates in either:
    /// the migration's preflight refuses while any name-conflict group remains, and the migration's own
    /// SQL guard refuses independently. What is left is a database broken underneath the ORM —
    /// which is the honest reason this cover exists, and reason enough, because the rule it pins ("one
    /// studio the library holds twice is counted and never offered") has no other test.
    ///
    /// Note that the *matcher's* ambiguity is wider than the host's and stays ordinarily reachable:
    /// its key keeps only alphanumerics where <c>StudioIdentityKey</c> merely lowercases, so
    /// "Pier Fidelity" beside "Pier-Fidelity" is two identities to Cove and one key to us. These three
    /// tests deliberately pin the case-only pair, which is the half that needs the door below.
    ///
    /// <c>SuppressEntityNameValidation()</c> is that door, and it is the host's own — Cove's merge
    /// services and its own tests use it to "pass through a pre-existing duplicate state". It skips the
    /// validation and the canonical-name trim, but <em>not</em> the key assignment, so both rows land
    /// carrying the same <c>NameKey</c> — which is precisely the state the migration's
    /// <c>EXCLUDE USING spgist</c> constraint would refuse, rather than an artefact of the fixture.
    ///
    /// This used to be an insert-then-rename through raw SQL, with the table and column names read out
    /// of the EF model so the fixture named no column the two revisions did not share. That scaffolding
    /// existed because the suppression API is not on 1.2 and the gate was still pinned there; with the
    /// floor and the pin both on 1.3.0 it has nothing left to protect.
    /// </summary>
    private static async Task SeedDuplicateStudiosAsync(CoveContext db, string first, string second)
    {
        using (db.SuppressEntityNameValidation())
        {
            db.Studios.Add(new Studio { Name = first });
            db.Studios.Add(new Studio { Name = second });
            await db.SaveChangesAsync();
        }

    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new CoveContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    /// <summary>
    /// The torrent every test here matches against, built in code.
    ///
    /// It used to be a real file loaded out of <c>resources/</c>, which meant twelve tests silently
    /// returned on any machine without that exact torrent — and reported green while doing so.
    /// No torrent is committed to this repo, so the fixture is invented rather than transcribed.
    ///
    /// Each tag drives one assertion instead of reproducing a real tag list. The container values are
    /// real strings, because the classifier has to recognise them for those assertions to mean anything.
    /// </summary>
    /// <summary>
    /// <see cref="SampleTorrent"/> carrying a cover URL. Built by hand rather than with <c>with</c>:
    /// <see cref="TorrentRelease"/> is a class, so it has no copy constructor.
    /// </summary>
    private static TorrentRelease SampleTorrentWithCover(string coverUrl)
    {
        var sample = SampleTorrent();
        return new TorrentRelease
        {
            Name = sample.Name,
            Title = sample.Title,
            Comment = sample.Comment,
            TagList = sample.TagList,
            Videos = sample.Videos,
            CoverUrl = coverUrl,
        };
    }

    /// <summary>
    /// The sample video described by a torrent carrying exactly <paramref name="tagList"/>.
    ///
    /// For the cases where the tag list *is* the fixture and <see cref="SampleTorrent"/>'s twelve
    /// entries would be noise — each tag here drives one assertion, which is the same rule the sample
    /// follows, applied to a smaller question.
    /// </summary>
    private static TorrentRelease TorrentTagged(params string[] tagList) => new()
    {
        Name = "tagged-scene",
        Title = SampleTitle,
        TagList = tagList,
        Videos = [new TorrentVideoFile(SampleVideoName, SampleVideoSize)],
    };

    private static TorrentRelease SampleTorrent() => new()
    {
        Name = "sample-scene",
        Title = SampleTitle,
        Comment = $"https://tracker.invalid/torrents.php?id={SampleTorrentId}",
        TagList =
        [
            "deep.blue.sea",                                // multi-word content: spelling styles, existing-tag match
            "kissing",                                      // single word: the already-applied case
            "sunbeam",                                      // resolves to an existing tag through an alias
            "jane.doe", "doe.jane", "jane.roe", "roe.jane", // one performer, two spellings, both orders
            "1080p",                                        // resolution — a container fact, never a tag
            "h.265",                                        // codec — and the case where a dot must survive
            "69",                                           // numeric, but content: must survive
        ],
        Videos = [new TorrentVideoFile(SampleVideoName, SampleVideoSize)],
    };

    /// <summary>
    /// A two-scene pack that also contains the sample video's file, at the same size — the shape
    /// found 20 times in a real library, where one local file is
    /// described by both a single-scene torrent and a siterip.
    ///
    /// Indexed alongside <see cref="SampleTorrent"/> it always loses the size tie, so a proposal
    /// naming it is a proposal that was forced.
    /// </summary>
    private static TorrentRelease PackContainingTheSampleVideo() => new()
    {
        Name = PackName,
        Title = "[SAMPLE-SITERIP] The Whole Set",
        TagList = ["kissing", "deep.blue.sea"],
        Videos =
        [
            new TorrentVideoFile(SampleVideoName, SampleVideoSize),
            new TorrentVideoFile(PackOtherFileName, SampleVideoSize + 4242L),
        ],
    };
}
