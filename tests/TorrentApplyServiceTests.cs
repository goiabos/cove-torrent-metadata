using Cove.Core.Entities;
using Cove.Data;
using Cove.TorrentMetadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// Covers writing a reviewed proposal back onto a video.
///
/// The rules that matter here are restraint rules: a torrent is a suggestion, not an authority, so it
/// may fill an empty field but never overwrite one, and it may only add relations. Those are asserted
/// directly, because getting them wrong would quietly damage a library rather than fail loudly.
/// </summary>
public class TorrentApplyServiceTests
{
    [Fact]
    public async Task Adds_selected_tags_and_creates_only_what_is_missing()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        var videoId = await SeedVideoAsync(db);

        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["Kissing", "big red barn"],
        });

        Assert.Equal(2, result!.TagsAdded);
        Assert.Equal(1, result.TagsCreated);
        Assert.Equal(2, await db.Set<VideoTag>().CountAsync(link => link.VideoId == videoId));
    }

    [Fact]
    public async Task Seeds_the_dotted_source_form_as_a_tag_alias()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);

        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["big red barn"],
            TagSources = { ["big red barn"] = "big.red.barn" },
        });

        // Only that the row is written. That it then resolves anything is the other half, and it was
        // missing for as long as this comment claimed it — the round trip is asserted in
        // `TorrentMatchServiceTests.Resolves_through_an_alias_an_earlier_apply_seeded`.
        Assert.Equal(1, result!.AliasesSeeded);
        Assert.True(await db.Set<TagAlias>().AnyAsync(alias => alias.Alias == "big.red.barn"));
    }

    /// <summary>
    /// A blank tag name in the request creates nothing.
    ///
    /// The classifier drops empty tag-list entries so one can never be proposed, but this list comes
    /// from a browser and never passes through the classifier — so the write side needs its own guard
    /// or the whole defence rests on the client. Cove trims a canonical name and maps an empty one to
    /// the literal `&lt;empty&gt;`, so a row created here would claim that name inside a namespace the
    /// host enforces, and no later tag could ever be called it.
    /// </summary>
    [Fact]
    public async Task Creates_no_tag_for_a_blank_name()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);

        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["", "   ", "kissing"],
        });

        Assert.Equal(1, result!.TagsCreated);
        Assert.Equal(["kissing"], await db.Tags.Select(tag => tag.Name).ToListAsync());
    }

    /// <summary>
    /// A padded source spelling does not seed a second copy of an alias the tag already has.
    ///
    /// The duplicate guard canonicalised its two sides differently: the known set was built with the
    /// database's `lower()` and no trim, while the probe used .NET's `ToLowerInvariant` — also with no
    /// trim, on the line directly below one that *does* trim for the collision-owner lookup. So a
    /// source arriving with surrounding whitespace missed the set and inserted again.
    ///
    /// It is `TagSources` that makes this reachable rather than stored data. Cove trims an alias inside
    /// `SaveChanges`, so a padded row cannot exist; but this value comes from a browser and nothing on
    /// the way in trims it. The insert then lands, Cove trims it to a spelling the tag already answers
    /// to, and 1.3 refuses the save — which fails the whole apply, since it is one transaction, and
    /// loses every tag the reviewer had approved.
    /// </summary>
    [Fact]
    public async Task Does_not_seed_an_alias_the_tag_already_has_under_a_padded_spelling()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Deep Blue Sea", Aliases = [new TagAlias { Alias = "deep.blue.sea" }] });
        var videoId = await SeedVideoAsync(db);

        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["Deep Blue Sea"],
            TagSources = { ["Deep Blue Sea"] = "  deep.blue.sea  " },
        });

        Assert.Equal(0, result!.AliasesSeeded);
        Assert.Equal(1, await db.Set<TagAlias>().CountAsync());
    }

    /// <summary>
    /// An apply does not leave the whole tag table in the change tracker.
    ///
    /// `RelationNameResolver.ResolveTagsAsync` is `db.Tags.Include(t => t.Aliases)` with no
    /// `AsNoTracking()`, on a context the caller goes on to save, so
    /// one call tracks every tag and every alias in the library for the rest of that context's life.
    /// On the real library that is 3,551 entities, and it turns a save that adds one tag from 8.8 ms
    /// into 372 ms — about 98% of a per-video apply spent on change detection over rows nobody is
    /// changing.
    ///
    /// Asserted as tracker contents and not as elapsed time, deliberately: a wall-clock assertion is
    /// the one kind of assertion that fails on a loaded machine for no reason, and it would be the one
    /// kind of test that fails on a loaded machine for no reason.
    ///
    /// The claim is a *bound*, not emptiness. Measured at the detach itself the tracker holds zero tags
    /// and zero aliases; by the end of the apply EF's own fixup during the final save has re-tracked
    /// the row that was actually linked. That is the difference between O(tags applied) and
    /// O(library), which is the whole finding — so the assertion is that nothing the apply did not
    /// touch survives, and every alias is gone.
    /// </summary>
    [Fact]
    public async Task Leaves_no_library_tag_tracked_after_resolving()
    {
        await using var db = CreateContext();
        for (var i = 0; i < 40; i++)
            db.Tags.Add(new Tag { Name = $"library tag {i}", Aliases = [new TagAlias { Alias = $"library.tag.{i}" }] });
        var videoId = await SeedVideoAsync(db);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Resolves an existing tag, so the apply itself creates nothing — everything the resolver
        // touched is a read, and every one of them used to stay.
        await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["library tag 7"],
        });

        // 40 tags and 40 aliases were read to resolve one name. Only the one name may remain.
        Assert.Equal(
            ["library tag 7"],
            db.ChangeTracker.Entries<Tag>().Select(entry => entry.Entity.Name).Order());
        Assert.Empty(db.ChangeTracker.Entries<TagAlias>());
    }

    /// <summary>
    /// The detach above does not take the tags the apply is creating with it.
    ///
    /// They are `Added` and the save is what assigns their ids, so detaching by type rather than by
    /// state would leave the apply writing links to id 0. The library is seeded large enough that the
    /// resolver has something to track, so this covers both halves in one pass: the reads go, the
    /// writes stay.
    /// </summary>
    [Fact]
    public async Task Still_creates_a_new_tag_while_the_resolver_reads_are_dropped()
    {
        await using var db = CreateContext();
        for (var i = 0; i < 40; i++)
            db.Tags.Add(new Tag { Name = $"library tag {i}", Aliases = [new TagAlias { Alias = $"library.tag.{i}" }] });
        var videoId = await SeedVideoAsync(db);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["something new"],
        });

        Assert.Equal(1, result!.TagsCreated);
        var created = await db.Tags.SingleAsync(tag => tag.Name == "something new");
        Assert.True(created.Id > 0);
        Assert.True(await db.Set<VideoTag>().AnyAsync(link => link.VideoId == videoId && link.TagId == created.Id));
    }

    [Fact]
    public async Task Resolves_through_an_existing_alias_instead_of_creating_a_duplicate()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Titjob", Aliases = [new TagAlias { Alias = "titfuck" }] });
        var videoId = await SeedVideoAsync(db);

        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["titfuck"],
        });

        Assert.Equal(0, result!.TagsCreated);
        Assert.Equal(1, await db.Tags.CountAsync());
    }

    /// <summary>
    /// The seeded alias may not take a spelling another tag is named by.
    ///
    /// Reachable whenever the library holds the normalised spelling on one tag and the dotted spelling
    /// on another — an early dotted-style apply creating `deep.blue.sea`, and anything else (a Cove
    /// scraper, the tag editor) naming a second tag `Deep Blue Sea`. The proposal then resolves on the
    /// normalised form and carries the dotted one as its source, so the alias write lands on the wrong
    /// side of a name the library has already given away.
    ///
    /// Names and aliases share one case-insensitive namespace, so the unguarded write throws. The
    /// guard is shaped by the quieter ancestor of that failure, where one tag ended up named what
    /// another was aliased by and which one a later resolve returned was not something the user could
    /// see.
    /// </summary>
    [Fact]
    public async Task Does_not_seed_an_alias_that_another_tag_is_named_by()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Deep Blue Sea" });
        db.Tags.Add(new Tag { Name = "deep.blue.sea" });
        var videoId = await SeedVideoAsync(db);

        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["Deep Blue Sea"],
            TagSources = { ["Deep Blue Sea"] = "deep.blue.sea" },
        });

        Assert.Equal(0, result!.AliasesSeeded);
        Assert.False(await db.Set<TagAlias>().AnyAsync());

        // The link is the thing the user asked for; the alias is an optimisation for later matching.
        // Losing the tag to protect the convenience would be the wrong way round.
        Assert.Equal(1, result.TagsAdded);
        Assert.Equal(1, await db.Set<VideoTag>().CountAsync(link => link.VideoId == videoId));
    }

    /// <summary>
    /// The same rule for a spelling another tag holds as an <em>alias</em> rather than as its name.
    /// One namespace under 1.3, so both halves have to be checked, and the guard in the code compares
    /// against whatever the resolver says owns the spelling rather than against names alone.
    /// </summary>
    [Fact]
    public async Task Does_not_seed_an_alias_another_tag_already_holds()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Deep Blue Sea" });
        db.Tags.Add(new Tag { Name = "Ocean", Aliases = [new TagAlias { Alias = "deep.blue.sea" }] });
        var videoId = await SeedVideoAsync(db);

        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["Deep Blue Sea"],
            TagSources = { ["Deep Blue Sea"] = "deep.blue.sea" },
        });

        Assert.Equal(0, result!.AliasesSeeded);
        Assert.Equal(1, await db.Set<TagAlias>().CountAsync());
        Assert.Equal(1, result.TagsAdded);
    }

    /// <summary>
    /// The collision can also be built inside one request: two entries of the same torrent, one named
    /// by the style and one already dotted, where the first's source is the second's name. Neither tag
    /// exists when the resolver runs, so this is the case a check against the database alone misses.
    /// </summary>
    [Fact]
    public async Task Does_not_seed_an_alias_that_another_tag_in_the_same_request_is_named_by()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);

        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["Deep Blue Sea", "deep.blue.sea"],
            TagSources = { ["Deep Blue Sea"] = "deep.blue.sea" },
        });

        Assert.Equal(2, result!.TagsCreated);
        Assert.Equal(0, result.AliasesSeeded);
        Assert.False(await db.Set<TagAlias>().AnyAsync());
    }

    [Fact]
    public async Task Applying_the_same_proposal_twice_changes_nothing_the_second_time()
    {
        await using var db = CreateContext();
        var performer = new Performer { Name = "Noa Amane" };
        db.Performers.Add(performer);
        await db.SaveChangesAsync();
        var performerId = performer.Id;
        var videoId = await SeedVideoAsync(db);
        var request = new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["kissing"],
            Performers = [performerId],
            Url = "https://tracker.example/torrents.php?id=1133888",
            TorrentId = "1133888",
        };

        var service = new TorrentApplyService(db);
        await service.ApplyAsync(request);
        var second = await service.ApplyAsync(request);

        // Re-running an import must be a no-op, not an accumulation.
        Assert.Equal(0, second!.TagsAdded);
        Assert.Equal(0, second.PerformersAdded);
        Assert.False(second.UrlAdded);
        Assert.Equal(1, await db.Set<VideoRemoteId>().CountAsync(link => link.VideoId == videoId));
    }

    [Fact]
    public async Task Fills_empty_fields_but_never_overwrites_existing_ones()
    {
        await using var db = CreateContext();
        var video = new Video { Title = "A title the user already set", Date = new DateOnly(2020, 1, 1) };
        db.Videos.Add(video);
        await db.SaveChangesAsync();

        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = video.Id,
            Title = "Torrent title",
            Date = "2018-03-20",
        });

        Assert.False(result!.TitleChanged);
        Assert.False(result.DateChanged);
        Assert.Equal("A title the user already set", (await db.Videos.FindAsync(video.Id))!.Title);
    }

    [Fact]
    public async Task Fills_a_field_that_is_empty()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db, title: null);

        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Title = "Torrent title",
            Date = "2018-03-20",
        });

        Assert.True(result!.TitleChanged);
        Assert.True(result.DateChanged);
    }

    [Fact]
    public async Task Links_an_existing_studio_but_never_creates_one()
    {
        await using var db = CreateContext();
        db.Studios.Add(new Studio { Name = "Lanternbay" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db);

        var linked = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            StudioName = "lanternbay",
        });

        Assert.True(linked!.StudioChanged);

        // A studio the library does not have is left alone: the tag list only carries a bare lowercase
        // domain, and creating from that would litter the library with near-duplicates.
        var otherId = await SeedVideoAsync(db);
        var unknown = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = otherId,
            StudioName = "someunknownsite",
        });

        Assert.False(unknown!.StudioChanged);
        Assert.Equal(1, await db.Studios.CountAsync());
    }

    [Fact]
    public async Task Stamps_provenance_on_tags_it_creates()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);

        await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["brand new tag"],
        });

        var tag = await db.Tags.SingleAsync();
        var definition = await db.Set<CustomFieldDefinition>()
            .SingleAsync(field => field.Key == TorrentApplyService.SourceFieldKey);
        var value = await db.Set<CustomFieldValue>().SingleAsync();

        Assert.Equal([CustomFieldEntityTypes.Tag], definition.EntityTypes);
        Assert.Equal(CustomFieldEntityTypes.Tag, value.EntityType);
        Assert.Equal(tag.Id, value.EntityId);
        Assert.Equal("torrent-metadata", value.TextValue);
    }

    [Fact]
    public async Task Does_not_stamp_provenance_on_a_tag_the_user_already_had()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db);

        await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["kissing"],
        });

        // The field lives on the tag globally, so labelling a user-made tag as imported would be a lie —
        // and would wrongly sweep it into any "undo the import" selection later.
        Assert.Empty(await db.Set<CustomFieldValue>().ToListAsync());
    }

    [Fact]
    public async Task Records_a_tag_application_for_every_link_it_writes()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Kissing" });
        var videoId = await SeedVideoAsync(db);

        await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["Kissing", "brand new tag"],
        });

        // Both links, not just the created tag: the custom field says a *tag* was invented here, and
        // this says a *video* carries it because of us. Only the second makes the work reversible —
        // the host purges tag_applications by source and then drops the links left behind them.
        var applications = await db.Set<TagApplication>().ToListAsync();
        Assert.Equal(2, applications.Count);
        Assert.All(applications, application =>
        {
            Assert.Equal(AffinityHostType.Video, application.HostType);
            Assert.Equal(videoId, application.HostId);
            Assert.Equal("torrent-metadata", application.SourceKey);
            Assert.NotEmpty(application.SourceRunId);
        });
        Assert.Equal(
            await db.Set<VideoTag>().Select(link => link.TagId).OrderBy(id => id).ToListAsync(),
            applications.Select(application => application.TagId).OrderBy(id => id).ToList());
    }

    [Fact]
    public async Task Does_not_claim_a_link_the_video_already_carried()
    {
        await using var db = CreateContext();
        var tag = new Tag { Name = "Kissing" };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db);
        db.Set<VideoTag>().Add(new VideoTag { VideoId = videoId, TagId = tag.Id });
        await db.SaveChangesAsync();

        await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["kissing", "brand new tag"],
        });

        // The hazard this guards is not cosmetic. Cove's own VideoMetadataApplyService records
        // provenance for every tag in its payload, including ones the video already had; a purge of
        // that source then deletes the link, because RemoveOrphanedTagLinksAsync drops any link with no
        // provenance left behind it — and almost no link in a real library has provenance at all. So
        // claiming a link we did not write hands the user's own tagging to our undo.
        var applications = await db.Set<TagApplication>().ToListAsync();
        var claimed = Assert.Single(applications);
        Assert.NotEqual(tag.Id, claimed.TagId);
    }

    [Fact]
    public async Task Groups_one_apply_under_a_single_run_id_and_a_later_one_under_another()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var service = new TorrentApplyService(db);

        await service.ApplyAsync(new TorrentApplyRequest { VideoId = videoId, Tags = ["one", "two"] });
        await service.ApplyAsync(new TorrentApplyRequest { VideoId = videoId, Tags = ["three"] });

        // Two applies, two run ids — which is what lets one of them be undone without the other. A
        // shared id would collapse them into a single blunt "everything this extension did".
        var runIds = await db.Set<TagApplication>().Select(application => application.SourceRunId).ToListAsync();
        Assert.Equal(3, runIds.Count);
        Assert.Equal(2, runIds.Distinct().Count());
    }

    [Fact]
    public async Task Takes_the_run_id_it_is_given_so_a_bulk_run_is_one_unit()
    {
        await using var db = CreateContext();
        var first = await SeedVideoAsync(db);
        var second = await SeedVideoAsync(db, "second video");
        var service = new TorrentApplyService(db);

        await service.ApplyAsync(new TorrentApplyRequest { VideoId = first, Tags = ["one"], SourceRunId = "run-7" });
        await service.ApplyAsync(new TorrentApplyRequest { VideoId = second, Tags = ["two"], SourceRunId = "run-7" });

        var runIds = await db.Set<TagApplication>().Select(application => application.SourceRunId).Distinct().ToListAsync();
        Assert.Equal(["run-7"], runIds);
    }

    [Fact]
    public async Task Reuses_one_provenance_definition_across_many_created_tags()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);

        await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["first new tag", "second new tag"],
        });

        Assert.Single(await db.Set<CustomFieldDefinition>().ToListAsync());
        Assert.Equal(2, await db.Set<CustomFieldValue>().CountAsync());
    }

    [Fact]
    public async Task Finds_the_provenance_definition_a_previous_apply_left_behind()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);

        await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["first new tag"],
        });

        // A fresh instance is the ordinary case, not the exception: the service is AddScoped, so every
        // request builds one with an empty cache. It has to find the definition already in the database
        // rather than add a second — Cove puts a unique index on CustomFieldDefinition.Key, so a
        // duplicate is not untidy, it is a DbUpdateException that fails the whole apply and loses the
        // tags the reviewer just approved.
        await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["second new tag"],
        });

        var definition = Assert.Single(await db.Set<CustomFieldDefinition>().ToListAsync());
        var values = await db.Set<CustomFieldValue>().ToListAsync();
        Assert.Equal(2, values.Count);
        Assert.All(values, value => Assert.Equal(definition.Id, value.DefinitionId));
    }

    [Fact]
    public async Task Reuses_the_cached_provenance_definition_across_applies_on_one_instance()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var service = new TorrentApplyService(db);

        await service.ApplyAsync(new TorrentApplyRequest { VideoId = videoId, Tags = ["first new tag"] });
        await service.ApplyAsync(new TorrentApplyRequest { VideoId = videoId, Tags = ["second new tag"] });

        // The batch path builds one service for the whole folder, so after the first video every
        // created tag takes the cached branch. Reuse is not directly observable, so what is asserted is
        // what a wrong cached id would break: both values point at the definition that actually exists.
        var definition = Assert.Single(await db.Set<CustomFieldDefinition>().ToListAsync());
        var values = await db.Set<CustomFieldValue>().ToListAsync();
        Assert.Equal(2, values.Count);
        Assert.All(values, value => Assert.Equal(definition.Id, value.DefinitionId));
    }

    [Fact]
    public async Task Keeps_the_cached_provenance_definition_across_a_change_tracker_clear()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);
        var service = new TorrentApplyService(db);

        await service.ApplyAsync(new TorrentApplyRequest { VideoId = videoId, Tags = ["first new tag"] });

        // Exactly what TorrentBatchService does between videos, to stop change detection going
        // quadratic over a whole folder. The cache holds an id rather than a tracked entity, so it has
        // to survive this; caching the entity instead would break here and nowhere else.
        db.ChangeTracker.Clear();

        await service.ApplyAsync(new TorrentApplyRequest { VideoId = videoId, Tags = ["second new tag"] });

        var definition = Assert.Single(await db.Set<CustomFieldDefinition>().ToListAsync());
        var values = await db.Set<CustomFieldValue>().ToListAsync();
        Assert.Equal(2, values.Count);
        Assert.All(values, value => Assert.Equal(definition.Id, value.DefinitionId));
    }

    [Fact]
    public async Task Recovers_when_another_apply_wins_the_race_to_create_the_provenance_definition()
    {
        // The defect is a race: the service is AddScoped, so two applies running at once hold separate
        // instances with separate empty caches, both can see no definition, and both insert. What the
        // fixture reproduces is the window rather than the concurrency — the rival row is written on
        // this connection, inside the apply's own transaction, for the reason CreatesTheDefinitionFirst
        // sets out.
        await using var loser = CreateContext(new CreatesTheDefinitionFirst());
        var firstVideoId = await SeedVideoAsync(loser);
        var secondVideoId = await SeedVideoAsync(loser);

        var service = new TorrentApplyService(loser);

        // The interceptor fires inside this apply, between the lookup that returned nothing and the
        // insert reaching the database — the exact window, and the only moment it is ever open.
        var result = await service.ApplyAsync(new TorrentApplyRequest
        {
            VideoId = firstVideoId,
            Tags = ["first new tag"],
        });

        // Losing the race must cost nothing. Before the fix this threw DbUpdateException out of
        // ApplyTagsAsync and took the whole apply with it, discarding every tag the reviewer approved
        // over a row that by then existed.
        Assert.Equal(1, result!.TagsAdded);
        Assert.Equal(1, result.TagsCreated);

        // And the context has to still be usable. A failed insert left `Added` would be retried by
        // every later SaveChanges on this context, so in the batch path one collision on the first
        // video would poison the rest of the folder.
        var second = await service.ApplyAsync(new TorrentApplyRequest
        {
            VideoId = secondVideoId,
            Tags = ["second new tag"],
        });

        Assert.Equal(1, second!.TagsCreated);

        // One definition — the winner's — and both created tags stamped against it.
        var definition = Assert.Single(await loser.Set<CustomFieldDefinition>().ToListAsync());
        var values = await loser.Set<CustomFieldValue>().ToListAsync();
        Assert.Equal(2, values.Count);
        Assert.All(values, value => Assert.Equal(definition.Id, value.DefinitionId));
    }

    /// <summary>
    /// The apply is one transaction, so a failure late in it leaves nothing behind.
    ///
    /// It used to be two unwrapped saves: the first committed the new Tag rows, the second wrote the
    /// links, the provenance and the scalars. A throw in the second grew the library's vocabulary with
    /// nothing pointing at it and answered the endpoint with a raw 500 — a tag the user never asked to
    /// exist on its own, and no way to tell from the response that it now did.
    /// </summary>
    [Fact]
    public async Task A_failure_after_the_tags_are_created_leaves_none_of_them_behind()
    {
        await using var db = FailsOnTheLinkSave();
        var videoId = await SeedVideoAsync(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new TorrentApplyService(db).ApplyAsync(
            new TorrentApplyRequest { VideoId = videoId, Tags = ["a tag the library does not have"] }));

        // Read past the failed attempt's own tracker, which still holds the entities the rollback
        // undid — the question is what is in the database, not what EF was about to put there.
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Tags.ToListAsync());
        Assert.Empty(await db.Set<VideoTag>().ToListAsync());
        Assert.Empty(await db.Set<CustomFieldDefinition>().ToListAsync());
    }

    /// <summary>
    /// Losing the race to create a tag costs the reviewer nothing, and costs the tag nothing either.
    ///
    /// The same window as the provenance definition above, one table over, and newly reachable on Cove
    /// 1.3: names and aliases share a case-insensitive namespace enforced inside SaveChanges, so the
    /// apply that gets there second is refused rather than quietly writing a duplicate row. Uncaught it
    /// now rolls the whole apply back — every tag the reviewer approved lost over one that by then
    /// exists.
    ///
    /// Two tags because the partial case is the interesting one: the save fails as a unit, so the tag
    /// nobody else claimed still has to be created, and the winner's row has to be adopted rather than
    /// counted as ours. What is counted decides what the reviewer is told and which tags carry the
    /// "imported from" stamp — and a tag we did not create must never be relabelled as imported.
    /// </summary>
    [Fact]
    public async Task Adopts_a_tag_another_apply_created_first_and_still_creates_the_rest()
    {
        await using var db = TagArrivesFirst("kissing");
        var videoId = await SeedVideoAsync(db);

        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["kissing", "outdoor"],
        });

        Assert.Equal(2, result!.TagsAdded);
        Assert.Equal(1, result.TagsCreated);

        db.ChangeTracker.Clear();
        // One row per name — the winner's, not a second "kissing" beside it.
        Assert.Equal(2, await db.Tags.CountAsync());
        Assert.Equal(2, await db.Set<VideoTag>().CountAsync(link => link.VideoId == videoId));

        // Stamped on the one this apply really created, and on nothing else.
        var stamped = Assert.Single(await db.Set<CustomFieldValue>().ToListAsync());
        var ours = await db.Tags.SingleAsync(tag => tag.Name == "outdoor");
        Assert.Equal(ours.Id, stamped.EntityId);
    }

    /// <summary>
    /// Stands in for the apply that got there first: on the intercepted context's first save of a
    /// provenance definition, it inserts the same row, so the intercepted insert lands on a unique
    /// index that is already occupied.
    ///
    /// Firing from <c>SavingChangesAsync</c> rather than from the test body is what makes it
    /// deterministic — the window is inside a single service call, and racing two real tasks for it
    /// would be a flake waiting for CI.
    ///
    /// It writes over the intercepted connection, enlisted in the apply's own transaction, and that is
    /// a constraint of the fixture rather than the shape of the real race. This used to be a second
    /// connection onto a shared-cache database, which is the honest reproduction and no longer runs:
    /// an apply is one transaction now and SQLite serialises writers across the whole database,
    /// so a rival on another connection cannot commit while that transaction is open — it waits out the
    /// busy timeout and the test measures a deadlock instead of a recovery. PostgreSQL, which is what
    /// Cove runs, has no such rule: the rival commits on its own, our insert then fails on the unique
    /// index, and the recovery re-query sees the winner because the apply reads at READ COMMITTED.
    /// Either way what is pinned is the same window — the row exists by the time our insert reaches the
    /// index — and the same recovery from it.
    ///
    /// The row is inserted before EF takes its savepoint for the failing save, so rolling back to that
    /// savepoint leaves the winner in place, exactly as a committed rival would be.
    /// </summary>
    private sealed class CreatesTheDefinitionFirst : SaveChangesInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken ct = default)
        {
            var context = eventData.Context;
            var insertingDefinition = context?.ChangeTracker
                .Entries<CustomFieldDefinition>()
                .Any(entry => entry.State == EntityState.Added) ?? false;

            if (!_fired && insertingDefinition && context is not null)
            {
                _fired = true;

                var builder = new DbContextOptionsBuilder<CoveContext>()
                    .UseSqlite(context.Database.GetDbConnection());
                await using var rival = new CoveContext(builder.Options);

                var transaction = context.Database.CurrentTransaction;
                if (transaction is not null)
                    await rival.Database.UseTransactionAsync(transaction.GetDbTransaction(), ct);

                rival.Set<CustomFieldDefinition>().Add(new CustomFieldDefinition
                {
                    Key = TorrentApplyService.SourceFieldKey,
                    Label = "Imported from",
                    Type = CustomFieldTypes.Text,
                    EntityTypes = [CustomFieldEntityTypes.Tag],
                    Filterable = true,
                });
                await rival.SaveChangesAsync(ct);
            }

            return await base.SavingChangesAsync(eventData, result, ct);
        }
    }

    [Fact]
    public async Task Returns_nothing_for_a_video_that_does_not_exist()
    {
        await using var db = CreateContext();
        Assert.Null(await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest { VideoId = 4242 }));
    }

    [Fact]
    public async Task Applies_nothing_when_the_user_selected_nothing()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);

        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest { VideoId = videoId });

        Assert.Equal(0, result!.TagsAdded);
        Assert.Equal(0, result.PerformersAdded);
        Assert.Empty(await db.Tags.ToListAsync());
    }

    // ---------------------------------------------------------------------
    // Performers are linked, never created
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Links_a_performer_by_id_without_going_through_their_name()
    {
        await using var db = CreateContext();
        // A disambiguation is what makes this the interesting case: from Cove 1.3 a name-only request
        // cannot address her at all — `PerformerIdentityKey(name, null)` never equals
        // `PerformerIdentityKey(name, "II")` — so a request written in names would resolve nothing and
        // create a second "Angela Frost" beside her.
        var performer = new Performer { Name = "Angela Frost", Disambiguation = "II" };
        db.Performers.Add(performer);
        await db.SaveChangesAsync();
        var videoId = await SeedVideoAsync(db);

        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Performers = [performer.Id],
        });

        Assert.Equal(1, result!.PerformersAdded);
        Assert.Equal(1, await db.Performers.CountAsync());
        var link = Assert.Single(await db.Set<VideoPerformer>().ToListAsync());
        Assert.Equal(performer.Id, link.PerformerId);
    }

    [Fact]
    public async Task Never_creates_a_performer_for_an_id_the_library_does_not_have()
    {
        await using var db = CreateContext();
        var videoId = await SeedVideoAsync(db);

        // A performer deleted between the review and the apply. Skipped, not invented and not thrown:
        // losing the tags the user just approved over one departed row helps nobody, and there is no
        // name here to invent a row from even if we wanted to.
        var result = await new TorrentApplyService(db).ApplyAsync(new TorrentApplyRequest
        {
            VideoId = videoId,
            Tags = ["kissing"],
            Performers = [4242],
        });

        Assert.Equal(0, result!.PerformersAdded);
        Assert.Equal(1, result.TagsAdded);
        Assert.Empty(await db.Performers.ToListAsync());
        Assert.Empty(await db.Set<VideoPerformer>().ToListAsync());
    }

    // ---------------------------------------------------------------------

    private static async Task<int> SeedVideoAsync(CoveContext db, string? title = "video")
    {
        var video = new Video { Title = title };
        db.Videos.Add(video);
        await db.SaveChangesAsync();
        return video.Id;
    }

    /// <summary>
    /// Fails the save that writes the links — the second of the two the apply used to make, and the one
    /// Cove 1.3 makes likeliest to throw.
    /// </summary>
    private sealed class FailsOnTheLinks(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            ChangeTracker.Entries<VideoTag>().Any(entry => entry.State == EntityState.Added)
                ? throw new InvalidOperationException("the link save failed")
                : base.SaveChangesAsync(cancellationToken);
    }

    private static CoveContext FailsOnTheLinkSave()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new FailsOnTheLinks(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    /// <summary>
    /// Stands in for the apply that got to one of the tags first: on the save that would insert them,
    /// and just before it, the named tag appears.
    ///
    /// A context override rather than an interceptor, and that is the whole trick. An interceptor runs
    /// *inside* <c>CoveContext.SaveChangesAsync</c>, which has already taken the tag-namespace write
    /// lock — process-wide on SQLite, since only PostgreSQL gets the advisory-lock path — so a rival
    /// writing a tag from there waits on a semaphore its own caller holds and the test hangs. An
    /// override runs before that call, with the lock free, and lands the row in exactly the window the
    /// recovery is about: after the resolve decided the name was missing, before the insert reaches the
    /// namespace.
    ///
    /// It writes over this connection, enlisted in the apply's transaction, for the same reason
    /// <see cref="CreatesTheDefinitionFirst"/> does.
    /// </summary>
    private sealed class ATagArrivesFirst(DbContextOptions<CoveContext> options, string name) : CoveContext(options)
    {
        private bool _fired;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_fired && ChangeTracker.Entries<Tag>().Any(entry => entry.State == EntityState.Added))
            {
                _fired = true;

                var builder = new DbContextOptionsBuilder<CoveContext>()
                    .UseSqlite(Database.GetDbConnection());
                await using var rival = new CoveContext(builder.Options);

                var transaction = Database.CurrentTransaction;
                if (transaction is not null)
                    await rival.Database.UseTransactionAsync(transaction.GetDbTransaction(), cancellationToken);

                rival.Tags.Add(new Tag { Name = name });
                await rival.SaveChangesAsync(cancellationToken);
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    private static CoveContext TagArrivesFirst(string name)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new ATagArrivesFirst(options, name);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private static CoveContext CreateContext(SaveChangesInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:");

        if (interceptor is not null)
            builder.AddInterceptors(interceptor);

        var context = new CoveContext(builder.Options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }
}
