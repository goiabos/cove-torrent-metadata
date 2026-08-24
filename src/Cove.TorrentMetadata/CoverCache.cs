using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cove.Core.Interfaces;
using Cove.Plugins;

namespace Cove.TorrentMetadata;

/// <summary>
/// Remembers which blob a cover URL was already stored as, so the same image is not downloaded twice.
///
/// The third of the measures the tracker's staff conditioned clearance on: "caching to avoid
/// redownloading the same images over and over". It is not an optimisation for us — without it a
/// pack's tag list is applied to each of its scenes in turn and every one of them re-requests the
/// *same* cover, which is the shape most likely to look like abuse from the far end.
///
/// It has to exist here because Cove's blob store is GUID-keyed rather than content-addressed
/// (<c>Cove.Api/Services/BlobService.cs</c>), so storing the same bytes twice yields two blobs and
/// the host has no way to notice. The cache therefore saves the duplicate blob as well as the
/// request.
///
/// **Reusing one blob across many videos makes the host's reference counting a correctness
/// dependency.** Before this, one video's cover was one blob; now a pack's scenes share one. Deleting
/// a video must not take the cover away from its siblings, and nothing in this repo can enforce that
/// — it is <c>Cove.Api.BlobService.DeleteBlobIfUnreferencedAsync</c>'s job, and that assembly is not
/// referenced here. Note the trap beside it: that helper's optional argument defaults to deleting
/// unconditionally.
/// </summary>
/// <param name="time">Clock for the failure TTL. Defaults to the system clock.</param>
/// <param name="maxCachedCovers">
/// Overrides <see cref="MaxCachedCovers"/> so a test can reach the eviction path without minting ten
/// thousand entries, the way <c>CoverPreviewCache</c>'s budget already is. Production never passes it.
/// </param>
public sealed class CoverCache(TimeProvider? time = null, int maxCachedCovers = CoverCache.MaxCachedCovers)
{
    /// <summary>
    /// Prefix for the per-URL keys in the extension store. One key per URL rather than one key
    /// holding the whole map: a shared value would have to be rewritten on every store, which turns
    /// a bulk apply into a read-modify-write race against itself, and pruning would rewrite it again.
    /// </summary>
    public const string KeyPrefix = "cover:";

    /// <summary>
    /// How long a failed fetch is remembered.
    ///
    /// Short, and deliberately in memory only. Its job is to stop one bulk run re-requesting a dead
    /// cover once per scene — 468 times on the measured folder — not to remember an outage across
    /// days. Persisting it would make a transient failure sticky past the restart that is the
    /// obvious way to ask for a retry, and would mean a user who fixed the cause still waited.
    /// </summary>
    public static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Upper bound on how many URL→blob mappings are held **in memory** at once.
    ///
    /// It bounds the read-through layer and nothing else. It used to delete the persisted row along
    /// with the memory entry, on the theory that this bounded the store too, and that was wrong in
    /// both directions: it never bounded the store — the map starts empty on every boot and
    /// only ever learns about a URL something looks up by name, so rows written in an earlier
    /// session are invisible to this count forever — and where it *did* fire it cost far more than
    /// it saved. Dropping the row discards a ~110-byte record, and the next video wanting that cover
    /// then pays a fresh request to the image host (against "one download per cover URL, persisted
    /// across restarts", which is a clearance condition, not a tuning parameter) *and* a second blob
    /// of identical bytes, because Cove's store is GUID-keyed rather than content-addressed and
    /// cannot notice. Evicting a hundred bytes to spend two megabytes is not a bound.
    ///
    /// **So eviction is now nearly free, and that is the point.** A dropped entry is re-read from
    /// the store the next time it is asked for — one store lookup, no network, no new blob — which
    /// is what lets this stay a memory cap rather than something sized against a promise.
    ///
    /// The measured corpus is 3,218 bookmarked torrents and its heaviest pack shares one cover URL
    /// across 1,913 scenes — packs collapse to a single entry here regardless of size, so this map
    /// tracks distinct *releases*, not distinct videos, and 3,218 is already the ceiling for an
    /// entire bookmarked history. This cap sits about three times above that, so nothing here fires
    /// on an honest library of the measured size at all.
    ///
    /// Eviction always takes the least-recently-touched entry: a URL a bulk apply is currently
    /// walking is touched on every scene and is never the oldest, so a victim is always a URL
    /// nothing has asked for in a long time.
    ///
    /// **What bounds the store is <see cref="ForgetAsync"/> on a stale blob**, which drops a row the
    /// moment it is found to point at nothing. That is lazy rather than eager on purpose: the same
    /// arithmetic above says a boot-time sweep would cost one blob open per row — and
    /// <c>IBlobService</c> has no exists-check, so an "open" is a real one — to reclaim a hundred
    /// bytes each. A row is only ever written by an *import* (<c>CoverResolver.StoreAsync</c> is
    /// <see cref="RememberAsync"/>'s only caller), so the store holds one row per distinct cover the
    /// user has actually imported. That tracks their library, not anything a .torrent controls.
    /// </summary>
    public const int MaxCachedCovers = 10_000;

