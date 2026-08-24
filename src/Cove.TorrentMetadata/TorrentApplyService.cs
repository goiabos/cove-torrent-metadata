using System.Globalization;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.TorrentMetadata;

/// <summary>What the user chose to keep out of a proposal. Anything absent is simply not applied.</summary>
public sealed record TorrentApplyRequest
{
    public required int VideoId { get; init; }

    /// <summary>Tag names to apply, as shown in the proposal.</summary>
    public List<string> Tags { get; init; } = [];

    /// <summary>
    /// Library performers to link, by id.
    ///
    /// Ids rather than names because a name is not an identity. Cove does not resolve performers by
    /// alias, and a performer carrying a disambiguation cannot be addressed by a name-only request even
    /// under their exact canonical spelling — so a request written in names would create duplicates
    /// beside the rows it meant to link, silently.
    ///
    /// The proposal already knows the id, so nothing here has to resolve anything.
    /// </summary>
    public List<int> Performers { get; init; } = [];

    /// <summary>
    /// Original dotted tag-list entries keyed by the tag name they were shown under. Recorded as
    /// <see cref="TagAlias"/> rows so later torrents match the same tag exactly instead of relying on
    /// the normaliser again.
    /// </summary>
    public Dictionary<string, string> TagSources { get; init; } = [];

    public string? Title { get; init; }
    public string? Date { get; init; }
    public string? StudioName { get; init; }
    public string? Url { get; init; }
    public string? TorrentId { get; init; }

    /// <summary>
    /// The torrent's raw tag-list size, echoed back from the proposal, and recorded as the baseline
    /// this torrent is compared against next time.
    ///
    /// Null means no baseline is recorded and the row falls back to the older, cruder rule. That is
    /// the honest answer for a caller that cannot know the count — not a reason to guess one.
    /// </summary>
    public int? TorrentTagCount { get; init; }

    /// <summary>
    /// Allows a supplied field to replace a value the video already has. Off by default, so the safe
    /// behaviour (fill empty fields only) is what happens unless the reviewer deliberately asked to
    /// overwrite. Only fields actually present in this request are touched either way.
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>When set, the image at this URL is fetched and stored as the video's cover.</summary>
    public string? CoverUrl { get; init; }

    /// <summary>
    /// Groups every tag link written by one user action, so that action can be undone on its own.
    ///
    /// A bulk apply passes one id for the whole run; a single review leaves it null and gets one
    /// generated for it. The host purges on <c>SourceRunId</c> as readily as on <c>SourceKey</c>
    /// (`AiDataPurgeService.QueryTagApplicationCandidatesAsync`), so this is the difference between
    /// "undo that run" and "undo everything this extension has ever done".
    /// </summary>
    public string? SourceRunId { get; init; }
}

/// <summary>What actually changed, so the UI can report it rather than claiming success blindly.</summary>
public sealed record TorrentApplyResult
{
    public int TagsAdded { get; init; }
    public int TagsCreated { get; init; }
    /// <summary>
    /// Links written. There is deliberately no "created" counterpart: this path links performers the
    /// library already holds and has no way to invent one.
    /// </summary>
    public int PerformersAdded { get; init; }
    public int AliasesSeeded { get; init; }
    public bool TitleChanged { get; init; }
    public bool DateChanged { get; init; }
    public bool StudioChanged { get; init; }
    public bool UrlAdded { get; init; }
    public bool CoverChanged { get; init; }

    /// <summary>
    /// Why a requested cover was not imported, or null when it was — or when none was asked for.
    ///
    /// Every failure path in the cover fetch used to return null silently, which was survivable while
    /// the allowlist shipped populated. It is not survivable now that it ships empty: the first
    /// apply on a fresh install imports no cover, and without this the only signal is a cover that
    /// did not change.
    /// </summary>
    public string? CoverSkipped { get; init; }
}

