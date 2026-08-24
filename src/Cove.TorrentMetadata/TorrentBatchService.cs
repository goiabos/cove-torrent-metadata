using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.TorrentMetadata;

/// <summary>One torrent's video file, and the library video it identifies (if any).</summary>
public sealed record BatchRow
{
    public required string TorrentName { get; init; }
    public required string FileName { get; init; }
    public string? TorrentId { get; init; }

    /// <summary>Above one means pack metadata shared across scenes; excluded from bulk apply by default.</summary>
    public required int FanOut { get; init; }

    /// <summary>
    /// "matched", "applied" or "updated" — an unmatched file has no row, only a count.
    ///
    /// "updated" is an applied row whose torrent has since gained tags this video does not carry. A
    /// tracker keeps a torrent's id when its tags are edited, so re-downloading a re-tagged .torrent
    /// produces the same <see cref="TorrentId"/> and the row read "applied" — which meant bulk apply
    /// skipped it and the page's "Hide applied" filter, on by default, kept it off screen entirely
    ///.
    ///
    /// It is a third status rather than a flag beside "applied" for a mechanical reason: everything
    /// that acts on a row tests this field against an exact string, so a new value is excluded from
    /// both apply paths and survives the hide filter without any of them being touched.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// The library video this row's file matches. Never absent: a torrent file that matches nothing
    /// is counted in <see cref="BatchOverview.Unmatched"/> and gets no row at all, so the type says so
    /// rather than leaving every consumer to rediscover it.
    /// </summary>
    public required int VideoId { get; init; }

    public string? VideoTitle { get; init; }

    /// <summary>
    /// Whether the library video has artwork of its own.
    ///
    /// Answered here so the page can leave the slot empty instead of asking for an image that is not
    /// there: `/api/videos/{id}/image` 404s for a video with no cover, once per row, and the browser
    /// logs every one of them.
    /// </summary>
    public bool VideoHasImage { get; init; }

    /// <summary>Tags the video already carries, so a bare library is visible at a glance.</summary>
    public int VideoTagCount { get; init; }

    /// <summary>
    /// Tags this torrent would add to <em>this video</em> — the number the reviewer is deciding about.
    ///
    /// Counted against the video's own tags, not against the library vocabulary. It used to be the
    /// latter, which meant a video already carrying every tag in its torrent still read "would add 43",
    /// and applying did not move it: the created tags merely crossed from one bucket to the other and
    /// the total was identical before and after, so a correctly refreshed row looked stale.
    /// </summary>
    public int TagsToAdd { get; init; }

    /// <summary>
    /// How many of <see cref="TagsToAdd"/> do not exist in the library at all, and would be created.
    ///
    /// A subset, not a second bucket. It is what separates a row that only links existing tags — what
    /// a bulk apply does by default — from one that grows the vocabulary.
    /// </summary>
    public int TagsToCreate { get; init; }

    /// <summary>
    /// Performers this torrent would add to <em>this video</em>.
    ///
    /// Counted against the video's own performers, not against the torrent's list. It used to be the
    /// latter under the name `PerformerCount`, which meant a video already carrying every performer in
    /// its torrent still reported the full count and applying did not move it — the same untruth already
    /// fixed for tags, left standing beside it because it was out of that scope.
    ///
    /// By id, because that is what identifies a performer and what an apply links.
    /// </summary>
    public int PerformersToAdd { get; init; }

    /// <summary>The torrent's own cover, so the page can show it beside the library video's.</summary>
    public string? TorrentCoverUrl { get; init; }

    /// <summary>
    /// Whether that cover's host is one the operator has named.
    ///
    /// The page needs it because the thumbnail goes through the extension's own proxy now, and
    /// the proxy refuses a host that is not on the list — so a row whose host is unconfigured must
    /// render no image rather than a broken one. Computed exactly as
    /// <c>TorrentMatchProposal.CoverHostAllowed</c> is, so the grid and the dialog cannot disagree
    /// about the same torrent.
    /// </summary>
    public bool TorrentCoverAllowed { get; init; }
}

/// <summary>
/// One row of the batch overview, as what identifies it: the library video, and which torrent describes
/// it.
///
/// A row is a video file described by a specific torrent, and two torrents can describe the same file
/// — 2.32% of corpus sizes are shared, 20 files in the real library. That is why an apply cannot be
/// addressed by video id alone: naming the video names every row the video appears in, so ticking one of
/// two rows applied both, and the request had no way to say which was meant.
///
/// It was <c>(Torrent, File)</c> — the torrent's name and the file's basename — and that pair does not
/// identify a row either, in the *other* direction. Neither half is unique: a pack holding
/// <c>Disc1/01.mp4</c> beside <c>Disc2/01.mp4</c> yields two rows sharing both, and 53 basename buckets
/// were measured over 3,202 corpus torrents. Those two rows carry different <see cref="BatchRow.VideoId"/>,
/// so one tick applied two videos. The video id is what separates them, and it is also the half
/// this key was missing entirely.
///
/// <see cref="TorrentName"/> rides along because <see cref="TorrentId"/> is absent on a torrent whose
/// comment carries no recognisable URL, and a row still has to be addressable then. See
/// <c>TorrentBatchService.RowKey</c> for how the two are folded — the id wins wherever it exists, so two
/// copies of one tracker id are one row rather than two identical ones.
/// </summary>
public sealed record BatchRowRef
{
    /// <summary>The library video this row applies to. Half of the identity, and the half that was missing.</summary>
    public required int VideoId { get; init; }

    /// <summary>The tracker's torrent id, or null when the file carries no recognisable comment URL.</summary>
    public string? TorrentId { get; init; }

    /// <summary>The torrent's name, which identifies it only where <see cref="TorrentId"/> does not.</summary>
    public required string TorrentName { get; init; }
}