    /// <summary>
    /// Upper bound on how many distinct URLs' failures are remembered at once.
    ///
    /// Smaller than <see cref="MaxCachedCovers"/> on purpose, because evicting a failure record
    /// costs far less: the entry is memory-only and already expires on its own after
    /// <see cref="FailureTtl"/>, so losing one early just means the next lookup of that exact URL
    /// pays for one real (and, being the same dead URL, probably still-failing) request instead of
    /// being short-circuited — never a working image lost, unlike the map above. Still generous
    /// against the realistic worst case, a library where most covers are dead, which is why it does
    /// not need to track the corpus figures the way the cap above does.
    /// </summary>
    public const int MaxRememberedFailures = 2_000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly int _maxCachedCovers = maxCachedCovers;

    /// <summary>Read-through layer over the store, so a bulk run pays one store read per URL.</summary>
    private readonly ConcurrentDictionary<string, CacheEntry> _blobIdByUrl = new(StringComparer.Ordinal);

    /// <summary>
    /// A remembered blob id plus a strictly increasing touch stamp used only to pick an LRU victim
    /// when <see cref="_blobIdByUrl"/> is over <see cref="MaxCachedCovers"/> — not wall-clock time,
    /// so ordering does not depend on clock resolution or on a test's <see cref="TimeProvider"/>.
    /// </summary>
    private readonly record struct CacheEntry(string BlobId, long Touch);

