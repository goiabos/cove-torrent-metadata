using System.Globalization;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.TorrentMetadata;

/// <summary>A tag or performer the torrent proposes, and whether the library already knows it.</summary>
/// <param name="Name">The value that would be applied.</param>
/// <param name="Source">The original dotted tag-list entry, kept so it can be stored as an alias.</param>
/// <param name="MatchesExisting">True when this resolves to an existing row rather than creating one.</param>
/// <param name="AlreadyApplied">True when the video already carries it, so review can hide it as a no-op.</param>
public sealed record ProposedRelation(string Name, string? Source, bool MatchesExisting, bool AlreadyApplied);

/// <summary>A library performer the torrent names.</summary>
/// <param name="Id">The row an apply would link. Performers are addressed by id, never by name.</param>
/// <param name="Name">Their canonical name — what the library calls them, whatever the torrent wrote.</param>
/// <param name="Source">
/// The tag-list entry that found them, when an alias did and their name never appeared. Null otherwise,
/// which is the ordinary case: the dialog shows this beside the name so a match the reviewer cannot
/// account for explains itself, and a match they can is left to read as one line.
/// </param>
/// <param name="AlreadyApplied">True when the video already carries them, so review can hide it as a no-op.</param>
public sealed record ProposedPerformer(int Id, string Name, string? Source, bool AlreadyApplied);

/// <summary>A studio the reviewer may choose, and the domain that found it.</summary>
/// <param name="Name">What the library calls it — the library names its own studios.</param>
/// <param name="Source">The tracker's spelling of the site tag, e.g. <c>lanternbay.com</c>. Shown beside
/// the name because network and imprint differ by domain rather than by how the library spells them.</param>
public sealed record ProposedStudio(string Name, string Source);

/// <summary>What a torrent offers for one video, for the user to review before anything is written.</summary>
public sealed record TorrentMatchProposal
{
    public required int VideoId { get; init; }
    public required string TorrentName { get; init; }

    /// <summary>The torrent's video file this proposal came from; lets a caller re-request the same one.</summary>
    public required string FileName { get; init; }

    public required string MatchedOn { get; init; }

    /// <summary>
    /// How many videos share this torrent's metadata. Above one means pack or siterip metadata: the tag
    /// list is the union across every scene it contains, so most of it is wrong for any single video.
    /// Review should default these to nothing-selected rather than everything-selected.
    /// </summary>
    public required int FanOut { get; init; }

    /// <summary>
    /// How many entries the torrent's tag list holds, raw — before classification, the performer split
    /// or anything the library knows.
    ///
    /// Sent so the apply can record it as the baseline for "has this torrent changed since".
    /// Raw rather than the count of tags actually offered below, because the offered count moves when
    /// the *library* gains a performer or an alias, and a library edit must not read as a torrent edit.
    /// </summary>
    public required int TorrentTagCount { get; init; }

    public string? Title { get; init; }
    public string? Date { get; init; }

    /// <summary>The one studio the torrent's site tags resolve to, or null. Never a name the library lacks.</summary>
    public string? StudioName { get; init; }

    /// <summary>
    /// The two studios the reviewer may choose between, or empty.
    ///
    /// Populated only when exactly two resolve, which is the cap the design study settled: a shortlist
    /// drawn from five would have to order it, and tag order is the defect this rule exists to kill. Three or
    /// more send <see cref="StudioMatchCount"/> and no options, because naming studios the window will
    /// not offer is noise.
    ///
    /// <see cref="StudioName"/> is null whenever this is non-empty — the two states are alternatives,
    /// and a field cannot be both proposed and asked about.
    /// </summary>
    public IReadOnlyList<ProposedStudio> StudioChoices { get; init; } = [];

    /// <summary>
    /// How many distinct studios in the library the torrent's site tags matched.
    ///
    /// Sent so the window can say *why* nothing is proposed when several matched, which is the one thing
    /// the reviewer cannot otherwise tell from silence: a torrent naming no studio and a torrent naming
    /// five look identical without it.
    /// </summary>
    public int StudioMatchCount { get; init; }
    public string? CoverUrl { get; init; }

    /// <summary>
    /// Whether <see cref="CoverUrl"/>'s host is one covers may be fetched from.
    ///
    /// Answered here rather than left to the apply result so review can say so before the user ticks
    /// the box and waits. With the allowlist shipping empty this is false on every fresh
    /// install, and finding that out only afterwards is the difference between a setting nobody has
    /// filled in and a feature that appears not to work.
    /// </summary>
    public bool CoverHostAllowed { get; init; }