public sealed record BatchApplyRequest
{
    /// <summary>
    /// The rows to apply. **Empty means every eligible row** — the sweep, which is what the page's
    /// *Apply to N* runs and what most tests exercise.
    ///
    /// A row named here is applied whatever <see cref="IncludePacks"/> says, because naming it *is*
    /// the explicit inclusion that flag exists to ask for. The flag still guards the sweep, which
    /// names nothing and so cannot have consented to anything.
    /// </summary>
    public List<BatchRowRef> Rows { get; init; } = [];

    /// <summary>When false (the default) only tags that already exist are applied and none are created.</summary>
    public bool CreateNewTags { get; init; }

    /// <summary>
    /// Packs are skipped unless this is set. Their tag list is the union across every scene in the
    /// torrent, so applying it wholesale would tag each video with the others' content.
    /// </summary>
    public bool IncludePacks { get; init; }

    /// <summary>Fetches each torrent's cover and replaces the video's. Off by default: it hits a
    /// third-party host once per video and overwrites artwork the user may have curated.</summary>
    public bool ImportCovers { get; init; }
}

/// <summary>
/// The batch overview: the rows worth reviewing, and counts for everything else.
///
/// Only matched rows are carried. A torrent describing a file the library does not have has nothing
/// to review on it — no video, no tags, no proposal — and a real torrent folder is overwhelmingly
/// made of those: 3218 bookmarked torrents index 139,141 video files, of which 715 match. Returning
/// a row each made a 45 MB response that was 99.5% padding. <see cref="Unmatched"/> is what those
/// rows were actually communicating.
/// </summary>
public sealed record BatchOverview
{
    public required IReadOnlyList<BatchRow> Rows { get; init; }

    /// <summary>Indexed video files that no library file matches.</summary>
    public required int Unmatched { get; init; }

    /// <summary>
    /// Videos the size match misses but the *name* match would find — a file you hold under the same
    /// name at a different size, which is a re-encode or a different release of the same scene.
    ///
    /// Not a row, and never eligible for anything. It is the count that separates the two answers
    /// hiding inside <see cref="Unmatched"/>: "you never downloaded this", which is almost all of it
    /// and which nothing can be done about, from "you have it, and the metadata is one click away on
    /// the video itself". `TorrentIndex.Find` falls back to the basename when size finds
    /// nothing, so those videos already match from the dialog and report `matched on file name`; this
    /// side does not do that fallback, which is why the batch page could not see them at all.
    ///
    /// **Per video, not per video file.** A video holding a size match on one file and a name match on
    /// another is matched, because <c>TorrentMatchService</c> takes the first hit across its files.
    /// Counting files would claim it as a near-miss. It is also the only count here not in torrent-file
    /// units, which is why the page has to name its unit rather than appending it to the row of
    /// figures that are.
    /// </summary>
    public required int VideosMatchableByName { get; init; }

    /// <summary>
    /// Video files across every indexed torrent — the denominator for <see cref="Rows"/> and
    /// <see cref="Unmatched"/>, which are both per video file rather than per torrent.
    /// </summary>
    public required int IndexedFiles { get; init; }

    /// <summary>
    /// Torrents those files came from. Reported separately because the two differ by orders of
    /// magnitude once packs are involved: 3218 real torrents index 139,141 video files.
    /// </summary>
    public required int Torrents { get; init; }
}

public sealed record BatchApplyResult
{
    public int VideosTouched { get; init; }
    public int TagsAdded { get; init; }
    public int TagsCreated { get; init; }
    public int PerformersAdded { get; init; }
    public int AliasesSeeded { get; init; }
    public int CoversImported { get; init; }

    // There is deliberately no count of rows declined for being packs. The page filters packs
    // out of its own request before sending one, so the honest per-request answer is zero on every
    // call it makes — and on the calls where it would not be, it would mislead: a video described by
    // both a single-scene torrent and a pack (20 of them in the real library) is applied from the
    // single-scene row while its pack row is declined, so a count would report a decline for a video
    // that was written. The filter itself stays; only the counter is gone.

    /// <summary>
    /// Videos whose cover was requested and not imported. The counterpart to
    /// <see cref="CoversImported"/>: without it, "0 covers" on a 468-video run says nothing about
    /// whether the covers were unreachable or the allowlist was simply never configured.
    /// </summary>
    public int CoversSkipped { get; init; }

    /// <summary>
    /// The first reason a cover was skipped, as a sample rather than a list.
    ///
    /// One line, not one per video: a bulk run against an unconfigured allowlist skips every cover
    /// for the same reason, and 468 copies of it is not more information than one.
    /// </summary>
    public string? CoverSkipReason { get; init; }

    /// <summary>
    /// Rows whose apply threw and were skipped.
    ///
    /// A floor rather than a clean count of rows that wrote nothing: <c>ApplyTagsAsync</c> saves created
    /// tags before it saves aliases and links, so a row that throws on the second save has already
    /// written the first. It says what did not finish, never that nothing happened — which is why the
    /// wording that reports it says "failed" rather than "skipped".
    /// </summary>
    public int RowsFailed { get; init; }

    /// <summary>
    /// The first failure, as a sample rather than a list — the rule <see cref="CoverSkipReason"/>
    /// follows, for the same reason. A systemic fault fails every row identically.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// The run stopped itself after <see cref="MaxConsecutiveFailures"/> rows failed in a row, rather
    /// than working through the rest.
    ///
    /// It has to be on the wire rather than stay a server-side detail: the page applies a large
    /// selection in chunks, so a breaker the caller cannot see is a breaker that trips once per chunk
    /// and lets the run continue anyway — which is most of what it exists to prevent.
    /// </summary>
    public bool StoppedEarly { get; init; }
}