    private long _touchClock;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Reason)> _failures =
        new(StringComparer.Ordinal);

    private IExtensionStore? _store;

    public void AttachStore(IExtensionStore store) => _store = store;

    /// <summary>
    /// A blob already holding this URL's image, or null when there is nothing usable to reuse.
    ///
    /// The existence check is the whole reason this is not a plain dictionary lookup. The host's
    /// <c>BlobReferenceSaveChangesInterceptor</c> deletes a blob once nothing references it, so a
    /// remembered id goes stale the moment the last video carrying it loses its cover — and handing
    /// back a dangling id would set <c>ImageBlobId</c> to a blob that is not there, which reads to
    /// the user as a broken image rather than as a cache fault.
    ///
    /// A miss prunes the entry, which is one of two things that bound the map's size — the other is
    /// <see cref="MaxCachedCovers"/> below, for a URL that never goes stale because nothing ever
    /// asks for it again.
    /// </summary>
    public async Task<string?> TryReuseAsync(string url, IBlobService blobs, CancellationToken ct = default)
    {
        var blobId = _blobIdByUrl.TryGetValue(url, out var remembered)
            ? remembered.BlobId
            : await ReadAsync(url, ct);

        if (blobId is null)
            return null;

        if (!await ExistsAsync(blobs, blobId, ct))
        {
            await ForgetAsync(url, ct);
            return null;
        }

        Register(url, blobId);
        return blobId;
    }

    /// <summary>Why a recent fetch of this URL failed, if it failed inside <see cref="FailureTtl"/>.</summary>
    public string? RecentFailure(string url)
    {
        if (!_failures.TryGetValue(url, out var failure))
            return null;

        if (_time.GetUtcNow() - failure.At < FailureTtl)
            return failure.Reason;

        _failures.TryRemove(url, out _);
        return null;
    }

    /// <summary>
    /// Records a stored cover. Persisted, because the whole point is that a restart does not send us
    /// back to the tracker for images already on disk.
    /// </summary>
    public async Task RememberAsync(string url, string blobId, CancellationToken ct = default)
    {
        Register(url, blobId);
        _failures.TryRemove(url, out _);

        if (_store is null)
            return;

        try
        {
            await _store.SetAsync(Key(url), blobId, ct);
        }
        catch (Exception)
        {
            // A cache that cannot persist is still a cache for this session. Failing the apply over it
            // would trade a working cover for a bookkeeping problem.
        }
    }

    /// <summary>
    /// Records a failed fetch and the reason the user was given, so a replay says the same thing.
    ///
    /// The reason is kept rather than regenerated because a replay that said something vaguer than
    /// the original would make the second video's report worse than the first's for no reason.
    ///
    /// Bounded the same way as the map above, at <see cref="MaxRememberedFailures"/> — the newest
    /// failure always wins a spot, and if that pushes the count over the cap the oldest-recorded
    /// failure is dropped first, which is also the one closest to expiring under
    /// <see cref="FailureTtl"/> on its own.
    /// </summary>
    public void RememberFailure(string url, string reason)
    {
        _failures[url] = (_time.GetUtcNow(), reason);

        if (_failures.Count > MaxRememberedFailures)
            EvictOldestFailure();
    }

    /// <summary>
    /// Registers a URL→blob mapping in the read-through memory layer and, if that pushes the map
    /// over <see cref="MaxCachedCovers"/>, evicts the least-recently-touched entry to make room.
    /// Called on both a fresh store (<see cref="RememberAsync"/>) and a cache hit that re-touches an
    /// existing one (<see cref="TryReuseAsync"/>) — a hit does not grow the map, but it does move
    /// the entry to the front of the LRU order, which is what keeps an actively reused pack cover
    /// safe from eviction.
    ///
    /// Synchronous, and that is the shape of the fix rather than a tidy-up: it no longer touches the
    /// store, so there is nothing left in it to cancel or to fail.
    /// </summary>
    private void Register(string url, string blobId)
    {
        _blobIdByUrl[url] = new CacheEntry(blobId, Interlocked.Increment(ref _touchClock));

        if (_blobIdByUrl.Count > _maxCachedCovers)
            EvictLeastRecentlyTouched();
    }

    /// <summary>
    /// Drops the least-recently-touched entry from memory, **and only from memory**.
    ///
    /// The persisted row deliberately survives, which is the whole point. Deleting it here bought
    /// back a ~110-byte record and cost the next video wanting that cover a fresh request to the
    /// image host plus a duplicate blob — a reclaim four orders of magnitude smaller than the spend,
    /// made against a promise given to a third party. A row *is* still dropped when it is genuinely
    /// worthless: <see cref="ForgetAsync"/>, from <see cref="TryReuseAsync"/>, when the blob has
    /// gone. Cold is not worthless.
    /// </summary>
    private void EvictLeastRecentlyTouched()
    {
        // A single-pass scan rather than a second index kept in step with the map. Note this runs on
        // *every* insert once the map is at its cap, not once per cap's worth — so it is one O(n)
        // walk per new cover URL once a session is over the cap. A walk of MaxCachedCovers is cheap
        // next to the paced HTTP fetch it sits beside; a second index would be a second thing to keep
        // true, which is the trade being made here.
        var victim = _blobIdByUrl.MinBy(pair => pair.Value.Touch).Key;

        if (victim is not null)
            _blobIdByUrl.TryRemove(victim, out _);
    }

    private void EvictOldestFailure()
    {
        var victim = _failures.MinBy(pair => pair.Value.At).Key;

        if (victim is not null)
            _failures.TryRemove(victim, out _);
    }

    private async Task<string?> ReadAsync(string url, CancellationToken ct)
    {
        if (_store is null)
            return null;

        try
        {
            var stored = await _store.GetAsync(Key(url), ct);
            return string.IsNullOrWhiteSpace(stored) ? null : stored;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Drops a URL from both the memory map and the persisted store, so a subsequent lookup treats
    /// it as never having been cached.
    ///
    /// One caller, and the narrowing is deliberate: a row is deleted only once its blob has actually
    /// gone, which is the one state that makes it worthless. LRU eviction used to come through here
    /// as well and no longer does — a cold entry leaves memory and is re-read from the store on the
    /// next lookup.
    /// </summary>
    private async Task ForgetAsync(string url, CancellationToken ct)
    {
        _blobIdByUrl.TryRemove(url, out _);
        if (_store is null)
            return;

        try
        {
            await _store.DeleteAsync(Key(url), ct);
        }
        catch (Exception)
        {
            // Same trade as RememberAsync: a stale entry costs one wasted existence check next time.
        }
    }

    /// <summary>
    /// Whether the blob is still there. Any failure counts as "gone", so a broken blob store costs a
    /// re-fetch rather than a dangling reference.
    /// </summary>
    private static async Task<bool> ExistsAsync(IBlobService blobs, string blobId, CancellationToken ct)
    {
        try
        {
            // GetBlobAsync opens the payload rather than merely reporting on it — there is no
            // exists-check on IBlobService — so the stream has to be disposed or every cache hit
            // leaks a file handle.
            var blob = await blobs.GetBlobAsync(blobId, ct);
            if (blob is null)
                return false;

            await blob.Value.Stream.DisposeAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The store key for a URL: hashed, because a cover URL is arbitrary-length attacker-influenced
    /// text and the store's key column is not ours to size. The URL is not recoverable from the key,
    /// which costs nothing — nothing here ever needs to enumerate the map by URL.
    /// </summary>
    internal static string Key(string url) =>
        KeyPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
}