/// <summary>
/// Writes a reviewed proposal onto a video.
///
/// Only what the user selected is written, and a field is filled only where empty unless the reviewer
/// explicitly asked to overwrite — a torrent is a suggestion, never an authority. Tags and performers
/// are additive for the same reason.
///
/// Tag resolution goes through <see cref="RelationNameResolver"/>, the same helper the proposal used,
/// so what the user was shown ("matches existing" / "will create") is what actually happens.
///
/// Performers do not, and deliberately: they arrive as ids. A name is not an identity — aliases do not
/// resolve, and a disambiguated performer cannot be addressed by name at all — so a proposal and an
/// apply that both spoke names would agree with each other and still be wrong. Speaking ids means
/// neither has to ask what a name means.
/// </summary>
public sealed class TorrentApplyService(
    CoveContext db,
    IBlobService? blobs = null,
    CoverResolver? covers = null,
    AppliedTorrentBaseline? baseline = null,
    BlobReferenceTransactionCoordinator? blobTransactions = null)
{
    public const string RemoteIdEndpoint = "torrent-metadata";

    /// <summary>Custom-field key stamped on tags this extension creates.</summary>
    public const string SourceFieldKey = "torrent-metadata.source";

    /// <summary>
    /// Stamped on every video→tag link this extension writes, as <see cref="TagApplication.SourceKey"/>.
    ///
    /// **Never change this string.** It keys every row already written, and the host's purge matches on
    /// it exactly, so a rename does not migrate anything — it orphans the lot, silently and with no
    /// error, exactly as the remote-id endpoint key nearly did. It is deliberately a literal
    /// rather than the manifest id read at runtime for the same reason: the manifest id has changed
    /// once already, and it must be able to change again without taking the provenance with it.
    ///
    /// It is the same string as <see cref="RemoteIdEndpoint"/> and the stem of
    /// <see cref="SourceFieldKey"/>, so everything this extension stamps reads as one source.
    ///
    /// **It deliberately does not carry Cove's <c>ext:</c> prefix**, which is not decoration:
    /// <c>SourceKeyConventions.IsExtensionSource</c> matches it, and
    /// <c>EffectiveTagDtoLoader.HasEditableDirectSource</c> then returns false for any tag whose only
    /// host-level provenance is an extension — which sets <c>CanRemove = false</c> and renders the tag
    /// as a locked, derived chip the user cannot delete by hand. That is the right semantic for a
    /// tagger that keeps re-deriving and would put the tag straight back. This extension writes once,
    /// on request, so taking away the user's simplest correction would buy them nothing. Tags are only
    /// ever added here and the user stays in control of removing them.
    ///
    /// <c>extension:&lt;id&gt;</c> would behave the same way today, but only by failing to match
    /// <c>ext:</c> — a distinction one broadened check would erase, retroactively, for every row ever
    /// written. This name has no such dependency.
    /// </summary>
    public const string TagSourceKey = "torrent-metadata";

    /// <summary>
    /// The named client the cover fetch uses. It is registered with automatic redirects turned off so
    /// every hop can be checked against <see cref="CoverHostAllowlist"/>; on the default handler the
    /// redirect is followed before we ever see it, and only the first URL would have been checked.
    /// </summary>
    public const string CoverHttpClientName = "io.github.goiabos.torrent-metadata.cover";

    private int? _sourceFieldId;

    public async Task<TorrentApplyResult?> ApplyAsync(TorrentApplyRequest request, CancellationToken ct = default)
    {
        // The cover is fetched at most once, before the transaction opens and outside any retry. It is
        // an outbound HTTP request through a rate limiter plus a blob write, and holding a database
        // transaction open across either turns a slow image host into contention on the tag namespace —
        // which under 1.3 is one global advisory lock for the whole install. Fetching it once is
        // also the "at most one request per URL" promise the tracker clearance rests on, so a
        // second attempt reuses the blob rather than asking that host for the same image again.
        string? coverBlobId = null;
        string? coverSkipped = null;
        var coverFetched = false;

        TorrentApplyResult? result = null;
        var videoFound = false;

        // Everything this apply writes goes in one transaction, so a failure leaves the library exactly
        // as it was. It used to be two unwrapped saves — the first committing new Tag rows, the second
        // the provenance stamps, aliases, links and scalars — so a throw between them grew the
        // vocabulary with nothing linked to it and answered the endpoint with a raw 500. Under
        // 1.3 the second save is the likely one to throw, because names and aliases share one
        // case-insensitive namespace that SaveChanges enforces.
        //
        // Through the execution strategy rather than calling BeginTransactionAsync directly: Cove's own
        // AddDbContext<CoveContext> registration turns on EnableRetryOnFailure
        // (Cove.Data/DataServiceExtensions.cs), and a retrying strategy refuses a user-initiated
        // transaction outright. The host's own TagMergeService is written this way for the same reason.
        // A retry re-runs the whole delegate, which is why the video is read inside it and why the
        // tracker is cleared first — the entities from the failed attempt are still Added, and saving
        // them again is how one failure poisons every later save on a shared context.
        var attempt = 0;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0)
                db.ChangeTracker.Clear();

            // Three collection includes on one root would normally be a cartesian product: EF folds them
            // into a single join, so a video with t tags, p performers and u urls comes back as t*p*u rows
            // to de-duplicate client-side. There is deliberately no AsSplitQuery() here — the host already
            // sets QuerySplittingBehavior.SplitQuery globally for CoveContext, in the sole
            // AddDbContext<CoveContext> registration (Cove.Data/DataServiceExtensions.cs), so this is
            // already three queries and a local call would only restate a host default. Checked rather
            // than assumed, because adding it looks free. All three collections are read below.
            var video = await db.Videos
                .Include(candidate => candidate.VideoTags)
                .Include(candidate => candidate.VideoPerformers)
                .Include(candidate => candidate.Urls)
                .FirstOrDefaultAsync(candidate => candidate.Id == request.VideoId, ct);

            if (video is null)
                return;

            videoFound = true;

            // A cover URL is only ever sent when the reviewer ticked the cover box, so its presence *is*
            // the intent to replace. Gating it behind the scalar-field Overwrite flag meant ticking the
            // box did nothing on a video that already had a cover, while the dialog said "will replace".
            if (!coverFetched && !string.IsNullOrWhiteSpace(request.CoverUrl))
            {
                (coverBlobId, coverSkipped) = await TryStoreCoverAsync(request.CoverUrl, ct);
                coverFetched = true;
            }

            // Blob references and explicit transactions are mutually exclusive unless the host is asked
            // first. BlobReferenceSaveChangesInterceptor rejects outright any save that changes one
            // inside an explicit transaction — detaching a blob deletes a file, and a file delete does
            // not roll back — so setting ImageBlobId below would throw on the way in.
            // BlobReferenceTransactionCoordinator is the host's opt-in for exactly this: it holds the
            // reference lease across the transaction and defers the cleanup until CompleteAsync
            // confirms the commit. Cove's own TagMergeService is written this way, and this is the
            // supported answer rather than a way around the guard.
            //
            // Null only where nothing registered it — a directly constructed service in a test with no
            // interceptor wired, where there is no blob-reference plan for the guard to reject either.
            var blobTransaction = blobTransactions is null ? null : await blobTransactions.BeginAsync(db, ct);
            var committed = false;
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var (tagsAdded, tagsCreated, aliasesSeeded) = await ApplyTagsAsync(video, request, ct);
                var performersAdded = await ApplyPerformersAsync(video, request.Performers, ct);

                var titleChanged = false;
                if ((request.Overwrite || string.IsNullOrWhiteSpace(video.Title)) && !string.IsNullOrWhiteSpace(request.Title))
                {
                    video.Title = request.Title;
                    titleChanged = true;
                }

                var dateChanged = false;
                if ((request.Overwrite || video.Date is null)
                    && !string.IsNullOrWhiteSpace(request.Date)
                    // Exact and invariant, matching what the proposal wrote. `TryParse` under
                    // the ambient culture reads a Gregorian ISO date as something else, or as nothing
                    // at all, and a date that fails to parse here is simply not applied — no error
                    // reaches the reviewer, who ticked the field and watched it do nothing. Exact is
                    // right rather than merely strict: this value is never typed, it is the string this
                    // extension put in the proposal.
                    && DateOnly.TryParseExact(
                        request.Date,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var parsedDate))
                {
                    video.Date = parsedDate;
                    dateChanged = true;
                }

                var studioChanged = false;
                if ((request.Overwrite || video.StudioId is null) && !string.IsNullOrWhiteSpace(request.StudioName))
                {
                    // Only ever links to a studio that already exists. The tag list carries a bare domain
                    // ("lanternbay"), and creating studios from that would litter the library with lowercase
                    // near-duplicates of studios the user already curates.
                    //
                    // This scans the studio table: it compiles to WHERE lower(name) = @p, which cannot use
                    // Cove's plain btree index on studios.name. Left as it is on purpose. Cove runs on
                    // PostgreSQL, and none of the usual answers survive contact with that:
                    //
                    //   - EF.Functions.ILike is Npgsql-only and would throw in this project's SQLite tests —
                    //     and ILIKE with no wildcard is not index-usable in PostgreSQL either, so it trades
                    //     portability for nothing.
                    //   - A case-insensitive collation on studios.name, or a functional index on
                    //     lower(name), would both fix it — and both are changes to a column and a table Cove
                    //     owns. An extension does not migrate the host's schema.
                    //
                    // Note the shape here is already the one that *would* become an index seek the moment
                    // Cove adds an index on lower(name), so nothing here needs rewriting if it ever does.
                    // Trying an exact-case match first to hit the existing index was considered and rejected:
                    // the tag list supplies a lowercase domain and curated studios are properly cased, so it
                    // would miss nearly always and cost an extra round trip per video — making the bulk case,
                    // the only case where this matters, worse.
                    var normalized = request.StudioName.Trim().ToLowerInvariant();
                    var studio = await db.Studios.FirstOrDefaultAsync(candidate => candidate.Name.ToLower() == normalized, ct);
                    if (studio is not null)
                    {
                        video.StudioId = studio.Id;
                        studioChanged = true;
                    }
                }

                var coverChanged = false;
                if (coverBlobId is not null)
                {
                    video.ImageBlobId = coverBlobId;
                    coverChanged = true;
                }

                var urlAdded = false;
                if (!string.IsNullOrWhiteSpace(request.Url)
                    && !video.Urls.Any(existing => string.Equals(existing.Url, request.Url, StringComparison.OrdinalIgnoreCase)))
                {
                    video.Urls.Add(new VideoUrl { VideoId = video.Id, Url = request.Url });
                    urlAdded = true;
                }

                // A stable remote id makes re-importing the same torrent a no-op rather than an accumulation.
                if (!string.IsNullOrWhiteSpace(request.TorrentId))
                {
                    var alreadyLinked = await db.Set<VideoRemoteId>().AnyAsync(
                        link => link.VideoId == video.Id
                            && link.Endpoint == RemoteIdEndpoint
                            && link.RemoteId == request.TorrentId,
                        ct);

                    if (!alreadyLinked)
                    {
                        db.Set<VideoRemoteId>().Add(new VideoRemoteId
                        {
                            VideoId = video.Id,
                            Endpoint = RemoteIdEndpoint,
                            RemoteId = request.TorrentId,
                        });
                    }
                }

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                committed = true;

                // Only now is the detached blob safe to delete, which is the whole reason the coordinator
                // exists: the interceptor's own cleanup runs on a successful save, and inside a transaction
                // a successful save is not yet a committed one.
                if (blobTransaction is not null)
                    await blobTransaction.CompleteAsync();

                // After the commit, and for the same reason the baseline below is: the in-memory copy of
                // these bytes is the cheapest answer there is, and dropping it before a write that could
                // still throw loses them — leaving an orphaned blob and a re-download, which is the "at most
                // one request per URL" promise broken by the code that exists to keep it.
                if (coverChanged)
                    covers?.ForgetPreview(request.CoverUrl);

                result = new TorrentApplyResult
                {
                    TagsAdded = tagsAdded,
                    TagsCreated = tagsCreated,
                    AliasesSeeded = aliasesSeeded,
                    PerformersAdded = performersAdded,
                    TitleChanged = titleChanged,
                    DateChanged = dateChanged,
                    StudioChanged = studioChanged,
                    UrlAdded = urlAdded,
                    CoverChanged = coverChanged,
                    CoverSkipped = coverSkipped,
                };
            }
            finally
            {
                // A rollback un-creates rows, and one of them is cached. GetOrCreateSourceFieldAsync
                // remembers the provenance definition's id for the life of this service, which was
                // safe while its own save committed on its own; inside a transaction that id is
                // provisional until the commit. The batch path is where that bites: one service
                // instance applies every row, so a failed row would hand the next one a definition id
                // that no longer exists and fail it on a foreign key — turning one bad row into a run
                // of them, which is exactly what the bulk apply's breaker would then read as systemic.
                if (!committed)
                    _sourceFieldId = null;

                // Aborts if CompleteAsync never ran, which releases the lease and drops the deferred
                // cleanup — the blob the failed apply would have detached is still referenced by the
                // row the rollback restored, so deleting it would delete a live cover.
                if (blobTransaction is not null)
                    await blobTransaction.DisposeAsync();
            }
        });

        if (!videoFound)
            return null;

        // Outside the transaction because it is not in the database the transaction covers: the baseline
        // lives in the extension store. After it, so a baseline is never recorded for an apply that did
        // not land. It is written on every apply that does, including a re-apply: the reviewer has just
        // seen this torrent's current list, so that list is what the next "has it changed" question is
        // asked against.
        if (baseline is not null && request.TorrentTagCount is { } tagCount && !string.IsNullOrWhiteSpace(request.TorrentId))
            await baseline.RecordAsync(request.VideoId, request.TorrentId, tagCount, ct);

        return result;
    }

    /// <summary>
    /// Ensures the provenance field exists and returns its id, or null if it cannot be created.
    ///
    /// Stamped only on tags the extension *creates*, never on ones it merely applies: the field lives on
    /// the tag globally, so "this tag exists because of a torrent import" is a durable fact, while
    /// "a torrent once mentioned this tag" would be noise. It also gives the only practical undo — the
    /// created tags become a filterable set.
    ///
    /// Get-or-create against a unique index, not check-then-act. The lookup and the insert are two
    /// statements, and this service is scoped, so two applies running at once each hold their own
    /// instance with its own empty cache and nothing serialises them: both can read null and both can
    /// insert. <c>CustomFieldDefinition.Key</c> is uniquely indexed, so the loser's insert fails — and
    /// because this is called from inside <see cref="ApplyTagsAsync"/>, an uncaught failure would fail
    /// the entire apply and lose every tag the reviewer just approved, over a row that by then exists.
    /// The window is only ever open on a library that has never imported before, which is exactly what
    /// makes it worth handling rather than diagnosing: it happens once, to a new user, and never
    /// reproduces.
    /// </summary>
    private async Task<int?> GetOrCreateSourceFieldAsync(CancellationToken ct)
    {
        // Cached per service instance: this used to be queried once per created tag, which on a first
        // bulk import is a round trip per new tag for a value that never changes.
        if (_sourceFieldId is { } cached)
            return cached;

        var existing = await db.Set<CustomFieldDefinition>()
            .FirstOrDefaultAsync(field => field.Key == SourceFieldKey, ct);
        if (existing is not null)
            return _sourceFieldId = existing.Id;

        var definition = new CustomFieldDefinition
        {
            Key = SourceFieldKey,
            Label = "Imported from",
            Type = CustomFieldTypes.Text,
            EntityTypes = [CustomFieldEntityTypes.Tag],
            Filterable = true,
        };

        db.Set<CustomFieldDefinition>().Add(definition);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Detaching first is not tidiness. A failed insert stays `Added`, so every later
            // SaveChanges on this context would re-attempt the same doomed row — which in the batch
            // path means one collision on the first video poisons the rest of the folder.
            db.Entry(definition).State = EntityState.Detached;

            var winner = await db.Set<CustomFieldDefinition>()
                .FirstOrDefaultAsync(field => field.Key == SourceFieldKey, ct);

            // Only a lost race is recoverable. If the row still is not there the insert failed for
            // some other reason, and swallowing that would silently drop provenance on every tag
            // this import creates.
            if (winner is null)
                throw;

            return _sourceFieldId = winner.Id;
        }

        return _sourceFieldId = definition.Id;
    }

    /// <summary>
    /// Applies the selected tags, creating any that do not resolve.
    ///
    /// Structured as two writes rather than a write per tag. The naive shape — save each new tag, look up
    /// the provenance field, save its value, then query whether an alias exists — costs roughly one round
    /// trip per tag, which on a first bulk import across a library is minutes of latency for work the
    /// database could do in two statements.
    /// </summary>
    private async Task<(int Added, int Created, int Aliased)> ApplyTagsAsync(
        Video video,
        TorrentApplyRequest request,
        CancellationToken ct)
    {
        if (request.Tags.Count == 0)
            return (0, 0, 0);

        // One id for everything this call writes. A bulk run supplies its own so all its videos share
        // one, which is what makes that run undoable as a unit rather than only as part of everything
        // this extension has ever applied.
        var runId = string.IsNullOrWhiteSpace(request.SourceRunId)
            ? Guid.NewGuid().ToString("n")
            : request.SourceRunId.Trim();

        // A blank name never becomes a tag. The classifier drops empty tag-list entries before they can
        // be proposed, but this list arrives from a browser and never went through it, so the
        // guard has to exist on the write side as well. Cove maps an empty canonical name to the
        // literal `<empty>` and enforces the namespace, so the row this would create permanently claims
        // that name for a tag nobody asked for and nothing can name again.
        var wanted = request.Tags
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The source spellings are resolved alongside the names, in the query that was already being
        // made, because phase two has to know whether a spelling it is about to seed as an alias is
        // one some *other* tag already answers to. `ResolveTagsAsync` keys its result by the
        // spelling asked for and resolves by name and alias alike, which is exactly that question.
        // Asking separately would have cost two more queries per row, and bulk apply makes this call
        // once per video.
        var sources = request.TagSources.Values
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => source.Trim());
        var resolved = await RelationNameResolver.ResolveTagsAsync(
            db,
            [.. wanted.Concat(sources).Distinct(StringComparer.OrdinalIgnoreCase)],
            ct);
        DetachResolvedReads();

        // Phase one: create everything missing in a single save so EF assigns all ids at once.
        var created = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in wanted.Where(name => !resolved.ContainsKey(name)))
        {
            var tag = new Tag { Name = name };
            db.Tags.Add(tag);
            created[name] = tag;
        }

        if (created.Count > 0)
            await SaveCreatedTagsAsync(created, resolved, ct);

        var tagsByName = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in wanted)
        {
            if (resolved.TryGetValue(name, out var existing))
                tagsByName[name] = existing;
            else if (created.TryGetValue(name, out var fresh))
                tagsByName[name] = fresh;
        }

        // Stamp provenance on the newly created tags only — a tag the user already had must never be
        // relabelled as imported.
        if (created.Count > 0 && await GetOrCreateSourceFieldAsync(ct) is { } definitionId)
        {
            foreach (var tag in created.Values)
            {
                db.Set<CustomFieldValue>().Add(new CustomFieldValue
                {
                    DefinitionId = definitionId,
                    EntityType = CustomFieldEntityTypes.Tag,
                    EntityId = tag.Id,
                    TextValue = "torrent-metadata",
                });
            }
        }

        // Phase two: aliases and links, using one alias query for the whole set instead of one per tag.
        var tagIds = tagsByName.Values.Select(tag => tag.Id).ToList();
        var knownAliases = await db.Set<TagAlias>()
            .Where(alias => tagIds.Contains(alias.TagId))
            .Select(alias => new { alias.TagId, alias.Alias })
            .ToListAsync(ct);
        // Canonicalised in .NET, deliberately, and not half of it in SQL. It used to select
        // `alias.Alias.ToLower()`, which is the *database's* lower() under whatever collation it runs,
        // and then probe the set with .NET's ToLowerInvariant — two answers to one question, differing
        // on exactly the inputs the host also treats specially.
        var aliasSet = knownAliases.Select(entry => (entry.TagId, SpellingKey(entry.Alias))).ToHashSet();

        // Which tag already answers to a given spelling. `resolved` covers everything the library held
        // when this call started; the tags created in phase one are added because a request can build
        // the collision by itself — one entry named by the style and one already dotted, where the
        // first's source is the second's name, and neither existed for the resolver to see.
        var ownerBySpelling = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (spelling, owner) in resolved)
            ownerBySpelling.TryAdd(SpellingKey(spelling), owner.Id);
        foreach (var tag in tagsByName.Values)
            ownerBySpelling.TryAdd(SpellingKey(tag.Name), tag.Id);

        var existingTagIds = video.VideoTags.Select(link => link.TagId).ToHashSet();
        int added = 0, aliased = 0;

        foreach (var (name, tag) in tagsByName)
        {
            // Record the torrent's dotted spelling so the next torrent resolves by alias rather than by
            // re-running the normaliser and hoping it lands on the same string.
            //
            // Never onto a spelling another tag already answers to. Comparing against this tag's own
            // name is not enough: the proposal resolves on the normalised form, so the source it
            // carries can belong to a different tag entirely, and writing it is a save that throws —
            // names and aliases are one case-insensitive namespace. The check predates that
            // enforcement and is shaped by its quieter ancestor, where nothing rejected the write and
            // the spelling simply led two ways with nothing saying which won.
            //
            // The alias is dropped and the tag link kept. The link is what the user ticked; the alias
            // only saves a later match from re-deriving it, and it is already saved — by the tag that
            // owns the spelling.
            if (request.TagSources.TryGetValue(name, out var source)
                && !string.IsNullOrWhiteSpace(source)
                && !string.Equals(source, tag.Name, StringComparison.OrdinalIgnoreCase)
                && !(ownerBySpelling.TryGetValue(SpellingKey(source), out var owner) && owner != tag.Id)
                && aliasSet.Add((tag.Id, SpellingKey(source))))
            {
                db.Set<TagAlias>().Add(new TagAlias { TagId = tag.Id, Alias = source });
                aliased++;
            }

            if (existingTagIds.Add(tag.Id))
            {
                video.VideoTags.Add(new VideoTag { VideoId = video.Id, TagId = tag.Id });
                added++;

                // Provenance for the link, not just for the tag. The custom field above says "this tag
                // exists because of a torrent import"; this says "this *video* carries this tag because
                // of one" — which is the half that makes the work reversible, since the host purges
                // tag_applications by source and then drops the links left with no provenance behind
                // them (`AiDataPurgeService.RemoveOrphanedTagLinksAsync`). Without a row here, a link
                // this extension wrote is indistinguishable from one the user applied by hand.
                //
                // Written straight to the DbSet rather than through ITagProvenanceService: that service
                // lives in Cove.Api, which no extension can reference, and its RecordAsync queries for
                // an existing row per tag — a round trip each, against ~16 tags a video. These rows ride
                // the save the apply already makes, and the unique index cannot fire because a run id is
                // new on every apply.
                //
                // Deliberately inside this branch, which Cove's own VideoMetadataApplyService is not:
                // it records provenance for every tag in the payload, including tags the video already
                // carried. Claiming a link we did not create would hand it to the purge, and since
                // almost no link in a real library has provenance at all, the one it deleted would
                // usually be the user's.
                db.Set<TagApplication>().Add(new TagApplication
                {
                    HostType = AffinityHostType.Video,
                    HostId = video.Id,
                    TagId = tag.Id,
                    SourceKey = TagSourceKey,
                    SourceRunId = runId,
                });
            }
        }

        return (added, created.Count, aliased);
    }

    /// <summary>
    /// Drops the resolver's reads out of the change tracker, having taken the ids off them.
    ///
    /// <c>RelationNameResolver.ResolveTagsAsync</c> is <c>db.Tags.Include(tag =&gt; tag.Aliases)</c> with
    /// no <c>AsNoTracking()</c>, on a context the caller goes on to save — so one call puts **the whole
    /// tag table and every alias** into the tracker for the rest of that context's life
    /// regardless of how few of them the caller asked for. Every later <c>SaveChanges</c> then pays change detection
    /// over all of it, plus Cove's own <c>BlobReferenceSaveChangesInterceptor</c>, which walks
    /// <c>ChangeTracker.Entries()</c> on each save.
    ///
    /// Measured on a 1.3-migrated copy of the real library — 908 tags, 2,643 aliases — one call leaves
    /// **3,551 entities** tracked, and a save that adds a single tag goes from 8.8 ms to 372 ms. Per
    /// video that is ~800 ms of which ~98% is change detection over rows nobody is changing; detaching
    /// takes it to ~148 ms, so a bulk run over 875 videos falls from ~11.7 min to ~2.2 min.
    ///
    /// Nothing here needs the entities afterwards — the resolve is consumed as <c>.Id</c> and
    /// <c>.Name</c>, and every write below sets <c>TagId</c> as a scalar rather than through a
    /// navigation.
    ///
    /// Only <see cref="EntityState.Unchanged"/> entries go, and that filter is **unreachable from both
    /// call sites, deliberately kept**. Each sits immediately after a resolve and before anything is
    /// added, so there is no <c>Added</c> tag for it to spare — removing the state check passes the
    /// whole suite, and that is stated here rather than left for someone to discover as dead code. It
    /// stays because the method promises to detach *reads*: a third call site placed after phase one
    /// would otherwise detach the tags being created, and since the save is what assigns their ids the
    /// symptom would be links written against id 0 rather than an exception.
    ///
    /// **This is compensating for a host defect and must be deleted, not kept, if Cove ever adds
    /// `AsNoTracking()` there.** An extension carrying a workaround for a fixed bug is worse than the
    /// bug: it would then be detaching rows the host had already declined to track, for a cost nobody
    /// could find by reading the host.
    ///
    /// It is not our own recomputation, and it is not the bulk apply's per-row clear,
    /// which bounds growth *across* rows where this bounds it *within* one. The host refills the tracker
    /// on the next call, which is exactly why clearing per row did not fix it.
    /// </summary>
    private void DetachResolvedReads()
    {
        foreach (var entry in db.ChangeTracker.Entries<Tag>().ToArray())
        {
            if (entry.State == EntityState.Unchanged)
                entry.State = EntityState.Detached;
        }

        foreach (var entry in db.ChangeTracker.Entries<TagAlias>().ToArray())
        {
            if (entry.State == EntityState.Unchanged)
                entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// The key Cove keys its tag namespace by: trim, then <c>ToLowerInvariant</c>.
    ///
    /// Every question this service asks about whether a spelling is already taken has to be asked in
    /// the host's terms, because the host is what refuses the save. Three places used to answer it
    /// three ways — the database's <c>lower()</c> for the known aliases, <c>ToLowerInvariant</c> with
    /// no trim for the probe against them, and <c>OrdinalIgnoreCase</c> over trimmed keys for who owns
    /// a spelling. The trim is what made it reachable: <c>TagSources</c> arrives from a browser and
    /// nothing on the way in trims it, so a padded source missed the set, inserted again, and was
    /// trimmed by <c>SaveChanges</c> into a spelling the tag already answered to — which throws, and
    /// takes the whole transactional apply with it.
    ///
    /// The casing half is the exotic one, and it is unified here rather than shown to fail:
    /// <c>ToLowerInvariant</c> folds U+212A KELVIN onto <c>k</c> where <c>OrdinalIgnoreCase</c> does
    /// not, so the two disagree about a spelling the host considers taken. It has no test, and that is
    /// deliberate — a case reaching it has to have the colliding tag *created by the same request*,
    /// because a tag the library already held resolves through <c>ResolveTagsAsync</c>, which keys its
    /// answer by the spelling asked for and so agrees with either comparer. Worth removing the
    /// disagreement, not worth a fixture that exists only to prove a character.
    ///
    /// This mirrors <c>TagNameRules.NormalizeAlias</c> + <c>NamespaceKey</c> rather than calling them:
    /// both are in <c>Cove.Core</c> and reachable, but naming the pair here states the contract this
    /// depends on, and it is the *contract* — not the helper — that a host change would move.
    /// </summary>
    private static string SpellingKey(string spelling) => spelling.Trim().ToLowerInvariant();

    /// <summary>
    /// Commits the tags this apply is creating, and survives losing the race to create one.
    ///
    /// Get-or-create against a namespace the host enforces, not check-then-act — the same shape and the
    /// same reasoning as <see cref="GetOrCreateSourceFieldAsync"/>. The resolve and this insert are two
    /// statements, this service is scoped, and nothing serialises two applies: both can see a name as
    /// missing and both can insert it. On Cove 1.3 the loser does not quietly get a duplicate row, it
    /// gets a <c>TagNameConflictException</c> out of SaveChanges — names and aliases share one
    /// case-insensitive namespace, and a global advisory lock serialises the writes — and an uncaught
    /// one now rolls the whole apply back, losing every tag the reviewer just approved over rows that by
    /// then exist.
    ///
    /// Recovering in place is only possible because EF takes a savepoint before a SaveChanges that runs
    /// inside an open transaction and rolls back to it on failure, so the transaction
    /// <see cref="ApplyAsync"/> opened is still usable after the catch.
    ///
    /// The winner's row is adopted into <paramref name="resolved"/> and the name dropped from
    /// <paramref name="created"/>, because "created" decides two further things: which tags carry the
    /// provenance stamp — a tag we did not create must never be relabelled as imported — and the count
    /// the reviewer is shown. A partial collision is why this is not a single re-query: the save fails
    /// as a unit, so names that really are absent still have to be inserted, and they get one more
    /// attempt. A second failure is not a lost race, and it is left to the caller, which rolls the apply
    /// back whole rather than half-applying it.
    /// </summary>
    private async Task SaveCreatedTagsAsync(
        Dictionary<string, Tag> created,
        Dictionary<string, Tag> resolved,
        CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return;
        }
        catch (Exception error) when (error is DbUpdateException or TagNameConflictException)
        {
            // Detaching first is not tidiness. A failed insert stays Added, so every later SaveChanges
            // on this context would re-attempt the same doomed rows — which in the batch path means one
            // collision on the first video poisons the rest of the folder.
            foreach (var tag in created.Values)
                db.Entry(tag).State = EntityState.Detached;
        }

        var winners = await RelationNameResolver.ResolveTagsAsync(db, [.. created.Keys], ct);
        DetachResolvedReads();

        var stillMissing = new List<string>();
        foreach (var name in created.Keys.ToList())
        {
            if (winners.TryGetValue(name, out var winner))
            {
                resolved[name] = winner;
                created.Remove(name);
            }
            else
            {
                stillMissing.Add(name);
            }
        }

        if (stillMissing.Count == 0)
            return;

        foreach (var name in stillMissing)
        {
            var tag = new Tag { Name = name };
            db.Tags.Add(tag);
            created[name] = tag;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Links the requested performers to the video. It cannot create one, and that is the point.
    ///
    /// <c>docs/DESIGN-DECISIONS.md</c> says performers are matched, never detected, and the corpus run
    /// measured it: 101 resolved across 53 videos and none ever created. That used to be a property of
    /// how <c>PerformerMatcher</c> happened to behave, held up by a create path sitting right here that
    /// simply never fired. It is now a property of there being no such path — the request carries ids,
    /// so there is no name for this method to invent a row from.
    ///
    /// Unknown ids are skipped rather than throwing: a performer can be deleted between the review and
    /// the apply, and losing the whole apply over a row that no longer exists helps nobody.
    /// </summary>
    private async Task<int> ApplyPerformersAsync(
        Video video,
        List<int> performerIds,
        CancellationToken ct)
    {
        if (performerIds.Count == 0)
            return 0;

        var wanted = performerIds.Distinct().ToList();
        var known = await db.Performers
            .Where(performer => wanted.Contains(performer.Id))
            .Select(performer => performer.Id)
            .ToListAsync(ct);
        var knownIds = known.ToHashSet();

        var existingIds = video.VideoPerformers.Select(link => link.PerformerId).ToHashSet();
        var added = 0;

        foreach (var performerId in wanted)
        {
            if (!knownIds.Contains(performerId))
                continue;

            if (existingIds.Add(performerId))
            {
                video.VideoPerformers.Add(new VideoPerformer { VideoId = video.Id, PerformerId = performerId });
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// Fetches a cover image and stores it as a blob, or explains why it did not.
    ///
    /// Never throws for anything the image host does: a cover is the least important thing in a
    /// proposal, and an unreachable image host must not cost the user the tags they just approved. It
    /// does now always *say* something, which is the part that changed once the allowlist existed — with it
    /// shipping empty, a silent refusal is indistinguishable from a broken feature.
    ///
    /// Cancellation is the one thing that does come out, because it is not the image host's doing and
    /// the apply is already unwinding.
    ///
    /// The sequence itself is <see cref="CoverResolver"/>'s. It used to be written out here as well as
    /// in <see cref="CoverProxyService"/>, and the two had drifted three ways by the time anyone
    /// compared them — the worst of which lived on this side, where a refusal by the *rate
    /// limiter* was recorded in the negative cache, so a sixty-second breaker turned into ten minutes
    /// of missing thumbnails on the batch page.
    /// </summary>
    private async Task<(string? BlobId, string? Skipped)> TryStoreCoverAsync(string url, CancellationToken ct)
    {
        // Both are optional constructor dependencies, and the batch service once built this applier
        // without them — the covers silently never imported, and it took a hand check to notice.
        if (blobs is null || covers is null)
            return (null, "Cover skipped: image fetching is not available in this context.");

        var stored = await covers.StoreAsync(url, blobs, ct);
        return (stored.BlobId, stored.Skipped);
    }
}