/// <summary>
/// Builds the batch overview and runs bulk applies.
///
/// The overview resolves every indexed torrent against the whole library in a handful of queries: the
/// tag and performer vocabularies are pulled once into sets and matched in memory, rather than issuing
/// per-torrent lookups. With a few hundred torrents against a library of this size that is the
/// difference between one page load and thousands of round trips.
///
/// "Applied" is derived from the <c>VideoRemoteId</c> written on apply, not from moving or deleting the
/// .torrent. That keeps files intact (they may still be seeding), survives renames, and — because a
/// pack maps to many videos — can express partial completion, which a file-level flag cannot.
/// </summary>
public sealed class TorrentBatchService(
    CoveContext db,
    TorrentIndex index,
    TorrentMetadataSettings settings,
    IBlobService? blobs = null,
    CoverHostAllowlist? coverHosts = null,
    CoverResolver? covers = null,
    AppliedTorrentBaseline? baseline = null,
    BlobReferenceTransactionCoordinator? blobTransactions = null)
{
    /// <summary>Shared with the apply, which owns the warning about never renaming it.</summary>
    private const string RemoteIdEndpoint = TorrentApplyService.RemoteIdEndpoint;

    /// <summary>
    /// What identifies a row: the library video, and which torrent describes it.
    ///
    /// **One definition, because three places ask this question.** <see cref="LoadAsync"/> builds rows by
    /// it, <see cref="ApplyAsync"/> tests requested membership by it and looks the winning entry up by it,
    /// and <c>ui/src/queue.ts</c>'s <c>rowKey</c> mirrors it in the browser. They used to answer it as
    /// <c>(Torrent.Name, Video.Basename)</c>, which is not unique in either direction, and this codebase
    /// has a record of what happens when sites answering one question drift apart.
    ///
    /// **The id wins wherever it exists, and that is the load-bearing half.** A tracker keeps a torrent's
    /// id when its tags are edited, so two copies of one re-tagged release share it — and they are one
    /// row, not two. Keying by name instead let both survive into the overview as identical rows while
    /// the apply resolved both to whichever copy <see cref="TorrentEntryPreference"/> picked, so a row's
    /// displayed tag count came from a different file than the one it applied.
    ///
    /// The name is the fallback rather than a component, because a torrent whose comment carries no
    /// recognisable URL has no id and still has to be addressable. The prefixes keep the two spaces
    /// apart: without them a torrent *named* <c>12345</c> and a torrent with *id* <c>12345</c> describing
    /// the same video would be one row.
    /// </summary>
    internal static (int VideoId, string Torrent) RowKey(int videoId, string? torrentId, string torrentName) =>
        (videoId, torrentId is { Length: > 0 } id ? "i:" + id : "n:" + torrentName);

    /// <summary>The identity of a row that already exists, by the one definition above.</summary>
    internal static (int VideoId, string Torrent) RowKey(BatchRow row) =>
        RowKey(row.VideoId, row.TorrentId, row.TorrentName);

    /// <summary>The identity an <see cref="BatchApplyRequest.Rows"/> entry names, by that same definition.</summary>
    internal static (int VideoId, string Torrent) RowKey(BatchRowRef reference) =>
        RowKey(reference.VideoId, reference.TorrentId, reference.TorrentName);

    /// <summary>
    /// Rows that may fail in a row before the run stops itself.
    ///
    /// The same shape as the cover breaker in <see cref="CoverRateLimiter"/> and deliberately **not**
    /// the same constant. That one is a number quoted verbatim to the tracker's staff, so tying this to
    /// it would mean a change here silently altering a promise made to a third party. Five is chosen
    /// for the same reason it was there — enough that an unlucky run of bad rows does not stop a good
    /// import, few enough that a systemic fault is caught before it has walked the whole selection.
    /// </summary>
    private const int MaxConsecutiveFailures = 5;

    /// <summary>
    /// One line describing a row failure, for the sample reported to the page.
    ///
    /// The exception's own message, because on the failures this actually sees it is the useful half —
    /// a name conflict names the name. Trimmed to one line: some providers put the offending SQL in
    /// there, and the page renders this as a sentence in a status line.
    /// </summary>
    private static string Describe(Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        return message.Length <= 200 ? message : message[..200].TrimEnd() + "…";
    }

    public async Task<BatchOverview> ListAsync(CancellationToken ct = default) =>
        (await LoadAsync(withCounts: true, ct)).Overview;

    /// <summary>
    /// Everything both entry points need: the overview, the flattened index behind it, and the two
    /// vocabularies with their derived lookups.
    ///
    /// It is one method because <see cref="ApplyAsync"/> used to call <see cref="ListAsync"/> and then
    /// redo its preamble — loading both vocabularies a second time (four more queries for values that
    /// cannot have changed) and calling <c>index.All()</c> again, which materialises a fresh list of
    /// every indexed entry: 139,141 of them on the real folder. The UI chunks bulk apply in tens, so
    /// each of those was paid 72 times over a 715-row folder.
    /// </summary>
    /// <param name="withCounts">
    /// Whether to compute the figures only the *page* reads.
    ///
    /// Unifying the two entry points removed the duplicated preamble but left apply paying for the
    /// whole overview 72 times a run, and three parts of it are pure waste there: the near-miss scan,
    /// which builds a 139,141-entry basename set and regroups the library; the baseline store read,
    /// which is one whole-store materialisation; and <see cref="Summarise"/>, which classifies
    /// and matches every row against both vocabularies — 715 rows, per chunk.
    ///
    /// What apply genuinely needs from here is the row *identity* and its eligibility, and both are
    /// computed identically in either mode. The one visible difference is that an already-applied row
    /// reads "applied" rather than "updated", because telling those apart is exactly what the skipped
    /// work is for — and both are ineligible, so nothing downstream can see it. **If "updated" ever
    /// becomes eligible, this parameter has to go**, because then apply would need the distinction it
    /// is skipping.
    /// </param>
    private async Task<BatchState> LoadAsync(bool withCounts, CancellationToken ct)
    {
        var entries = index.All();
        if (entries.Count == 0)
        {
            return new BatchState(
                new BatchOverview { Rows = [], Unmatched = 0, VideosMatchableByName = 0, IndexedFiles = 0, Torrents = 0 },
                entries,
                new TagVocabulary([]),
                PerformerMatcher.BuildLookup([]),
                new ClassificationCache(),
                new Dictionary<(int VideoId, string Torrent), TorrentIndexEntry>());
        }

        var sizes = entries.Select(entry => entry.Video.Length).ToHashSet();

        // The library read and the size-to-video rule both live in `LibraryFiles`, because the folder
        // listing for the write folder has to answer the same question, and two answers to it is the
        // failure that already cost this codebase once. That file also carries the measurement — why the library
        // is read wholesale and intersected in memory rather than filtered by the torrent's sizes, and
        // why a `WHERE Size IN (…)` must not come back.
        //
        // `Basename` rides along for the name-only count below: one more column on a read that already
        // happens, rather than a second query.
        var libraryFiles = await LibraryFiles.LoadAsync(db, ct);
        var videoIdBySize = LibraryFiles.VideoIdBySize(libraryFiles, sizes);

        // Videos the size match missed that the name match would find.
        //
        // The comparer is `OrdinalIgnoreCase` because `TorrentIndexSnapshot.ByBasename` is, and this
        // count is a claim about what `TorrentIndex.Find` would do. If the two ever disagree, the page
        // reports a number of videos the dialog then refuses to match — which is the one way this can
        // lie, and it would lie quietly.
        //
        // Grouped by video rather than counted per file: a video with a size match on one file and a
        // name match on another is matched, since `TorrentMatchService` takes the first hit across
        // `video.Files`. Per-file counting would report it as a near-miss.
        var videosMatchableByName = 0;
        if (withCounts)
        {
            var indexedBasenames = entries
                .Select(entry => entry.Video.Basename)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            videosMatchableByName = libraryFiles
                .GroupBy(file => file.VideoId)
                .Count(video =>
                    !video.Any(file => sizes.Contains(file.Size))
                    && video.Any(file => indexedBasenames.Contains(file.Basename)));
        }

        var videoIds = videoIdBySize.Values.ToHashSet();
        var videos = await db.Videos
            .AsNoTracking()
            .Where(video => videoIds.Contains(video.Id))
            // TagIds and PerformerIds are the denormalised arrays Cove maintains on save, so the current
            // tag count and both sets the "would add" columns are counted against come out of this one
            // read — no join, and no second query per row. PerformerIds made the performer column a widening
            // of a projection rather than the join it looked like it needed.
            // `HasImage` rather than the blob id itself: the page only ever asks whether to render the
            // library's thumbnail slot, and asking for an image that is not there costs a 404 per row
            // — a request Cove answers for nothing and the browser logs as an error.
            .Select(video => new
            {
                video.Id,
                video.Title,
                video.TagIds,
                video.PerformerIds,
                HasImage = video.ImageBlobId != null,
            })
            .ToDictionaryAsync(
                video => video.Id,
                video => new VideoRow(video.Title, [.. video.TagIds], [.. video.PerformerIds], video.HasImage),
                ct);

        var appliedRemoteIds = await db.Set<VideoRemoteId>()
            .AsNoTracking()
            .Where(link => link.Endpoint == RemoteIdEndpoint)
            .Select(link => new { link.VideoId, link.RemoteId })
            .ToListAsync(ct);
        var applied = appliedRemoteIds.Select(link => (link.VideoId, link.RemoteId)).ToHashSet();

        // One store read for every row's baseline. Empty when the extension store is unwired, which is
        // what every test that does not care about this gets, and it falls back below.
        var baselines = baseline is null || !withCounts
            ? new Dictionary<(int, string), int>()
            : (IReadOnlyDictionary<(int VideoId, string TorrentId), int>)await baseline.LoadAsync(ct);

        var knownTags = await LoadTagVocabularyAsync(ct);
        var performers = PerformerMatcher.BuildLookup(await LoadPerformerVocabularyAsync(ct));
        var classified = new ClassificationCache();

        // Sized for the matches, not the folder: an unmatched entry is counted and dropped rather than
        // turned into a row nothing can be done with.
        // One entry per row, not one per indexed file. Two entries can land on the same row — two copies
        // of a re-tagged release share the tracker id that identifies it — and emitting both put two
        // identical rows on the page while the apply resolved both to a single winner, so a row's counts
        // described a different file than the one it would write.
        //
        // `TorrentEntryPreference` picks the survivor, and it is the same instance `ApplyAsync` and
        // `TorrentIndex.Find` consult: the entry a row is *built* from must be the entry it *applies*, or
        // the page is describing work it will not do. `unmatched` still counts per entry, because it
        // answers how many indexed files matched nothing rather than how many rows there are.
        var entryByRow = new Dictionary<(int VideoId, string Torrent), TorrentIndexEntry>();
        var unmatched = 0;
        foreach (var entry in entries)
        {
            videoIdBySize.TryGetValue(entry.Video.Length, out var matchedId);
            if (matchedId == 0 || !videos.ContainsKey(matchedId))
            {
                unmatched++;
                continue;
            }

            var key = RowKey(matchedId, entry.Torrent.TorrentId, entry.Torrent.Name);
            if (!entryByRow.TryGetValue(key, out var held)
                || TorrentEntryPreference.Instance.Compare(entry, held) < 0)
            {
                entryByRow[key] = entry;
            }
        }

        // Walked in index order rather than over `entryByRow.Values`, because dictionary enumeration order
        // is undefined in .NET and this list is what the page renders: taking it from the dictionary would
        // let row order move between rescans of an unchanged folder. Same reasoning as the total order in
        // `TorrentEntryPreference` — an arbitrary answer is fine, an answer that moves is not.
        var rows = new List<BatchRow>();
        foreach (var entry in entries)
        {
            if (!videoIdBySize.TryGetValue(entry.Video.Length, out var videoId)
                || !videos.TryGetValue(videoId, out var video))
            {
                continue;
            }

            // Only the winner emits, so the losing copy of a shared row is skipped rather than duplicated.
            if (!entryByRow.TryGetValue(RowKey(videoId, entry.Torrent.TorrentId, entry.Torrent.Name), out var won)
                || !ReferenceEquals(won, entry))
            {
                continue;
            }

            var torrent = entry.Torrent;
            // The counts are the page's, and apply reads none of them — it builds its own proposal from
            // the same entry. Skipping this is most of what `withCounts` buys.
            var (tagsToAdd, tagsToCreate, performersToAdd) = withCounts
                ? Summarise(torrent, knownTags, performers, classified, video.TagIds, video.PerformerIds)
                : (0, 0, 0);

            rows.Add(new BatchRow
            {
                TorrentName = torrent.Name,
                FileName = entry.Video.Basename,
                TorrentId = torrent.TorrentId,
                FanOut = entry.FanOut,
                // An applied row whose torrent has *grown since it was applied* is reported as
                // "updated" rather than "applied", which is what makes a re-tagged torrent visible
                // again without making it eligible for anything. Deliberately *not* bulk-eligible: a
                // row can also have tags left because the reviewer declined them on purpose — most
                // often on a pack, where most of the list does not belong to any one scene — and
                // re-applying those in bulk would overwrite a decision rather than deliver an update.
                //
                // "Grown since" is measured against the tag-list size recorded at apply time, not
                // against whether anything is outstanding. Outstanding is the *normal* state of an
                // applied row: a default apply creates no tags, so every tag the library does not
                // already know stays outstanding for good. Judging by that left 692 of 709 real rows
                // (97.6%) reading "updated" the moment they were applied, which is no signal at all
                // and kept nearly every row past the "Hide applied" filter.
                //
                // With no baseline — a row applied before this was recorded — there is nothing to
                // compare against, so the old rule stands. It over-reports rather than under-reports,
                // which is the right direction for a signal whose whole job is to resurface work.
                //
                // Performers count towards this as well as tags. They did not, because there was no
                // per-video performer number to test, so a torrent that gained only a performer stayed
                // "applied" and the update signal had a hole in it.
                // `withCounts` short-circuits rather than relying on the zero counts above reaching the
                // same answer: with no baselines loaded `HasUpdate` falls back to its over-reporting
                // rule, which would call every applied row "updated" on the apply path. Both are
                // ineligible either way, and saying so explicitly is cheaper than reasoning about it.
                Status = torrent.TorrentId is not null && applied.Contains((videoId, torrent.TorrentId))
                    ? withCounts && HasUpdate(baselines, videoId, torrent, tagsToAdd, performersToAdd)
                        ? "updated"
                        : "applied"
                    : "matched",
                VideoId = videoId,
                VideoTitle = video.Title,
                VideoHasImage = video.HasImage,
                VideoTagCount = video.TagIds.Count,
                TagsToAdd = tagsToAdd,
                TagsToCreate = tagsToCreate,
                PerformersToAdd = performersToAdd,
                TorrentCoverUrl = torrent.CoverUrl,
                // Null answers false, the same direction every other cover path fails when the
                // allowlist is unwired: the page shows no thumbnail rather than asking for one the
                // proxy would refuse.
                TorrentCoverAllowed = torrent.CoverUrl is { } coverUrl
                    && Uri.TryCreate(coverUrl, UriKind.Absolute, out var coverUri)
                    && coverHosts?.Allows(coverUri) == true,
            });
        }

        var overview = new BatchOverview
        {
            Rows = rows,
            Unmatched = unmatched,
            VideosMatchableByName = videosMatchableByName,
            IndexedFiles = entries.Count,
            // Reference identity: every video of a torrent points at the one parsed instance.
            Torrents = entries.Select(entry => entry.Torrent).Distinct().Count(),
        };

        return new BatchState(overview, entries, knownTags, performers, classified, entryByRow);
    }

    public async Task<BatchApplyResult> ApplyAsync(BatchApplyRequest request, CancellationToken ct = default)
    {
        // Only matched rows are ever eligible, so the overview dropping the rest costs nothing here —
        // and `withCounts: false` drops the figures only the page reads, which is the same argument one
        // level down.
        var state = await LoadAsync(withCounts: false, ct);
        var rows = state.Overview.Rows;
        var wanted = request.Rows.Select(RowKey).ToHashSet();

        // Named rows are taken as named, including packs: a caller that lists a row has consented to
        // that row, and `IncludePacks` exists to ask a sweep — which names nothing — whether it may
        // reach them. Filtering a named pack row out instead is a request that reports success and
        // does nothing, which is the failure this shape was chosen to remove.
        var eligible = rows.Where(row =>
            row.Status == "matched"
            && (wanted.Count == 0
                ? request.IncludePacks || row.FanOut == 1
                : wanted.Contains(RowKey(row))));

        // Blob and HTTP deps are threaded through so cover import works here too; without them
        // TorrentApplyService silently skips covers. The allowlist rides along for the same reason and
        // fails the other way: dropped, every cover here is refused rather than fetched unchecked.
        var applier = new TorrentApplyService(db, blobs, covers, baseline, blobTransactions);
        var result = new BatchApplyResult();
        var consecutiveFailures = 0;

        // One provenance run id for the whole run, so undoing it is one operation rather than one per
        // video. The UI applies a large selection in chunks, so each chunk is its own run — that
        // is the honest granularity anyway: a chunk is what the user watched succeed or fail.
        var runId = Guid.NewGuid().ToString("n");

        // Taken from the preamble rather than rebuilt here. Two torrents can describe the same file, so
        // `TorrentEntryPreference` decides which one a row means — lowest fan-out, because a single-scene
        // torrent's metadata is about this video while a pack's is the union across its release, then a
        // fixed order so the rest of the answer does not move between runs.
        //
        // That choice is now made exactly once, where the rows are built. It used to be made a second
        // time here, over the same entries with the same rule, which is a correctness trap rather than
        // duplication: the entry a row is *built* from decides the counts the page shows, and the entry
        // looked up *here* decides what gets written. Agreeing by construction is the only way those
        // cannot diverge — this codebase has had one drift between sites answering this question, and
        // then the same drift reappearing between these two.
        //
        // `TorrentIndex.Find` and the forced branch of `TorrentMatchService` share the comparer for the
        // same reason.
        foreach (var row in eligible)
        {
            if (!state.EntryByRow.TryGetValue(RowKey(row), out var entry))
                continue;

            var proposal = BuildProposal(
                entry.Torrent, state.KnownTags, state.Performers, state.Classified, request.CreateNewTags);
            if (proposal.Tags.Count == 0 && proposal.Performers.Count == 0)
                continue;

            TorrentApplyResult? applied;
            try
            {
                applied = await applier.ApplyAsync(new TorrentApplyRequest
                {
                    VideoId = row.VideoId,
                    Tags = proposal.Tags,
                    Performers = proposal.Performers,
                    TagSources = proposal.Sources,
                    TorrentId = entry.Torrent.TorrentId,
                    TorrentTagCount = entry.Torrent.TagList.Count,
                    Url = entry.Torrent.Comment,
                    CoverUrl = request.ImportCovers ? entry.Torrent.CoverUrl : null,
                    // Bulk never overwrites: fields are filled only where empty, and replacing a value the
                    // user set stays a per-item review decision.
                    Overwrite = false,
                    SourceRunId = runId,
                }, ct);
                consecutiveFailures = 0;
            }
            // One row must not cost the rest of the run. The rows are independent by
            // construction — a transaction and a tracker clear each — so continuing is nearly free, and
            // stopping rolls nothing back: every row before the throw is committed either way, so an
            // abort buys a shorter run and strictly less information about it. The failing row itself
            // now leaves nothing at all, rather than the half of it that had already saved, so
            // RowsFailed counts rows that wrote nothing instead of rows in an unknown state.
            //
            // Cancellation is not a row failure and is rethrown: the caller went away, and counting
            // that as data would report a torn run as a partly broken library.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = result with
                {
                    RowsFailed = result.RowsFailed + 1,
                    // First one wins, as with the cover reason: a systemic fault fails every row with
                    // the same sentence, and 468 copies of it is not more information than one.
                    FailureReason = result.FailureReason ?? Describe(ex),
                };

                if (++consecutiveFailures >= MaxConsecutiveFailures)
                {
                    // Not "some rows are bad" any more. A library that fails this consistently fails on
                    // its own state rather than on any one torrent — a name every row wants and the
                    // host refuses, or a database that has gone away — and grinding through the
                    // remaining rows would produce hundreds of identical failures and no new
                    // information.
                    result = result with { StoppedEarly = true };
                    break;
                }

                continue;
            }
            finally
            {
                // Each video is finished and saved by this point, so nothing here needs to stay tracked —
                // and leaving it tracked is what made bulk apply quadratic. Every SaveChanges does work
                // proportional to the tracker, both in EF's own change detection and in the host's
                // BlobReferenceSaveChangesInterceptor, which walks ChangeTracker.Entries() on each save.
                // Measured over 468 videos the tracker reached 22,481 entities and per-video cost rose
                // 4.9x, from 351 ms to 1710 ms. Clearing bounds it to one video's worth.
                //
                // In `finally` rather than after the call because a throwing row leaves its own entities
                // tracked, some of them Added. The next row's save would try to write them again and
                // fail on the same fault, turning one bad row into every row after it.
                db.ChangeTracker.Clear();
            }

            if (applied is null)
                continue;

            result = result with
            {
                VideosTouched = result.VideosTouched + 1,
                TagsAdded = result.TagsAdded + applied.TagsAdded,
                TagsCreated = result.TagsCreated + applied.TagsCreated,
                PerformersAdded = result.PerformersAdded + applied.PerformersAdded,
                AliasesSeeded = result.AliasesSeeded + applied.AliasesSeeded,
                CoversImported = result.CoversImported + (applied.CoverChanged ? 1 : 0),
                CoversSkipped = result.CoversSkipped + (applied.CoverSkipped is null ? 0 : 1),
                // First one wins, so the reported reason is the one that actually happened first
                // rather than whatever the last video in the chunk hit.
                CoverSkipReason = result.CoverSkipReason ?? applied.CoverSkipped,
            };
        }

        return result;
    }

    /// <summary>
    /// Every spelling the library holds a tag under, mapped to the tag it belongs to.
    ///
    /// A map rather than the set this used to be, because knowing *that* a spelling exists is no longer
    /// enough: the overview has to say whether the video already carries that particular tag, which
    /// needs its id.
    ///
    /// Names are loaded before aliases and inserted with <c>TryAdd</c>, so a primary-name match wins
    /// over an alias match — the precedence <c>RelationNameResolver.ResolveTagsAsync</c> applies on the
    /// apply path. Ordered by id for the same reason the video tiebreak is: nothing else makes the
    /// choice deterministic when two tags share a spelling, and it decides which tag a row is about.
    /// </summary>
    private async Task<TagVocabulary> LoadTagVocabularyAsync(CancellationToken ct)
    {
        var names = await db.Tags.AsNoTracking()
            .OrderBy(tag => tag.Id)
            .Select(tag => new { tag.Id, Spelling = tag.Name })
            .ToListAsync(ct);
        var aliases = await db.Set<TagAlias>().AsNoTracking()
            .OrderBy(alias => alias.Id)
            .Select(alias => new { Id = alias.TagId, Spelling = alias.Alias })
            .ToListAsync(ct);

        var idBySpelling = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in names.Concat(aliases))
            idBySpelling.TryAdd(row.Spelling.Trim(), row.Id);

        return new TagVocabulary(idBySpelling);
    }

    /// <summary>
    /// Every spelling a library performer can be found under, each carrying the performer it belongs to.
    ///
    /// Aliases stay indexed: dropping them would push the names they match back into the tag list as
    /// content. What changed is that a match now yields the performer's id, which is what a bulk apply
    /// sends — a name does not resolve at all here, and would create a duplicate instead.
    /// </summary>
    private async Task<List<PerformerVocabularyEntry>> LoadPerformerVocabularyAsync(CancellationToken ct)
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
    /// What this torrent would add to this video, and how much of that would be created.
    ///
    /// Counted per video rather than against the library vocabulary, and deduplicated by the name
    /// the apply would be sent, because that is the key the apply itself folds on
    /// (<c>TorrentApplyService.ApplyTagsAsync</c> distincts case-insensitively). Two tag-list entries
    /// reaching one name is not hypothetical: it is what this extension's own alias seeding builds up,
    /// and counting both would promise more than an apply delivers — the same class of untruth this
    /// column was fixed for.
    /// </summary>
    /// <summary>
    /// Whether an applied row's torrent has gained tags since it was applied, and still has something
    /// to give.
    ///
    /// Both halves are required. Growth alone would flag a torrent whose new tags this video already
    /// carries — nothing for the reviewer to do — and something-outstanding alone is the 97.6% case
    /// this exists to stop reporting.
    ///
    /// "Something to give" counts performers as well as tags. The growth half needs no performer
    /// equivalent: performers are lifted out of the tag list, so a torrent that gained one gained a
    /// tag-list entry, and the recorded baseline sees it. Only the outstanding half was tag-only, which
    /// is what left a performer-only update reading as "applied".
    /// </summary>
    private static bool HasUpdate(
        IReadOnlyDictionary<(int VideoId, string TorrentId), int> baselines,
        int videoId,
        TorrentRelease torrent,
        int tagsToAdd,
        int performersToAdd)
    {
        if (tagsToAdd == 0 && performersToAdd == 0)
            return false;

        // No baseline recorded — the row was never applied, or was applied before baselines existed —
        // so there is nothing to have grown since, and the older, cruder rule applies: something
        // outstanding is enough. Written as an early return rather than as `? … : true`, which reads
        // like a branch someone forgot to finish and invites exactly the simplification that would
        // invert it.
        if (torrent.TorrentId is not { } torrentId || !baselines.TryGetValue((videoId, torrentId), out var applied))
            return true;

        return torrent.TagList.Count > applied;
    }

    /// <summary>
    /// One proposed tag: the name an apply would be sent, the tracker spelling it came from, and the
    /// library tag it resolved to — null where the library holds none and the apply would create one.
    /// </summary>
    private readonly record struct ProposedTag(string Name, string Source, int? TagId);

    /// <summary>
    /// What this torrent proposes, walked once.
    ///
    /// The column the page shows and the tags an apply writes used to be two walks over the same
    /// classification, and they had already diverged: this one deduplicated by name and the apply's
    /// did not, so the apply's <c>TagSources</c> kept the *last* spelling to reach a name where the
    /// review path keeps the first (see DESIGN-DECISIONS, *One proposed tag per name*). One walk makes
    /// the two answers the same by construction rather than by two edits staying in step.
    ///
    /// Deduplicated before anything is asked about the video, so a name reached twice — once resolved,
    /// once styled — is one proposal either way. Doing it after the "already carried" skip let the
    /// second entry be counted as an addition the apply would then fold away.
    /// </summary>
    private (List<ProposedTag> Tags, IReadOnlyList<MatchedPerformer> Performers) Propose(
        TorrentRelease torrent,
        TagVocabulary vocabulary,
        PerformerLookup performers,
        ClassificationCache classified)
    {
        // The performer vocabulary must be the real one: without it every name-shaped entry stays in the
        // tag list and bulk apply would create tags named after performers.
        var split = PerformerMatcher.Split(classified.Of(torrent), performers);

        var tags = new List<ProposedTag>();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in ContentTags(split.Tags))
        {
            var knownAs = vocabulary.KnownSpelling(tag);
            // The library's own spelling when it has one, so the apply resolves to that row rather than
            // creating a near-duplicate beside it.
            var name = knownAs ?? TagNameStyler.Apply(settings.TagNameStyle, tag.Value, tag.Source);
            // First entry wins, case-insensitively, because that is the comparer the apply resolves
            // with and the rule the review path already follows.
            if (!named.Add(name))
                continue;

            tags.Add(new ProposedTag(name, tag.Source, knownAs is null ? null : vocabulary.TagId(knownAs)));
        }

        return (tags, split.Performers);
    }

    private (int ToAdd, int ToCreate, int PerformersToAdd) Summarise(
        TorrentRelease torrent,
        TagVocabulary vocabulary,
        PerformerLookup performers,
        ClassificationCache classified,
        IReadOnlySet<int> videoTagIds,
        IReadOnlySet<int> videoPerformerIds)
    {
        var (proposed, matched) = Propose(torrent, vocabulary, performers, classified);

        var toAdd = 0;
        var toCreate = 0;
        foreach (var tag in proposed)
        {
            if (tag.TagId is { } tagId && videoTagIds.Contains(tagId))
                continue;

            toAdd++;
            if (tag.TagId is null)
                toCreate++;
        }

        // The same rule the tag count above follows, and for the same reason: what the reviewer is
        // deciding about is what this video would gain, not what the torrent happens to name. Matched
        // by id, so a performer found under an alias still counts as one they already have.
        var performersToAdd = matched.Count(performer => !videoPerformerIds.Contains(performer.Id));

        return (toAdd, toCreate, performersToAdd);
    }

    /// <summary>
    /// The library's tag vocabulary: every spelling a tag can be found under, and which tag that is.
    /// </summary>
    private sealed class TagVocabulary(Dictionary<string, int> idBySpelling)
    {
        /// <summary>
        /// The spelling the library already holds this tag under, or null if it holds none.
        ///
        /// Both forms are tried because either can be the stored one. <c>Value</c> is the
        /// normalised spelling and is checked first; <c>Source</c> is what the tracker wrote, and it is
        /// what a tag created under the dotted naming style is named by, as well as the form the seeded
        /// aliases take.
        ///
        /// It returns the spelling rather than a bool because the caller has to send a name the apply
        /// will resolve to the same row. Sending the normalised form for a tag the library holds only in
        /// source form would not match it — it would create a second tag beside it.
        /// </summary>
        public string? KnownSpelling(ClassifiedTag tag) =>
            idBySpelling.ContainsKey(tag.Value.Trim()) ? tag.Value
            : idBySpelling.ContainsKey(tag.Source.Trim()) ? tag.Source
            : null;

        /// <summary>The tag a spelling <see cref="KnownSpelling"/> returned belongs to.</summary>
        public int TagId(string spelling) => idBySpelling[spelling.Trim()];
    }

    /// <summary>
    /// The apply's half of <see cref="Propose"/>: the same names, filtered to what this run is allowed
    /// to write, in the shape <c>TorrentApplyRequest</c> takes.
    /// </summary>
    private (List<string> Tags, List<int> Performers, Dictionary<string, string> Sources) BuildProposal(
        TorrentRelease torrent,
        TagVocabulary vocabulary,
        PerformerLookup performers,
        ClassificationCache classified,
        bool createNewTags)
    {
        var (proposed, matched) = Propose(torrent, vocabulary, performers, classified);

        var tags = new List<string>();
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in proposed)
        {
            if (tag.TagId is null && !createNewTags)
                continue;

            tags.Add(tag.Name);
            sources[tag.Name] = tag.Source;
        }

        // Ids, because that is what the apply takes. A bulk run has no reviewer to notice a
        // performer that quietly became a second row of the same name.
        return (tags, [.. matched.Select(performer => performer.Id)], sources);
    }

    /// <summary>
    /// The kinds a proposal may carry. The batch page and the review dialog must agree on this, so it
    /// is the same set as <c>TorrentMatchService</c>'s — see there for why `SourceQuality` is not in it
    ///.
    /// </summary>
    private static List<ClassifiedTag> ContentTags(IEnumerable<ClassifiedTag> tags) =>
        [.. tags.Where(tag => tag.Kind is TorrentTagKind.Content or TorrentTagKind.Configuration)];

    /// <summary>
    /// A library video, reduced to what a row needs: its title, and the tags it already carries.
    ///
    /// The tag and performer ids are held as sets rather than the arrays they arrive as because every
    /// row of a pack asks the same video the same question — 1,913 times at the corpus's worst — and
    /// building the set once per video rather than once per row is the difference between that being
    /// free and not.
    /// </summary>
    private sealed record VideoRow(string? Title, HashSet<int> TagIds, HashSet<int> PerformerIds, bool HasImage);

    /// <summary>The shared preamble, computed once per request and used by both entry points.</summary>
    /// <param name="EntryByRow">
    /// The entry each row was built from, by <see cref="RowKey(BatchRow)"/>.
    ///
    /// Carried rather than rebuilt. <c>ApplyAsync</c> used to derive its own map with the same rule, and
    /// two derivations of one rule is how the row a page *shows* and the entry an apply *writes* drift
    /// apart — which has happened here twice, once from each side. Building it where the
    /// rows are built makes them the same answer by construction rather than by agreement.
    /// </param>
    private sealed record BatchState(
        BatchOverview Overview,
        IReadOnlyList<TorrentIndexEntry> Entries,
        TagVocabulary KnownTags,
        PerformerLookup Performers,
        ClassificationCache Classified,
        IReadOnlyDictionary<(int VideoId, string Torrent), TorrentIndexEntry> EntryByRow);

    /// <summary>
    /// Classification results, keyed by the parsed torrent they came from.
    ///
    /// Every video of a pack points at the *same* <see cref="TorrentRelease"/> instance — the overview
    /// already relies on that to count torrents — so classifying per row re-ran eight compiled regexes
    /// over an identical tag list once per scene. The corpus has 1,014 packs, median 70 videos and one
    /// of 1,913, so that is 70x redundant work on the normal path and 1,913x at the worst.
    ///
    /// Keyed by reference deliberately: <see cref="TorrentRelease"/> is a class, so the default comparer
    /// would be reference equality anyway, but saying so keeps a later record conversion from silently
    /// turning every lookup into a deep structural comparison of the tag list.
    /// </summary>
    private sealed class ClassificationCache
    {
        private readonly Dictionary<TorrentRelease, IReadOnlyList<ClassifiedTag>> _byTorrent =
            new(ReferenceEqualityComparer.Instance);

        public IReadOnlyList<ClassifiedTag> Of(TorrentRelease torrent)
        {
            if (_byTorrent.TryGetValue(torrent, out var held))
                return held;

            var classified = TagClassifier.ClassifyAll(torrent.TagList);
            _byTorrent[torrent] = classified;
            return classified;
        }
    }
}
