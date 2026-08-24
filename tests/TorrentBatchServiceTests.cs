using Cove.Core.Entities;
using Cove.Data;
using Cove.TorrentMetadata;
using Microsoft.EntityFrameworkCore;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// Covers the batch overview and bulk apply.
///
/// Bulk apply is the only action in this extension that writes to many videos at once with no undo, so
/// the rules that decide *what it touches* are asserted directly: packs excluded unless opted into,
/// already-applied rows skipped, and "existing tags only" creating nothing. A wrong boolean in any of
/// those is invisible until it has already run across a library.
/// </summary>
public class TorrentBatchServiceTests
{
    private const long SceneSize = 5_387_499_251L;
    private const long OtherSize = 1_234_567_890L;

    // ---------------------------------------------------------------------
    // Overview
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Reports_a_matched_row_with_current_and_proposed_tag_counts()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize, tagNames: ["Kissing"]);

        var overview = await Service(db, Torrent("scene", ["kissing", "big.waves", "vintage"], SceneSize)).ListAsync();

        var row = Assert.Single(overview.Rows);
        Assert.Equal("matched", row.Status);
        Assert.Equal(videoId, row.VideoId);
        Assert.Equal(1, row.VideoTagCount);
        // "kissing" resolves to the seeded tag, which this video already carries, so it is not
        // something the torrent would add. The other two are, and both would be created.
        Assert.Equal(2, row.TagsToAdd);
        Assert.Equal(2, row.TagsToCreate);
    }

    [Theory]
    [InlineData("example.invalid", true)]
    [InlineData("other.invalid", false)]
    [InlineData(null, false)]
    public async Task Says_whether_a_rows_cover_may_be_shown_at_all(string? allowedHost, bool expected)
    {
        await using var db = CreateContext();
        await SeedVideoAsync(db, SceneSize);

        var index = new TorrentIndex();
        index.Add(Torrent("scene", ["kissing"], SceneSize));
        var service = new TorrentBatchService(
            db, index, new TorrentMetadataSettings(),
            coverHosts: allowedHost is null ? null : new CoverHostAllowlist([allowedHost]));

        var row = Assert.Single((await service.ListAsync()).Rows);

        // The page renders the torrent's thumbnail through the extension's proxy now, and the
        // proxy refuses a host the operator has not named — so a row that cannot say which it is
        // would render a broken image under a notice claiming nothing was requested. Null answers
        // false, the same direction every other cover path fails when the allowlist is unwired.
        Assert.Equal(expected, row.TorrentCoverAllowed);
        Assert.Equal("https://example.invalid/cover.jpg", row.TorrentCoverUrl);
    }

    [Theory]
    [InlineData("blob-1", true)]
    [InlineData(null, false)]
    public async Task Says_whether_the_library_video_has_artwork_of_its_own(string? blobId, bool expected)
    {
        await using var db = CreateContext();
        await SeedVideoAsync(db, SceneSize, imageBlobId: blobId);

        var row = Assert.Single((await Service(db, Torrent("scene", ["kissing"], SceneSize)).ListAsync()).Rows);

        // Asked once here rather than discovered per row by a request that 404s: the page renders one
        // library thumbnail per row, and a video with no artwork answered every one of them with an
        // error the browser logs.
        Assert.Equal(expected, row.VideoHasImage);
    }

    [Fact]
    public async Task Counts_a_torrent_no_library_file_matches_instead_of_returning_a_row()
    {
        await using var db = CreateContext();
        await SeedVideoAsync(db, OtherSize);

        var overview = await Service(db, Torrent("scene", ["kissing"], SceneSize)).ListAsync();

        // An unmatched torrent has nothing to review on it — no video, no tags, no proposal — and a
        // real folder is overwhelmingly made of them, so it is counted rather than sent. Returning a
        // row each made the overview a 45 MB response that was 99.5% padding.
        Assert.Empty(overview.Rows);
        Assert.Equal(1, overview.Unmatched);
        Assert.Equal(1, overview.IndexedFiles);
        Assert.Equal(1, overview.Torrents);
    }

    [Fact]
    public async Task Counts_the_indexed_files_whether_or_not_they_matched()
    {
        await using var db = CreateContext();
        await SeedVideoAsync(db, SceneSize);

        var overview = await Service(
            db,
            Torrent("matches", ["kissing"], SceneSize),
            Torrent("does-not", ["kissing"], OtherSize)).ListAsync();

        Assert.Single(overview.Rows);
        Assert.Equal(1, overview.Unmatched);
        // The page needs these to show the folder is being read at all, which is the only thing the
        // dropped rows were really communicating.
        Assert.Equal(2, overview.IndexedFiles);
        Assert.Equal(2, overview.Torrents);
    }

    // ---------------------------------------------------------------------
    // The half of `unmatched` the user can act on
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Counts_a_video_whose_file_the_size_missed_but_the_name_would_find()
    {
        await using var db = CreateContext();
        await SeedVideoAsync(db, SceneSize, basename: "scene.mp4");

        // Same name, different size: a re-encode, or a different release of the same scene. The
        // overview matches on size alone, so this is `unmatched` to it — but `TorrentIndex.Find` falls
        // back to the basename, so opening the video offers this very torrent and says it matched on
        // the file name. The count is what stops that being invisible from here.
        var overview = await Service(db, TorrentNamed("re-encode", ("scene.mp4", OtherSize))).ListAsync();

        Assert.Empty(overview.Rows);
        Assert.Equal(1, overview.Unmatched);
        Assert.Equal(1, overview.VideosMatchableByName);
    }

    [Fact]
    public async Task Does_not_count_a_video_the_size_already_matched()
    {
        await using var db = CreateContext();
        await SeedVideoAsync(db, SceneSize, basename: "scene.mp4");

        // Name and size both agree. Counting this would double-report a row the page is already
        // showing, and inflate the one number whose whole job is to describe what the rows do not.
        var overview = await Service(db, TorrentNamed("exact", ("scene.mp4", SceneSize))).ListAsync();

        Assert.Single(overview.Rows);
        Assert.Equal(0, overview.VideosMatchableByName);
    }

    [Fact]
    public async Task Counts_a_video_once_however_many_torrents_name_its_file()
    {
        await using var db = CreateContext();
        await SeedVideoAsync(db, SceneSize, basename: "scene.mp4");

        // Per video, not per indexed entry. A scene re-uploaded at four bitrates is one video the user
        // can do something about, not four — and a pack naming its files `01.mp4` is exactly how a
        // per-entry count would run away.
        var overview = await Service(
            db,
            TorrentNamed("720p", ("scene.mp4", OtherSize)),
            TorrentNamed("1080p", ("scene.mp4", OtherSize + 1)),
            TorrentNamed("2160p", ("scene.mp4", OtherSize + 2))).ListAsync();

        Assert.Equal(3, overview.Unmatched);
        Assert.Equal(1, overview.VideosMatchableByName);
    }

    [Fact]
    public async Task Does_not_count_a_video_whose_other_file_matched_by_size()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, SceneSize, basename: "kept.mp4");
        await AttachFileAsync(db, videoId, "scene.mp4", OtherSize);

        // A Cove video can hold several files. `TorrentMatchService` walks them and takes the first
        // hit, so this video matches on size through `kept.mp4` and never reaches the name fallback.
        // Counting per file rather than per video would report it as a near-miss anyway.
        var overview = await Service(
            db,
            TorrentNamed("exact", ("kept.mp4", SceneSize)),
            TorrentNamed("re-encode", ("scene.mp4", OtherSize + 1))).ListAsync();

        Assert.Single(overview.Rows);
        Assert.Equal(0, overview.VideosMatchableByName);
    }

    [Fact]
    public async Task Counts_a_name_that_matches_only_once_case_is_ignored()
    {
        await using var db = CreateContext();
        await SeedVideoAsync(db, SceneSize, basename: "Scene.MP4");

        // `TorrentIndexSnapshot.ByBasename` is `OrdinalIgnoreCase`, so this is a match the dialog would
        // make. A case-sensitive count here would report fewer videos than the extension can actually
        // help with — and would do it silently.
        var overview = await Service(db, TorrentNamed("re-encode", ("scene.mp4", OtherSize))).ListAsync();

        Assert.Equal(1, overview.VideosMatchableByName);
    }

    [Fact]
    public async Task Ignores_a_library_file_that_belongs_to_no_video()
    {
        await using var db = CreateContext();
        var folder = new Folder { Path = "/orphans" };
        db.Set<Folder>().Add(folder);
        await db.SaveChangesAsync();

        // A file Cove has scanned but not attached to a video yet. Its size can collide with a
        // torrent's, and there is no video for a row to be about — so it has to be counted, not
        // matched. Without the guard the row would name video 0.
        db.VideoFiles.Add(new VideoFile { Basename = "orphan.mp4", ParentFolderId = folder.Id, Size = SceneSize });
        await db.SaveChangesAsync();

        var overview = await Service(db, Torrent("scene", ["kissing"], SceneSize)).ListAsync();

        Assert.Empty(overview.Rows);
        Assert.Equal(1, overview.Unmatched);
    }

    [Fact]
    public async Task Picks_the_same_video_every_time_when_two_library_files_share_a_size()
    {
        await using var db = CreateContext();
        var first = await SeedVideoAsync(db, SceneSize);
        var second = await SeedVideoAsync(db, SceneSize);
        Assert.True(second > first);

        var overview = await Service(db, Torrent("scene", ["kissing"], SceneSize)).ListAsync();

        // 2.32% of corpus sizes are shared, so a torrent's size can name more than one library video.
        // Which one the row is about used to be whatever order the database returned rows in — nothing
        // asserted it, and nothing made it stable across two page loads. Lowest id, deliberately.
        Assert.Equal(first, Assert.Single(overview.Rows).VideoId);
    }

    [Fact]
    public async Task Reports_applied_once_a_remote_id_links_the_video_to_that_torrent()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        // Carrying the torrent's only tag as well as its remote id: applied, and nothing left over.
        var videoId = await SeedVideoAsync(db, SceneSize, tagNames: ["Kissing"]);
        db.Set<VideoRemoteId>().Add(new VideoRemoteId { VideoId = videoId, Endpoint = "torrent-metadata", RemoteId = "999" });
        await db.SaveChangesAsync();

        var overview = await Service(db, Torrent("scene", ["kissing"], SceneSize, torrentId: "999")).ListAsync();

        // Status is derived from the remote id rather than from moving files, so it survives renames and
        // can express partial completion for a pack.
        Assert.Equal("applied", Assert.Single(overview.Rows).Status);
    }

    [Fact]
    public async Task Reports_updated_when_an_applied_torrent_has_gained_a_tag()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize, tagNames: ["Kissing"]);
        db.Set<VideoRemoteId>().Add(new VideoRemoteId { VideoId = videoId, Endpoint = "torrent-metadata", RemoteId = "999" });
        await db.SaveChangesAsync();

        // The same torrent id, re-downloaded after the tracker re-tagged it. An edit does not mint a new
        // id, so this is indistinguishable from the applied one by id alone — and it used to read
        // "applied", which meant bulk apply skipped it and "Hide applied" kept it off screen.
        var retagged = Torrent("scene", ["kissing", "vintage"], SceneSize, torrentId: "999");

        var overview = await Service(db, retagged).ListAsync();

        var row = Assert.Single(overview.Rows);
        Assert.Equal("updated", row.Status);
        Assert.Equal(1, row.TagsToAdd);
    }

    [Fact]
    public async Task Reports_updated_when_an_applied_torrent_has_gained_only_a_performer()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        db.Performers.Add(new Performer { Name = "Jane Doe" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize, tagNames: ["Kissing"]);
        db.Set<VideoRemoteId>().Add(new VideoRemoteId { VideoId = videoId, Endpoint = "torrent-metadata", RemoteId = "999" });
        await db.SaveChangesAsync();

        // The tracker added a performer and no tag the video is missing. The "still has
        // something to give" half of the test was tags only, so this read "applied" — bulk apply
        // skipped it and "Hide applied" kept it off screen, which is the same hole already fixed for tags
        // left open beside it.
        var retagged = Torrent("scene", ["kissing", "jane.doe"], SceneSize, torrentId: "999");

        var row = Assert.Single((await Service(db, retagged).ListAsync()).Rows);
        Assert.Equal("updated", row.Status);
        Assert.Equal(0, row.TagsToAdd);
        Assert.Equal(1, row.PerformersToAdd);
    }

    [Fact]
    public async Task Reads_applied_when_the_video_already_carries_the_performer_the_torrent_names()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        db.Performers.Add(new Performer { Name = "Jane Doe" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize, tagNames: ["Kissing"], performerNames: ["Jane Doe"]);
        db.Set<VideoRemoteId>().Add(new VideoRemoteId { VideoId = videoId, Endpoint = "torrent-metadata", RemoteId = "999" });
        await db.SaveChangesAsync();

        // The other direction, and the one that keeps it fixed: counting the torrent's performers
        // rather than the video's missing ones would make every applied row with a performer read
        // "updated" for good.
        var torrent = Torrent("scene", ["kissing", "jane.doe"], SceneSize, torrentId: "999");

        var row = Assert.Single((await Service(db, torrent).ListAsync()).Rows);
        Assert.Equal("applied", row.Status);
        Assert.Equal(0, row.PerformersToAdd);
    }

    [Fact]
    public async Task Reads_applied_when_the_only_tags_left_are_ones_the_apply_declined_to_create()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize);

        // "vintage" is not in the library, and a default apply creates nothing — so it is still
        // outstanding the instant the apply finishes. Judging "updated" by what is outstanding made
        // that the permanent state of almost every applied row: 692 of 709 against the real corpus,
        // which is no signal at all and kept them all past the "Hide applied" filter.
        var torrent = Torrent("scene", ["kissing", "vintage"], SceneSize, torrentId: "999");
        var baseline = new AppliedTorrentBaseline();
        baseline.AttachStore(new FakeExtensionStore());
        var service = ServiceWith(db, baseline, torrent);

        Assert.Equal(1, (await service.ApplyAsync(new BatchApplyRequest())).VideosTouched);

        var row = Assert.Single((await service.ListAsync()).Rows);
        Assert.Equal("applied", row.Status);
        // Still reported as outstanding — the reviewer can go and create it. It is just not an update.
        Assert.Equal(1, row.TagsToAdd);
        Assert.Equal(videoId, row.VideoId);
    }

    [Fact]
    public async Task Reads_updated_when_the_torrent_itself_has_gained_a_tag_since_the_apply()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize);

        var baseline = new AppliedTorrentBaseline();
        baseline.AttachStore(new FakeExtensionStore());
        var applied = Torrent("scene", ["kissing", "vintage"], SceneSize, torrentId: "999");
        Assert.Equal(1, (await ServiceWith(db, baseline, applied).ApplyAsync(new BatchApplyRequest())).VideosTouched);

        // The same torrent id, re-downloaded after the tracker added a tag. An edit does not mint a new
        // id, so growth in the tag list is the only thing that distinguishes this from the row above —
        // which is why the baseline is the raw list size and not what the video is missing.
        var retagged = Torrent("scene", ["kissing", "vintage", "oil"], SceneSize, torrentId: "999");

        var row = Assert.Single((await ServiceWith(db, baseline, retagged).ListAsync()).Rows);
        Assert.Equal("updated", row.Status);
    }

    [Fact]
    public async Task Keeps_an_updated_row_out_of_bulk_apply()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        db.Tags.Add(new Tag { Name = "Vintage" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize, tagNames: ["Kissing"]);
        db.Set<VideoRemoteId>().Add(new VideoRemoteId { VideoId = videoId, Endpoint = "torrent-metadata", RemoteId = "999" });
        await db.SaveChangesAsync();

        var result = await Service(db, Torrent("scene", ["kissing", "vintage"], SceneSize, torrentId: "999"))
            .ApplyAsync(new BatchApplyRequest());

        // Visible, deliberately not eligible. A row can have tags left because the reviewer declined
        // them — most often on a pack, where most of the list belongs to other scenes — and a bulk run
        // that swept those up would overwrite a decision rather than deliver an update.
        Assert.Equal(0, result.VideosTouched);
        Assert.Empty(await db.Set<VideoTag>().Where(link => link.VideoId == videoId && link.Tag!.Name == "Vintage").ToListAsync());
    }

    [Fact]
    public async Task Reports_one_row_per_video_in_a_pack()
    {
        await using var db = CreateContext();
        await SeedVideoAsync(db, SceneSize);
        await SeedVideoAsync(db, OtherSize);

        var pack = Torrent("pack", ["kissing"], SceneSize, OtherSize);
        var overview = await Service(db, pack).ListAsync();

        Assert.Equal(2, overview.Rows.Count);
        Assert.All(overview.Rows, row => Assert.Equal(2, row.FanOut));
        Assert.All(overview.Rows, row => Assert.Equal("matched", row.Status));

        // The two counts are not interchangeable, and a pack is where they part company: rows and
        // Unmatched are per video file, Torrents is per .torrent. Reporting one as the other is how
        // the page came to claim 139,141 "torrent files" for a folder holding 3218.
        Assert.Equal(2, overview.IndexedFiles);
        Assert.Equal(1, overview.Torrents);
    }

    [Fact]
    public async Task Summarises_every_scene_of_a_pack_identically()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        db.Performers.Add(new Performer { Name = "Jane Doe" });
        await db.SaveChangesAsync();

        await SeedVideoAsync(db, SceneSize);
        await SeedVideoAsync(db, OtherSize);

        // One tag list, two scenes, and **both videos start bare** — so with nothing to subtract, every
        // row of the pack must read the same. It is asserted because the classification behind it is
        // computed once per torrent and reused across its scenes: a cache keyed wrongly, or one
        // that let a row mutate what the next row reads, would show up here and nowhere else.
        //
        // The summary is no longer a pure function of the tag list — it is counted against each video
        //, which is what `Counts_only_what_a_video_does_not_already_carry` covers. The videos
        // being identically empty is what isolates the cache from that, and is the reason this test
        // seeds no tags on either.
        var pack = Torrent("pack", ["kissing", "jane.doe", "deep.blue.sea", "1080p"], SceneSize, OtherSize);
        var rows = (await Service(db, pack).ListAsync()).Rows;

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(rows[0].TagsToAdd, row.TagsToAdd));
        Assert.All(rows, row => Assert.Equal(rows[0].TagsToCreate, row.TagsToCreate));
        Assert.All(rows, row => Assert.Equal(rows[0].PerformersToAdd, row.PerformersToAdd));

        // And not identically empty, which would satisfy the above while asserting nothing.
        Assert.Equal(2, rows[0].TagsToAdd);
        Assert.Equal(1, rows[0].TagsToCreate);
        Assert.Equal(1, rows[0].PerformersToAdd);
    }

    // ---------------------------------------------------------------------
    // Bulk apply
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Bulk_apply_takes_single_scene_metadata_when_two_torrents_name_the_same_file()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        db.Tags.Add(new Tag { Name = "Vintage" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize);

        // Same torrent name and same basename from two payloads — a scene, and a siterip that also
        // holds it. Real: 2.32% of corpus sizes are shared, and 20 of the library's own files match
        // more than one torrent. The apply loop resolves that key to one entry, and it has to be the
        // single scene, whose tag list is about this video rather than a union over a whole release.
        var scene = Torrent("dup", ["kissing"], SceneSize);
        var pack = Torrent("dup", ["vintage"], null, [SceneSize, OtherSize]);

        var result = await Service(db, pack, scene).ApplyAsync(new BatchApplyRequest());

        Assert.Equal(1, result.VideosTouched);
        var applied = await db.Set<VideoTag>()
            .Where(link => link.VideoId == videoId)
            .Join(db.Tags, link => link.TagId, tag => tag.Id, (_, tag) => tag.Name)
            .ToListAsync();
        Assert.Equal(["Kissing"], applied);
    }

    [Fact]
    public async Task Still_takes_the_single_scene_when_packs_are_included()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        db.Tags.Add(new Tag { Name = "Vintage" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize);

        var scene = Torrent("dup", ["kissing"], SceneSize);
        var pack = Torrent("dup", ["vintage"], null, [SceneSize, OtherSize]);

        // The same shape as the test above, but with packs opted in — which is the only way the
        // tiebreak is actually reached. Without `includePacks` the fan-out filter drops the pack first,
        // so that test passes whether or not the tiebreak exists (found by mutation).
        var result = await Service(db, pack, scene)
            .ApplyAsync(new BatchApplyRequest { IncludePacks = true });

        Assert.True(result.VideosTouched > 0);
        var applied = await db.Set<VideoTag>()
            .Where(link => link.VideoId == videoId)
            .Join(db.Tags, link => link.TagId, tag => tag.Id, (_, tag) => tag.Name)
            .ToListAsync();
        Assert.Equal(["Kissing"], applied);
    }

    [Fact]
    public async Task Gives_one_bulk_run_a_single_provenance_run_id_across_every_video()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize);
        await SeedVideoAsync(db, OtherSize);

        var result = await Service(db, Torrent("one", ["kissing"], SceneSize), Torrent("two", ["kissing"], OtherSize))
            .ApplyAsync(new BatchApplyRequest());

        // Two videos, one run. Letting each video generate its own id would still record provenance,
        // but "undo that run" would then be as many purges as there were videos — which at corpus
        // scale is not an undo at all. The applier is what generates one per call, so this pins
        // the wiring that hands it a shared one rather than the generator itself.
        Assert.Equal(2, result.VideosTouched);
        var runIds = await db.Set<TagApplication>().Select(application => application.SourceRunId).ToListAsync();
        Assert.Equal(2, runIds.Count);
        Assert.Single(runIds.Distinct());
    }

    [Fact]
    public async Task Existing_only_mode_applies_known_tags_and_creates_nothing()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize);

        var result = await Service(db, Torrent("scene", ["kissing", "unknown.tag"], SceneSize))
            .ApplyAsync(new BatchApplyRequest());

        Assert.Equal(1, result.VideosTouched);
        Assert.Equal(1, result.TagsAdded);
        Assert.Equal(0, result.TagsCreated);
        Assert.Equal(1, await db.Tags.CountAsync());
        Assert.Equal(1, await db.Set<VideoTag>().CountAsync(link => link.VideoId == videoId));
    }

    [Fact]
    public async Task All_tags_mode_creates_the_missing_ones()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize);

        var result = await Service(db, Torrent("scene", ["kissing", "unknown.tag"], SceneSize))
            .ApplyAsync(new BatchApplyRequest { CreateNewTags = true });

        Assert.Equal(2, result.TagsAdded);
        Assert.Equal(1, result.TagsCreated);
        Assert.Equal(2, await db.Tags.CountAsync());
    }

    [Fact]
    public async Task Applies_a_tag_the_library_holds_under_the_trackers_own_spelling()
    {
        await using var db = CreateContext();
        // What a dotted-style apply leaves behind: the tag is named as the tracker spells it, not as
        // the normaliser would.
        db.Tags.Add(new Tag { Name = "big.red.barn" });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize);

        var result = await Service(db, Torrent("scene", ["big.red.barn"], SceneSize))
            .ApplyAsync(new BatchApplyRequest());

        // "Existing tags only" is the default, so a tag the vocabulary check misses is not merely
        // mislabelled — it is silently not applied.
        Assert.Equal(1, result.VideosTouched);
        Assert.Equal(1, result.TagsAdded);
        Assert.Equal(0, result.TagsCreated);
        // And it links the row that was already there rather than creating a second one beside it.
        Assert.Single(await db.Tags.ToListAsync());
    }

    [Fact]
    public async Task Counts_a_tag_stored_under_the_trackers_spelling_as_existing()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "big.red.barn" });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize);

        var overview = await Service(db, Torrent("scene", ["big.red.barn"], SceneSize)).ListAsync();

        // The page's columns and the dialog's badges have to agree about which tags exist; they read
        // the same vocabulary through different code. The video carries nothing yet, so the tag is
        // still something this torrent would add — it just would not be created.
        var row = Assert.Single(overview.Rows);
        Assert.Equal(1, row.TagsToAdd);
        Assert.Equal(0, row.TagsToCreate);
    }

    [Fact]
    public async Task Counts_nothing_to_add_when_the_video_already_carries_every_proposed_tag()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        db.Tags.Add(new Tag { Name = "Big Waves" });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize, tagNames: ["Kissing", "Big Waves"]);

        var overview = await Service(db, Torrent("scene", ["kissing", "big.waves"], SceneSize)).ListAsync();

        // The case that read wrong: this column used to count the torrent's tags against the *library*
        // vocabulary, so a video already holding all of them still said "would add 2" — and applying
        // did not move the number, which made a freshly reloaded row look stale.
        var row = Assert.Single(overview.Rows);
        Assert.Equal(0, row.TagsToAdd);
        Assert.Equal(0, row.TagsToCreate);
        // The video's own count is untouched by any of this — it is what the video has, not what it
        // would gain.
        Assert.Equal(2, row.VideoTagCount);
    }

    [Fact]
    public async Task Counts_only_what_a_video_does_not_already_carry()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize, tagNames: ["Kissing"]);

        var overview = await Service(db, Torrent("scene", ["kissing", "big.waves"], SceneSize)).ListAsync();

        // One already on the video, one not. The one that counts is the one that is missing, and it
        // would be created — so the two numbers are equal here for a reason, not by coincidence.
        var row = Assert.Single(overview.Rows);
        Assert.Equal(1, row.TagsToAdd);
        Assert.Equal(1, row.TagsToCreate);
    }

    [Fact]
    public async Task Counts_only_the_performers_a_video_does_not_already_carry()
    {
        await using var db = CreateContext();
        db.Performers.Add(new Performer { Name = "Jane Doe" });
        db.Performers.Add(new Performer { Name = "Noa Amane" });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize, performerNames: ["Jane Doe"]);

        var overview = await Service(db, Torrent("scene", ["jane.doe", "noa.amane", "kissing"], SceneSize))
            .ListAsync();

        // The torrent names two performers and the video already has one of them. The number the
        // reviewer is deciding about is the one that is missing — this once read 2 whatever the
        // video carried, and applying never moved it.
        Assert.Equal(1, Assert.Single(overview.Rows).PerformersToAdd);
    }

    [Fact]
    public async Task Counts_nothing_to_add_when_the_video_holds_the_performer_found_under_an_alias()
    {
        await using var db = CreateContext();
        db.Performers.Add(new Performer
        {
            Name = "Jane Roe",
            Aliases = [new PerformerAlias { Alias = "jane doe" }],
        });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize, performerNames: ["Jane Roe"]);

        var overview = await Service(db, Torrent("scene", ["jane.doe"], SceneSize)).ListAsync();

        // The torrent writes the alias and the video carries the performer. Counted by id, those are
        // the same row and there is nothing to add; counted by name they would not have been.
        Assert.Equal(0, Assert.Single(overview.Rows).PerformersToAdd);
    }

    [Fact]
    public async Task Counts_a_tag_the_library_has_but_the_video_does_not_as_added_without_creating_it()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        // The tag exists; this video simply does not have it yet.
        await SeedVideoAsync(db, SceneSize);

        var overview = await Service(db, Torrent("scene", ["kissing"], SceneSize)).ListAsync();

        // The distinction the second number carries: a bulk apply with its default "existing tags
        // only" would apply this row, and would create nothing doing it.
        var row = Assert.Single(overview.Rows);
        Assert.Equal(1, row.TagsToAdd);
        Assert.Equal(0, row.TagsToCreate);
    }

    [Fact]
    public async Task Counts_nothing_to_add_when_the_video_holds_the_tag_under_the_trackers_spelling()
    {
        await using var db = CreateContext();
        // What a dotted-style apply leaves behind, and what this extension's own alias seeding builds
        // up: the library knows the tag by the tracker's spelling, not the normaliser's.
        db.Tags.Add(new Tag { Name = "big.red.barn" });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize, tagNames: ["big.red.barn"]);

        var overview = await Service(db, Torrent("scene", ["big.red.barn"], SceneSize)).ListAsync();

        // Both spellings have to reach the same tag id, or the column reports a tag this extension
        // created itself as one the video is still missing.
        var row = Assert.Single(overview.Rows);
        Assert.Equal(0, row.TagsToAdd);
        Assert.Equal(0, row.TagsToCreate);
    }

    [Fact]
    public async Task Counts_two_entries_that_reach_one_tag_once()
    {
        await using var db = CreateContext();
        var tag = new Tag { Name = "Deep Blue Sea" };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        // Both spellings are the same tag, which is exactly what seeding an alias on apply produces.
        db.Set<TagAlias>().Add(new TagAlias { TagId = tag.Id, Alias = "deep.blue.sea" });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize);

        var overview = await Service(db, Torrent("scene", ["deep.blue.sea", "deep blue sea"], SceneSize))
            .ListAsync();

        // One tag would be linked, so the column must say one. The apply folds these by name anyway,
        // so counting both would promise more than an apply delivers.
        var row = Assert.Single(overview.Rows);
        Assert.Equal(1, row.TagsToAdd);
        Assert.Equal(0, row.TagsToCreate);
    }

    [Fact]
    public async Task Applies_the_first_spelling_when_two_entries_reach_one_name()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Deep Blue Sea" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize);

        // Both entries normalise to one name, so only one of the two tracker spellings can be sent:
        // `TagSources` is one source per name at both ends of the wire.
        var result = await Service(db, Torrent("scene", ["deep.blue.sea", "deep blue sea"], SceneSize))
            .ApplyAsync(new BatchApplyRequest());

        // The *first* one, which is the rule the review path already follows — DESIGN-DECISIONS,
        // "One proposed tag per name". The apply path used to keep the last, and the two spellings
        // are not interchangeable: the dotted one is a spelling the library does not hold and gets
        // seeded as an alias, while the second is the tag's own name and seeds nothing at all. So the
        // divergence cost a match the *next* torrent would otherwise have made by alias.
        Assert.Equal(1, result.TagsAdded);
        Assert.Equal(1, result.AliasesSeeded);
        var alias = Assert.Single(await db.Set<TagAlias>().ToListAsync());
        Assert.Equal("deep.blue.sea", alias.Alias);
        Assert.Single(await db.Set<VideoTag>().Where(link => link.VideoId == videoId).ToListAsync());
    }

    [Fact]
    public async Task Skips_packs_by_default()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize);
        await SeedVideoAsync(db, OtherSize);

        var result = await Service(db, Torrent("pack", ["kissing"], SceneSize, OtherSize))
            .ApplyAsync(new BatchApplyRequest());

        // A pack's tag list is the union across its scenes, so applying it wholesale tags each video
        // with the others' content. Nothing is written unless the caller opts in.
        //
        // Asserted as "nothing was written" rather than as a count of what was declined: the count
        // used to be computed over the whole folder instead of the request, and this test agreed with
        // it only because it applies to everything at once, so the two coincided.
        Assert.Equal(0, result.VideosTouched);
        Assert.Empty(await db.Set<VideoTag>().ToListAsync());
    }

    [Fact]
    public async Task Applies_packs_when_explicitly_included()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize);
        await SeedVideoAsync(db, OtherSize);

        var result = await Service(db, Torrent("pack", ["kissing"], SceneSize, OtherSize))
            .ApplyAsync(new BatchApplyRequest { IncludePacks = true });

        Assert.Equal(2, result.VideosTouched);
    }

    [Fact]
    public async Task Skips_rows_that_were_already_applied()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize);
        db.Set<VideoRemoteId>().Add(new VideoRemoteId { VideoId = videoId, Endpoint = "torrent-metadata", RemoteId = "999" });
        await db.SaveChangesAsync();

        var result = await Service(db, Torrent("scene", ["kissing"], SceneSize, torrentId: "999"))
            .ApplyAsync(new BatchApplyRequest());

        // Re-running a batch must not churn videos that already took this torrent's metadata.
        Assert.Equal(0, result.VideosTouched);
    }

    [Fact]
    public async Task Applies_only_the_requested_rows()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        var wanted = await SeedVideoAsync(db, SceneSize);
        var other = await SeedVideoAsync(db, OtherSize);

        var index = new TorrentIndex();
        index.Add(Torrent("a", ["kissing"], SceneSize));
        index.Add(Torrent("b", ["kissing"], OtherSize));

        var result = await new TorrentBatchService(db, index, new TorrentMetadataSettings())
            .ApplyAsync(new BatchApplyRequest { Rows = [Row(wanted, "a")] });

        Assert.Equal(1, result.VideosTouched);
        Assert.Equal(1, await db.Set<VideoTag>().CountAsync(link => link.VideoId == wanted));
        Assert.Equal(0, await db.Set<VideoTag>().CountAsync(link => link.VideoId == other));
    }

    [Fact]
    public async Task Applies_only_the_named_row_when_two_torrents_describe_the_same_file()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        db.Tags.Add(new Tag { Name = "Vintage" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize);

        // The reason the request stopped speaking video ids. Two differently-named torrents
        // describe this one file, so the overview carries two rows for one video — 2.32% of corpus
        // sizes are shared and 20 of the real library's files are in this position. Addressed by video,
        // picking either row applied both, and nothing in the request could say which was meant.
        var index = new TorrentIndex();
        index.Add(Torrent("first", ["kissing"], SceneSize));
        index.Add(Torrent("second", ["vintage"], SceneSize));

        var service = new TorrentBatchService(db, index, new TorrentMetadataSettings());
        Assert.Equal(2, (await service.ListAsync()).Rows.Count);

        var result = await service.ApplyAsync(new BatchApplyRequest { Rows = [Row(videoId, "second")] });

        Assert.Equal(1, result.VideosTouched);
        var applied = await db.Set<VideoTag>()
            .Where(link => link.VideoId == videoId)
            .Join(db.Tags, link => link.TagId, tag => tag.Id, (_, tag) => tag.Name)
            .ToListAsync();
        Assert.Equal(["Vintage"], applied);
    }

    [Fact]
    public async Task Applies_one_video_when_a_pack_holds_two_files_of_the_same_name()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        var disc1 = await SeedVideoAsync(db, SceneSize);
        var disc2 = await SeedVideoAsync(db, OtherSize);

        // The same failure surviving in the other axis of the same key. `Basename` strips the directory, so
        // a pack holding `Disc1/01.mp4` beside `Disc2/01.mp4` produced two rows agreeing on *both* halves
        // of the old (torrent name, file name) key — 53 basename buckets over 3,202 corpus torrents. They
        // are different library videos, so naming one row applied two.
        var index = new TorrentIndex();
        index.Add(new TorrentRelease
        {
            Name = "pack",
            TagList = ["kissing"],
            Videos = [new TorrentVideoFile("Disc1/01.mp4", SceneSize), new TorrentVideoFile("Disc2/01.mp4", OtherSize)],
        });

        var service = new TorrentBatchService(db, index, new TorrentMetadataSettings());
        var rows = (await service.ListAsync()).Rows;

        // Two rows the old key could not tell apart: same torrent, same file name, different video.
        Assert.Equal(2, rows.Count);
        Assert.Single(rows.Select(row => row.FileName).Distinct());
        Assert.Equal(2, rows.Select(row => row.VideoId).Distinct().Count());

        // Named rather than swept, so the pack flag is not what is under test here.
        var result = await service.ApplyAsync(new BatchApplyRequest { Rows = [Row(disc1, "pack")] });

        Assert.Equal(1, result.VideosTouched);
        Assert.Equal(1, await db.Set<VideoTag>().CountAsync(link => link.VideoId == disc1));
        Assert.Equal(0, await db.Set<VideoTag>().CountAsync(link => link.VideoId == disc2));
    }

    [Fact]
    public async Task Shows_one_row_for_two_copies_of_one_torrent_id_and_counts_the_one_it_applies()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        db.Tags.Add(new Tag { Name = "Vintage" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize);

        // A tracker keeps a torrent's id when its tag list is edited, so re-downloading a re-tagged
        // release yields a second file with the same id and name and different bytes — which the
        // content de-duplication in `ReloadIndex` does not collapse, because the bytes differ.
        //
        // Both copies used to reach the overview as their own row while the apply resolved both to
        // whichever one `TorrentEntryPreference` picked. So the page showed two identical rows, and the
        // count on each came from a different file than the one it would write.
        var index = new TorrentIndex();
        index.Add(Torrent("release", ["kissing"], "4242", [SceneSize]));
        index.Add(Torrent("release", ["kissing", "vintage"], "4242", [SceneSize]));

        var service = new TorrentBatchService(db, index, new TorrentMetadataSettings());
        var row = Assert.Single((await service.ListAsync()).Rows);
        Assert.Equal("4242", row.TorrentId);

        // The whole point of collapsing them: what the row claims it would add is what it then adds.
        // Both tags exist in the library, so the losing copy would have written two.
        Assert.Equal(1, row.TagsToAdd);

        var result = await service.ApplyAsync(
            new BatchApplyRequest { Rows = [Row(videoId, "release", "4242")] });

        Assert.Equal(1, result.VideosTouched);
        Assert.Equal(row.TagsToAdd, await db.Set<VideoTag>().CountAsync(link => link.VideoId == videoId));
    }

    [Fact]
    public async Task Applies_a_named_pack_row_without_the_pack_flag()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize);

        // Naming a row *is* the explicit inclusion `IncludePacks` exists to ask for. Filtering it out
        // anyway is a request that reports success and writes nothing, which is the silent half of
        // named rows — and the reviewer would have ticked that row on purpose, one pack at a time.
        var index = new TorrentIndex();
        index.Add(Torrent("pack", ["kissing"], null, [SceneSize, OtherSize]));

        var result = await new TorrentBatchService(db, index, new TorrentMetadataSettings())
            .ApplyAsync(new BatchApplyRequest { Rows = [Row(videoId, "pack")] });

        Assert.Equal(1, result.VideosTouched);
        Assert.Equal(1, await db.Set<VideoTag>().CountAsync(link => link.VideoId == videoId));
    }

    [Fact]
    public async Task Sweeps_every_eligible_row_when_the_request_names_none()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize);
        await SeedVideoAsync(db, OtherSize);

        // The other half of the contract, and the one every other test in this file leans on: an empty
        // list is the sweep, not an empty selection. The page always names what it means, so this path
        // is reached by a caller asking for everything.
        var index = new TorrentIndex();
        index.Add(Torrent("a", ["kissing"], SceneSize));
        index.Add(Torrent("b", ["kissing"], OtherSize));

        var result = await new TorrentBatchService(db, index, new TorrentMetadataSettings())
            .ApplyAsync(new BatchApplyRequest());

        Assert.Equal(2, result.VideosTouched);
    }

    [Fact]
    public async Task Ignores_a_named_row_the_overview_does_not_hold()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize);

        // A stale page can name a row a rescan has since dropped. That is nothing to apply rather than
        // something to fail on: the sweep it would otherwise fall back to is every eligible row, which
        // is the one answer that must never come out of a request that named one row.
        var index = new TorrentIndex();
        index.Add(Torrent("a", ["kissing"], SceneSize));

        var result = await new TorrentBatchService(db, index, new TorrentMetadataSettings())
            .ApplyAsync(new BatchApplyRequest { Rows = [Row(videoId, "gone")] });

        Assert.Equal(0, result.VideosTouched);
        Assert.Empty(await db.Set<VideoTag>().ToListAsync());
    }

    [Fact]
    public async Task Never_writes_fields_in_bulk()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize, title: null);

        await Service(db, Torrent("scene", ["kissing", "2018.03.20", "lanternbay.com"], SceneSize))
            .ApplyAsync(new BatchApplyRequest());

        // Bulk sends tags, performers and provenance only. Title/date/studio stay a review decision even
        // when the video's fields are empty.
        var video = await db.Videos.AsNoTracking().FirstAsync(candidate => candidate.Id == videoId);
        Assert.Null(video.Title);
        Assert.Null(video.Date);
        Assert.Null(video.StudioId);
    }

    [Fact]
    public async Task Lifts_known_performers_out_of_the_tag_list_in_bulk()
    {
        await using var db = CreateContext();
        db.Performers.Add(new Performer { Name = "Angela Frost" });
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize);

        var result = await Service(db, Torrent("scene", ["angela.frost", "kissing"], SceneSize))
            .ApplyAsync(new BatchApplyRequest { CreateNewTags = true });

        // Without the real performer vocabulary a name-shaped entry would be created as a tag — the bug
        // this guards is a bulk run inventing tags named after performers.
        Assert.Equal(1, result.PerformersAdded);
        Assert.Equal(1, await db.Set<VideoPerformer>().CountAsync(link => link.VideoId == videoId));
        Assert.DoesNotContain("Angela Frost", await db.Tags.Select(tag => tag.Name).ToListAsync());
    }

    [Fact]
    public async Task Writes_provenance_so_a_second_run_is_a_no_op()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db, SceneSize);
        var torrent = Torrent("scene", ["kissing"], SceneSize, torrentId: "1133888");

        var first = await Service(db, torrent).ApplyAsync(new BatchApplyRequest());
        var second = await Service(db, torrent).ApplyAsync(new BatchApplyRequest());

        Assert.Equal(1, first.VideosTouched);
        Assert.Equal(0, second.VideosTouched);
        Assert.Equal(1, await db.Set<VideoRemoteId>().CountAsync(link => link.VideoId == videoId));
    }

    /// <summary>
    /// A bulk apply does not read the baseline store at all.
    ///
    /// `LoadAsync` serves both entry points, and apply was paying for the whole overview — including a
    /// full materialisation of the extension store, which holds every cached cover URL as well as the
    /// baselines because `IExtensionStore` has no prefix query. The UI chunks bulk apply in tens, so a
    /// 715-row folder paid that 72 times for figures only the page renders.
    ///
    /// Counted rather than timed, and asserted in both directions: the overview still reads it exactly
    /// once, so this pins the split rather than the removal.
    /// </summary>
    [Fact]
    public async Task Does_not_read_the_baseline_store_while_applying()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        await SeedVideoAsync(db, SceneSize);

        var store = new FakeExtensionStore();
        var baseline = new AppliedTorrentBaseline();
        baseline.AttachStore(store);
        var service = ServiceWith(db, baseline, Torrent("scene", ["kissing"], SceneSize, torrentId: "999"));

        Assert.Equal(1, (await service.ApplyAsync(new BatchApplyRequest())).VideosTouched);
        Assert.Equal(0, store.GetAllCalls);

        await service.ListAsync();
        Assert.Equal(1, store.GetAllCalls);
    }

    [Fact]
    public async Task Does_nothing_when_the_index_is_empty()
    {
        await using var db = CreateContext();
        await SeedVideoAsync(db, SceneSize);

        var service = new TorrentBatchService(db, new TorrentIndex(), new TorrentMetadataSettings());

        var overview = await service.ListAsync();
        Assert.Empty(overview.Rows);
        Assert.Equal(0, overview.Unmatched);
        Assert.Equal(0, overview.IndexedFiles);
        Assert.Equal(0, overview.Torrents);
        Assert.Equal(0, (await service.ApplyAsync(new BatchApplyRequest())).VideosTouched);
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// A service sharing one <see cref="AppliedTorrentBaseline"/> across calls, so an apply and the
    /// overview after it — or after a re-download — see the same recorded baselines.
    /// </summary>
    private static TorrentBatchService ServiceWith(
        CoveContext db, AppliedTorrentBaseline baseline, params TorrentRelease[] torrents)
    {
        var index = new TorrentIndex();
        foreach (var torrent in torrents)
            index.Add(torrent);
        return new TorrentBatchService(db, index, new TorrentMetadataSettings(), baseline: baseline);
    }

    private static TorrentBatchService Service(CoveContext db, params TorrentRelease[] torrents)
    {
        var index = new TorrentIndex();
        foreach (var torrent in torrents)
            index.Add(torrent);
        return new TorrentBatchService(db, index, new TorrentMetadataSettings());
    }

    /// <summary>
    /// One row of the batch overview, by what identifies it: the video, and which torrent describes it.
    ///
    /// It was <c>(torrent name, file basename)</c>, and neither half is unique — a pack holding
    /// two same-named scenes produced two rows sharing both, so naming one applied both.
    /// </summary>
    private static BatchRowRef Row(int videoId, string torrent, string? torrentId = null) =>
        new() { VideoId = videoId, TorrentName = torrent, TorrentId = torrentId };

    private static TorrentRelease Torrent(string name, string[] tags, params long[] sizes) =>
        Torrent(name, tags, null, sizes);

    private static TorrentRelease Torrent(string name, string[] tags, long size, string? torrentId) =>
        Torrent(name, tags, torrentId, [size]);

    private static TorrentRelease Torrent(string name, string[] tags, string? torrentId, long[] sizes) => new()
    {
        Name = name,
        TagList = tags,
        Comment = torrentId is null ? null : $"https://tracker.example/torrents.php?id={torrentId}",
        CoverUrl = "https://example.invalid/cover.jpg",
        Videos = [.. sizes.Select((size, i) => new TorrentVideoFile($"{name}-{i}.mp4", size))],
    };

    private static async Task<int> SeedVideoAsync(
        CoveContext db,
        long size,
        string? title = "video",
        string[]? tagNames = null,
        string[]? performerNames = null,
        string? imageBlobId = null,
        string? basename = null)
    {
        var video = new Video { Title = title, ImageBlobId = imageBlobId };
        db.Videos.Add(video);
        await db.SaveChangesAsync();

        // Folder paths are unique, so each seeded video gets its own rather than colliding.
        var folder = new Folder { Path = $"/library/{video.Id}" };
        db.Set<Folder>().Add(folder);
        await db.SaveChangesAsync();

        db.VideoFiles.Add(new VideoFile
        {
            // Unique per video by default, so nothing accidentally name-matches; the name-only count
            // is about files deliberately given a torrent's name.
            Basename = basename ?? $"video-{video.Id}.mp4",
            ParentFolderId = folder.Id,
            Size = size,
            VideoId = video.Id,
        });

        foreach (var name in tagNames ?? [])
        {
            var tag = await db.Tags.FirstAsync(candidate => candidate.Name == name);
            db.Set<VideoTag>().Add(new VideoTag { VideoId = video.Id, TagId = tag.Id });
        }

        foreach (var name in performerNames ?? [])
        {
            var performer = await db.Performers.FirstAsync(candidate => candidate.Name == name);
            db.Set<VideoPerformer>().Add(new VideoPerformer { VideoId = video.Id, PerformerId = performer.Id });
        }

        await db.SaveChangesAsync();
        return video.Id;
    }

    /// <summary>A second file on an existing video — different encode, moved copy.</summary>
    private static async Task AttachFileAsync(CoveContext db, int videoId, string basename, long size)
    {
        var folder = new Folder { Path = $"/library/{videoId}/{basename}" };
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

    /// <summary>A torrent whose payload file names are chosen rather than derived from its own name.</summary>
    private static TorrentRelease TorrentNamed(string name, params (string File, long Size)[] files) => new()
    {
        Name = name,
        TagList = ["kissing"],
        CoverUrl = "https://example.invalid/cover.jpg",
        Videos = [.. files.Select(file => new TorrentVideoFile(file.File, file.Size))],
    };

    // ---------------------------------------------------------------------
    // A row that fails
    // ---------------------------------------------------------------------

    /// <summary>
    /// One bad row must not cost the rest of the run.
    ///
    /// The rows are independent by construction — a save and a tracker clear each — and everything
    /// before a throw is committed either way, so stopping would buy a shorter run and strictly less
    /// information about it.
    /// </summary>
    [Fact]
    public async Task A_row_that_throws_is_skipped_and_the_run_continues()
    {
        var failing = new HashSet<int>();
        await using var db = CreateContext(failing);
        var first = await SeedVideoAsync(db, SceneSize);
        var second = await SeedVideoAsync(db, OtherSize);
        var service = Service(db, Torrent("a", ["kissing"], SceneSize), Torrent("b", ["vintage"], OtherSize));

        failing.Add(first);
        var result = await service.ApplyAsync(new BatchApplyRequest { CreateNewTags = true });

        Assert.Equal(1, result.RowsFailed);
        Assert.Equal(1, result.VideosTouched);
        Assert.False(result.StoppedEarly);

        // A count with no sample is not actionable, and the reason is the same one on every row when
        // the cause is systemic — which is the case this reporting shape exists for.
        Assert.NotNull(result.FailureReason);
        Assert.Contains("conflict", result.FailureReason);

        // The surviving row really wrote, rather than the counter merely being incremented.
        Assert.Equal(1, await db.Set<VideoTag>().CountAsync(link => link.VideoId == second));
        Assert.Equal(0, await db.Set<VideoTag>().CountAsync(link => link.VideoId == first));
    }

    /// <summary>
    /// Five failures in a row is no longer "some rows are bad" — it is the library or the database
    /// failing on its own state, and the remaining rows would produce identical failures and no new
    /// information.
    /// </summary>
    [Fact]
    public async Task Stops_itself_once_five_rows_fail_in_a_row()
    {
        var failing = new HashSet<int>();
        await using var db = CreateContext(failing);
        var torrents = new List<TorrentRelease>();
        for (var i = 0; i < 8; i++)
        {
            var size = SceneSize + i;
            failing.Add(await SeedVideoAsync(db, size));
            torrents.Add(Torrent($"t{i}", ["kissing"], size));
        }

        var result = await Service(db, [.. torrents]).ApplyAsync(new BatchApplyRequest { CreateNewTags = true });

        // Exactly the threshold, so the three rows after it were never attempted.
        Assert.Equal(5, result.RowsFailed);
        Assert.True(result.StoppedEarly);
        Assert.Equal(0, result.VideosTouched);
    }

    /// <summary>
    /// The breaker counts <em>consecutive</em> failures, so a run that keeps succeeding between bad
    /// rows works through the whole selection however many fail in total.
    ///
    /// The failing rows are chosen after reading the order the run will visit them in, rather than by
    /// a rule over ids: which row is nth is the overview's business, and a test that assumed it would
    /// be asserting that instead of this.
    /// </summary>
    [Fact]
    public async Task A_row_that_succeeds_clears_the_failure_run()
    {
        var failing = new HashSet<int>();
        await using var db = CreateContext(failing);
        var torrents = new List<TorrentRelease>();
        for (var i = 0; i < 12; i++)
        {
            var size = SceneSize + i;
            await SeedVideoAsync(db, size);
            torrents.Add(Torrent($"t{i}", ["kissing"], size));
        }

        var service = Service(db, [.. torrents]);
        var order = (await service.ListAsync()).Rows.Select(row => row.VideoId).ToList();
        foreach (var videoId in order.Where((_, index) => index % 2 == 0))
            failing.Add(videoId);

        var result = await service.ApplyAsync(new BatchApplyRequest { CreateNewTags = true });

        Assert.Equal(6, result.RowsFailed);
        Assert.Equal(6, result.VideosTouched);
        Assert.False(result.StoppedEarly);
    }

    /// <summary>
    /// A cancelled run is not a run of failed rows. Counting it as one would report a caller that went
    /// away as a partly broken library, and the breaker would swallow the cancellation entirely.
    ///
    /// The cancellation is raised from inside a row rather than by handing in an already-cancelled
    /// token: that token throws in <c>LoadAsync</c>, before the loop exists, so the test would pass
    /// whatever the catch did. It did, on the first attempt at this.
    /// </summary>
    [Fact]
    public async Task Cancellation_from_inside_a_row_is_not_counted_as_a_failure()
    {
        var failing = new HashSet<int>();
        await using var db = CreateContext(failing, () => new OperationCanceledException());
        failing.Add(await SeedVideoAsync(db, SceneSize));
        await SeedVideoAsync(db, OtherSize);
        var service = Service(db, Torrent("a", ["kissing"], SceneSize), Torrent("b", ["vintage"], OtherSize));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ApplyAsync(new BatchApplyRequest { CreateNewTags = true }));
    }

    /// <summary>
    /// A context that throws on the save writing a chosen video's tag links.
    ///
    /// The seam is the save rather than the applier because <c>ApplyAsync</c> constructs its own
    /// <see cref="TorrentApplyService"/> deliberately — that is how the cover dependencies reach it —
    /// so an injectable applier would be a hole cut in production code for a test. Throwing here also
    /// puts the failure where a real one lands: after the row has already written part of itself,
    /// which is why <c>RowsFailed</c> is documented as a floor.
    /// </summary>
    private sealed class FailsOnVideo(
        DbContextOptions<CoveContext> options,
        HashSet<int> failing,
        Func<Exception>? throws = null)
        : CoveContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            ChangeTracker.Entries<VideoTag>().Any(entry => failing.Contains(entry.Entity.VideoId))
                ? throw (throws ?? (() => new InvalidOperationException("tag namespace conflict")))()
                : base.SaveChangesAsync(cancellationToken);
    }

    private static CoveContext CreateContext(HashSet<int> failing, Func<Exception>? throws = null)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new FailsOnVideo(options, failing, throws);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
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
}