    public string? Url { get; init; }
    public string? TorrentId { get; init; }

    /// <summary>
    /// Whether the library video has artwork of its own.
    ///
    /// Two things in review turn on it, and both used to be answered by letting a request fail: the
    /// dialog opens the cover comparison unprompted when there is nothing to compare against, and it
    /// leaves the thumbnail slot empty rather than asking for an image that 404s.
    /// </summary>
    public bool VideoHasImage { get; init; }

    // The video's current values, so review can show proposed against existing rather than asking the
    // user to accept a field blind. A null here means the field is empty and would simply be filled.
    public string? CurrentTitle { get; init; }
    public string? CurrentDate { get; init; }
    public string? CurrentStudioName { get; init; }

    /// <summary>URLs already on the video. The torrent's URL is added alongside, never replacing these.</summary>
    public IReadOnlyList<string> CurrentUrls { get; init; } = [];
    public IReadOnlyList<ProposedRelation> Tags { get; init; } = [];
    /// <summary>
    /// Performers the tag list named and the library knows, each flagged with whether this video
    /// already carries it.
    ///
    /// There is no second list of "candidates" beside this one. There was — the same filter, applied
    /// server-side and sent again — and nothing rendered it, because a client that has
    /// <see cref="ProposedPerformer.AlreadyApplied"/> can compute it in a `filter`. One rule
    /// stated twice is one rule that can disagree with itself.
    /// </summary>
    public IReadOnlyList<ProposedPerformer> Performers { get; init; } = [];

    /// <summary>The active naming style, so review can show and change it in place.</summary>
    public string TagNameStyle { get; init; } = "titlecase";
}

/// <summary>Why a match attempt produced no proposal, or that it produced one.</summary>
public enum TorrentMatchStatus
{
    /// <summary>A torrent was found and <see cref="TorrentMatchOutcome.Proposal"/> holds it.</summary>
    Matched,

    /// <summary>The library has no video with that id. Nothing about the torrent folder is implied.</summary>
    VideoNotFound,

    /// <summary>The video is there; no indexed torrent describes any file of it.</summary>
    NoTorrentMatched,
}

/// <summary>
/// The result of a match attempt: the proposal, or which of the two reasons there is not one.
///
/// A bare nullable proposal could not tell those reasons apart, so both surfaced as "no indexed
/// torrent describes any file of this video" — sending someone whose video had been deleted in
/// another tab off to rescan a torrent folder that was never the problem.
///
/// It carries a status rather than a message: what to *say* about a missing video is the endpoint's
/// business, and a service that returns user-facing prose acquires a second audience it cannot see.
/// The constructor is private so the three states are the only ones expressible — in particular there
/// is no way to build a <see cref="TorrentMatchStatus.Matched"/> outcome with no proposal in it.
/// </summary>
public sealed record TorrentMatchOutcome
{
    private TorrentMatchOutcome(TorrentMatchStatus status, TorrentMatchProposal? proposal)
    {
        Status = status;
        Proposal = proposal;
    }

    public TorrentMatchStatus Status { get; }

    /// <summary>The proposal, and non-null exactly when <see cref="Status"/> is Matched.</summary>
    public TorrentMatchProposal? Proposal { get; }

    public static TorrentMatchOutcome Matched(TorrentMatchProposal proposal) =>
        new(TorrentMatchStatus.Matched, proposal);

    public static TorrentMatchOutcome VideoNotFound { get; } = new(TorrentMatchStatus.VideoNotFound, null);

    public static TorrentMatchOutcome NoTorrentMatched { get; } = new(TorrentMatchStatus.NoTorrentMatched, null);
}

/// <summary>
/// Turns an indexed torrent into a reviewable proposal for one video.
///
/// Nothing here writes: the extension's contract with the user is that a torrent is a *suggestion*
/// compared against whatever metadata the video already has, so every field is resolved, labelled as
/// "matches existing" or "will create", and handed back for a decision.
///
/// Resolution goes through <see cref="RelationNameResolver"/> — the same helper Cove's own scrape-apply
/// path uses — so the prediction shown in review cannot drift from what a later apply would actually do.
/// It matches on primary name or alias, case-insensitively, with primary names winning.
/// </summary>
public sealed class TorrentMatchService(
    CoveContext db,
    TorrentIndex index,
    TorrentMetadataSettings settings,
    CoverHostAllowlist? coverHosts = null)
{
    /// <summary>
    /// Builds a proposal for a video.
    ///
    /// When <paramref name="forcedTorrentName"/> is given, that torrent is used regardless of whether its
    /// files match — the caller handed us a specific .torrent for this video, and that intent outranks
    /// anything the folder happens to contain. This is what lets someone attach the individual-scene
    /// torrent to a video they pulled out of a megapack, where the pack would otherwise win.
    ///
    /// <paramref name="forcedFileName"/> names a file inside it and is honoured as given. Without one the
    /// file is chosen by size, the same way the automatic path chooses a torrent: a caller who dropped a
    /// pack knows which torrent it handed over but not which of its scenes this video is.
    /// </summary>
    public async Task<TorrentMatchOutcome> MatchAsync(
        int videoId,
        string? forcedTorrentName = null,
        string? forcedFileName = null,
        CancellationToken ct = default)
    {
        var video = await db.Videos
            .AsNoTracking()
            .Where(candidate => candidate.Id == videoId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Title,
                candidate.Date,
                HasImage = candidate.ImageBlobId != null,
                StudioName = candidate.Studio != null ? candidate.Studio.Name : null,
                Urls = candidate.Urls.Select(url => url.Url).ToList(),
                Files = candidate.Files.Select(file => new { file.Size, file.Path }).ToList(),
                TagNames = candidate.VideoTags.Select(link => link.Tag!.Name).ToList(),
                // Ids, not names: what "this video already has them" means has to be the same question
                // the apply answers, and the apply links by id. It is also the denormalised
                // array Cove maintains on save, so no join is paid for it.
                PerformerIds = candidate.PerformerIds,
            })
            .FirstOrDefaultAsync(ct);

        if (video is null)
            return TorrentMatchOutcome.VideoNotFound;

        TorrentIndexEntry? entry = null;
        var matchedOn = string.Empty;

        if (!string.IsNullOrEmpty(forcedTorrentName))
        {
            var sizes = video.Files.Select(file => file.Size).ToHashSet();

            // Ordered by the shared preference so the choice below is both reproducible and the same one
            // the index and the batch overview would make. `All()` flattens dictionaries whose
            // enumeration order .NET does not define, which is not something a user-visible choice
            // should rest on — and sorting on the basename alone was no ordering at all once a file name
            // was forced, since every candidate then has that basename by construction.
            //
            // The tiebreak operates only *within* the torrent the caller named. Forcing still outranks
            // the automatic lookup entirely; this decides which of two entries answering to that same
            // name is meant, and prefers the single scene over the pack exactly as `TorrentIndex.Find`
            // does.
            var candidates = index.All()
                .Where(candidate =>
                    candidate.Torrent.Name == forcedTorrentName
                    && (forcedFileName is null || candidate.Video.Basename == forcedFileName))
                .Order(TorrentEntryPreference.Instance)
                .ToList();

            // Prefer the file this video actually has the bytes of. The search never leaves the forced
            // torrent, so this cannot reintroduce the pack-wins problem the forcing exists to avoid; it
            // only stops a pack's first-listed scene from being handed to a video from its middle.
            entry = candidates.FirstOrDefault(candidate => sizes.Contains(candidate.Video.Length))
                ?? candidates.FirstOrDefault();

            if (entry is not null)
            {
                // Say plainly whether the chosen torrent actually describes one of this video's files, so
                // an intentional override never looks like a verified match.
                matchedOn = sizes.Contains(entry.Video.Length) ? "file size" : "your selection";
            }
        }
        else
        {
            // A Cove video can have several files (different encodes, a moved copy); any one of them
            // being the file a torrent describes is enough to identify the release.
            foreach (var file in video.Files)
            {
                entry = index.Find(file.Size, file.Path);
                if (entry is not null)
                {
                    matchedOn = entry.Video.Length == file.Size ? "file size" : "file name";
                    break;
                }
            }
        }

        if (entry is null)
            return TorrentMatchOutcome.NoTorrentMatched;

        return TorrentMatchOutcome.Matched(await BuildProposalAsync(
            videoId,
            entry,
            matchedOn,
            video.TagNames,
            video.PerformerIds,
            new CurrentValues(
                video.Title,
                video.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                video.StudioName,
                video.Urls,
                video.HasImage),
            ct));
    }

    private async Task<TorrentMatchProposal> BuildProposalAsync(
        int videoId,
        TorrentIndexEntry entry,
        string matchedOn,
        List<string> existingTagNames,
        int[] existingPerformerIds,
        CurrentValues current,
        CancellationToken ct)
    {
        var torrent = entry.Torrent;
        var classified = TagClassifier.ClassifyAll(torrent.TagList);

        // Matched against every performer in the library, not just those already on this video: a pack's
        // tag list carries every performer across all its scenes, and the narrower set would leave the
        // rest sitting in the tag list looking like content.
        var knownPerformers = await LoadPerformerVocabularyAsync(ct);
        var split = PerformerMatcher.Split(classified, knownPerformers);

        // Content and Configuration only. `SourceQuality` is classified so it can be *recognised* and
        // dropped — `docs/DESIGN-DECISIONS.md` §"Technical tags are dropped" — and importing it
        // contradicted that in code while both docs said otherwise, with no test either way.
        // Ruled 2026-08-20: the doc is right and the code was wrong.
        var tagEntries = split.Tags
            .Where(tag => tag.Kind is TorrentTagKind.Content or TorrentTagKind.Configuration)
            .ToList();

        // Both spellings, because a tag can be stored under either. The classifier's `Value` is the
        // normalised form (`big red barn`) and `Source` is what the tracker wrote
        // (`big.red.barn`), and which one the library holds depends on how the tag got there: the
        // dotted naming style names a created tag by its source, and the aliases this extension seeds
        // are written in source form. Asking only for `Value` meant a tag this extension had created
        // itself came back unresolved — reported as "would be created" in review, and skipped
        // outright by a bulk apply with "create new tags" off.
        var resolvedTags = await RelationNameResolver.ResolveTagsAsync(
            db, [.. tagEntries.SelectMany(tag => new[] { tag.Value, tag.Source })], ct);

        // Only ever a studio the library already has, and only when the candidates agree on exactly one
        // of them. A studio that does not exist is not proposed at all, rather than offered as a
        // pre-ticked field the apply would then silently decline to link.
        //
        // Where they agree on exactly two, the reviewer is offered the choice instead — the
        // library holds both, both are correct names, and only the person looking at the video knows
        // which. Three or more is a count and nothing else.
        var studioCandidates = TagClassifier.ExtractStudioCandidates(split.Tags);
        var studioMatch = studioCandidates.Count == 0
            ? new StudioMatchResult()
            : StudioMatcher.Resolve(studioCandidates, await LoadStudioVocabularyAsync(ct));

        // The tracker's spelling of each domain, which the matcher does not carry: it reduces both sides
        // to a key and has no use for the original. The chooser shows it, because network and imprint
        // differ by domain and not by how the library spells them.
        var domainByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in split.Tags.Where(tag => tag.Kind == TorrentTagKind.SiteOrStudio))
            domainByKey.TryAdd(StudioMatcher.NormalizeKey(tag.Value), tag.Source);

        var existingTags = existingTagNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingPerformers = existingPerformerIds.ToHashSet();

        return new TorrentMatchProposal
        {
            VideoId = videoId,
            TorrentName = torrent.Name,
            FileName = entry.Video.Basename,
            MatchedOn = matchedOn,
            FanOut = entry.FanOut,
            TorrentTagCount = torrent.TagList.Count,
            Title = torrent.Title,
            // Invariant, because the format string alone is not a format. `ToString("yyyy-MM-dd")`
            // renders in the *culture's* calendar, so under th-TH a 2018 date leaves here as 2561 —
            // and the apply, reading the ambient culture too, then declines to parse it back.
            Date = TagClassifier.ExtractDate(split.Tags)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            StudioName = studioMatch.Resolved?.Name,
            StudioChoices = [.. studioMatch.Choices.Select(option =>
                new ProposedStudio(option.Studio.Name, domainByKey.GetValueOrDefault(option.Key, option.Key)))],
            StudioMatchCount = studioMatch.MatchCount,
            CoverUrl = torrent.CoverUrl,
            // Optional dependency, and null answers false — the same direction the apply path fails
            // when it is unwired, so review never promises a cover the apply would then refuse.
            CoverHostAllowed = torrent.CoverUrl is { } coverUrl
                && Uri.TryCreate(coverUrl, UriKind.Absolute, out var coverUri)
                && coverHosts?.Allows(coverUri) == true,
            Url = torrent.Comment,
            TorrentId = torrent.TorrentId,
            VideoHasImage = current.HasImage,
            CurrentTitle = current.Title,
            CurrentDate = current.Date,
            CurrentStudioName = current.StudioName,
            CurrentUrls = current.Urls,
            TagNameStyle = TagNameStyler.Serialize(settings.TagNameStyle),
            // A tag that resolves keeps the library's own spelling — the library is the authority on how
            // its tags are named. The configured style only decides how a tag that does not exist yet
            // gets spelled when it is created.
            Tags = [.. tagEntries
                .Select(tag =>
                {
                    // Normalised form first: it is the spelling the library is most likely to hold, and
                    // a source-form hit is the fallback for the tags this extension itself named.
                    var resolved = resolvedTags.TryGetValue(tag.Value, out var byValue) ? byValue.Name
                        : resolvedTags.TryGetValue(tag.Source, out var bySource) ? bySource.Name
                        : null;
                    var name = resolved ?? TagNameStyler.Apply(settings.TagNameStyle, tag.Value, tag.Source);
                    return new ProposedRelation(name, tag.Source, resolved is not null, existingTags.Contains(name));
                })
                // Two entries can arrive at one name — most often because the library holds both dotted
                // spellings as aliases of the same tag, which is what our own alias seeding builds up
                // over time. Left in, they are two rows sharing a React key and a checkbox, and a header
                // count larger than what would be applied, since the apply path folds them anyway
                //. Case-insensitive because that is the comparer the apply uses, so two rows the
                // reviewer sees as distinct would still be one tag.
                //
                // First source wins. `TorrentApplyRequest.TagSources` is one source per name at both
                // ends of the wire, so carrying the rest would be a contract change rather than a
                // different projection here.
                .DistinctBy(relation => relation.Name, StringComparer.OrdinalIgnoreCase)],
            // No "matches existing" flag, because there is no other kind. The split only yields
            // performers the library holds, and the apply can only link one — so a performer here is
            // an existing row by construction rather than by prediction.
            Performers = [.. split.Performers.Select(performer => new ProposedPerformer(
                performer.Id,
                performer.Name,
                performer.MatchedVia,
                existingPerformers.Contains(performer.Id)))],
        };
    }

    /// <summary>
    /// Every spelling a library performer can be found under, each carrying the performer it belongs to.
    ///
    /// Aliases are still indexed — dropping them would push the names they match back into the tag list
    /// as content, which is the junk the classifier exists to keep out. What changed is that an alias
    /// now resolves to a <em>row</em> rather than to a string that something downstream would have had
    /// to resolve again — and on the host this targets, could not resolve at all.
    /// </summary>
    private async Task<IReadOnlyList<PerformerVocabularyEntry>> LoadPerformerVocabularyAsync(CancellationToken ct)
    {
        var names = await db.Performers.AsNoTracking()
            .Select(performer => new { performer.Id, performer.Name })
            .ToListAsync(ct);
        // Read from the alias table rather than SelectMany-ing the navigation: projecting the
        // performer's own columns alongside the alias turns that into a correlated subquery, which
        // needs SQL APPLY and so translates on Postgres and throws on the SQLite the tests run on.
        var aliases = await db.Set<PerformerAlias>().AsNoTracking()
            .Select(alias => new { Id = alias.PerformerId, Name = alias.Performer!.Name, alias.Alias })
            .ToListAsync(ct);

        return
        [
            .. names.Select(row => new PerformerVocabularyEntry(row.Id, row.Name, row.Name)),
            .. aliases.Select(row => new PerformerVocabularyEntry(row.Id, row.Name, row.Alias)),
        ];
    }

    /// <summary>
    /// The library's studios, for the match to reduce and compare in memory.
    ///
    /// The whole table, and that is a deliberate reversal. It used to be narrowed by the query to the
    /// candidate names exactly — cheap, and impossible to keep once the comparison became a .NET string
    /// reduction: <c>lower(name)</c> is portable, "strip every non-alphanumeric" is not, and a
    /// provider that spelled it differently would make the review and the apply disagree about what a
    /// name means. Cove 1.3 reaches the same conclusion about its own namespace keys and for the same
    /// reason.
    ///
    /// Two columns and no tracking, so it is the tag vocabulary's shape rather than a new cost: that
    /// one already loads every tag and every alias, against a studio table which is smaller than either
    /// in every library measured.
    /// </summary>
    private async Task<IReadOnlyList<StudioCandidate>> LoadStudioVocabularyAsync(CancellationToken ct) =>
        await db.Studios.AsNoTracking()
            // Ordered so a library holding one studio twice resolves the same way on every read, rather
            // than however the provider happened to return it. It is refused either way, but a refusal
            // that flickers is worse than one that does not.
            .OrderBy(studio => studio.Id)
            .Select(studio => new StudioCandidate(studio.Id, studio.Name))
            .ToListAsync(ct);
}

/// <summary>The video's existing field values, carried into the proposal so review can compare.</summary>
internal sealed record CurrentValues(
    string? Title,
    string? Date,
    string? StudioName,
    IReadOnlyList<string> Urls,
    bool HasImage);
