using System.Collections.Concurrent;
using Cove.Core.Interfaces;

namespace Cove.TorrentMetadata;

/// <summary>
/// Why a cover was not produced. The two callers turn this into different things — a status code for
/// the proxy, a sentence for the reviewer — so the *kind* is carried rather than left to be inferred
/// from the message text.
/// </summary>
public enum CoverRefusal
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>Not a URL this could ever fetch. The caller got it wrong, not the image host.</summary>
    Malformed,

    /// <summary>A host the operator has not named, or an address on this server's own network.</summary>
    NotAllowed,

    /// <summary>Asked for and not got. Transient or hostile; the one generic reason.</summary>
    Unavailable,

    /// <summary>Not sent at all, because the pacing gate said not yet. <see cref="CoverResolution.RetryAfter"/> says when.</summary>
    Throttled,
}

/// <summary>
/// A cover, however it was come by — or the reason there is not one.
/// </summary>
/// <param name="BlobId">A blob already holding this image, when the persistent cache answered.</param>
/// <param name="Bytes">The image itself, when it came from memory or from the network.</param>
/// <param name="ContentType">The media type, set whenever <paramref name="Bytes"/> is.</param>
/// <param name="Skipped">Why there is nothing, phrased for the reviewer. Null on success.</param>
/// <param name="RetryAfter">Set on <see cref="CoverRefusal.Throttled"/> only: how long to leave it.</param>
/// <param name="Refusal">Which kind of nothing this is.</param>
public readonly record struct CoverResolution(
    string? BlobId,
    byte[]? Bytes,
    string? ContentType,
    string? Skipped,
    TimeSpan? RetryAfter,
    CoverRefusal Refusal)
{
    /// <summary>True when there is a cover here, in one form or the other.</summary>
    public bool Found => BlobId is not null || Bytes is not null;
}

/// <summary>What an import got, and whether the preview cache is still holding a copy of it.</summary>
/// <param name="BlobId">The stored blob, on success.</param>
/// <param name="Skipped">Why not, phrased for the reviewer.</param>
public readonly record struct CoverStorage(string? BlobId, string? Skipped);

/// <summary>
/// The one place the cover-resolution sequence lives: allowlist, then the blob a sibling scene already
/// imported, then bytes already in memory, then a recent failure, then — at most once per URL at a
/// time — the network.
///
/// It exists because that sequence used to be written twice, in <see cref="CoverProxyService"/> and in
/// <see cref="TorrentApplyService"/>, and the two copies had already drifted three different ways
///. Each drift was invisible in review because each copy read correctly on its own:
///
/// - The import negative-cached a *limiter* refusal. `CoverCache` is a singleton, so one bulk run
///   tripping a host's 60s breaker recorded every URL attempted in that minute as unfetchable for ten
///   minutes — and the batch page reads the same cache, so its thumbnails 502'd for ten minutes over a
///   sixty-second pause. Nothing was learned about those covers; they were never asked for.
/// - Both copies checked the negative cache **before** the preview cache, so a cover whose bytes were
///   already in memory was refused for having failed earlier. The order below is deliberate: bytes in
///   hand beat a remembered failure, because the failure is a claim about the network and the bytes
///   are not.
/// - The import dropped the preview entry before the save that could still fail, which loses the bytes
///   and contradicts the "one request per URL" promise. The entry is now dropped by the caller, after
///   its save landed — see <see cref="ForgetPreview"/>.
///
/// **A singleton.** The in-flight map is the whole point and only means anything shared across
/// requests; the caches it sits in front of are singletons for the same reason.
/// </summary>
public sealed class CoverResolver(
    CoverPreviewCache? previews = null,
    IHttpClientFactory? httpClients = null,
    CoverHostAllowlist? coverHosts = null,
    CoverCache? coverCache = null)
{
    /// <summary>
    /// The fetches currently in flight, keyed by URL, so two callers asking at the same moment make
    /// one request between them.
    ///
    /// The caches below already made "one request per URL" true *eventually* — the second scene of a
    /// pack hits the persistent cache because the first one finished first. They could not make it
    /// true *concurrently*: two browser tabs, or a preview racing the apply it triggered, both miss
    /// every cache and both go to the host. That is the shape the tracker's staff called spamming,
    /// and it is the one shape a cache cannot fix by construction.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<CoverBytes>>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves <paramref name="url"/> to a cover, going no further down the list than it has to.
    ///
    /// <paramref name="blobs"/> is per-request and may be null, in which case the persistent
    /// blob-reuse step is skipped rather than failing — a caller with no blob store can still preview.
    ///
    /// <paramref name="maxWait"/> is the caller's ceiling on time spent waiting, both at the rate
    /// limiter's gate and behind another caller's in-flight fetch of this same URL. A preview passes
    /// <see cref="CoverRateLimiter.PreviewMaxWait"/> — that field owns the reason a preview refuses
    /// early rather than waiting; null takes <see cref="CoverRateLimiter.MaxWait"/>.
    /// </summary>
    public async Task<CoverResolution> ResolveAsync(
        string? url,
        IBlobService? blobs = null,
        TimeSpan? maxWait = null,
        CancellationToken ct = default)
    {
        // A bare path parses as an absolute *file* URI on Unix, so "is it absolute" alone would let one
        // through to be refused later for the wrong reason.
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return Refused(CoverRefusal.Malformed, "Cover skipped: the torrent's cover URL is not a usable URL.");
        }

        // Before anything is sent, and failing closed on an unwired allowlist: this is the only thing
        // standing in front of a request whose URL came out of a .torrent downloaded from a tracker.
        if (coverHosts is null)
            return Refused(CoverRefusal.NotAllowed, "Cover skipped: no cover hosts are configured.");
        if (!coverHosts.Allows(uri))
            return Refused(CoverRefusal.NotAllowed, coverHosts.Explain(uri));

        // A cover a sibling scene already imported is on disk. This is what makes a 1913-scene pack
        // one download rather than 1913, and it is checked first because it costs no bytes at all.
        if (coverCache is not null && blobs is not null
            && await coverCache.TryReuseAsync(url, blobs, ct) is { } reused)
        {
            return new CoverResolution(reused, null, null, null, null, CoverRefusal.None);
        }

        // Then bytes already in memory — the reviewer has usually just looked at this cover, since the
        // dialog previews it through this same sequence. Ahead of the negative cache on purpose: a
        // remembered failure is a claim about the network, and these bytes are not the network.
        if (previews?.Get(url) is { } previewed)
            return new CoverResolution(null, previewed.Bytes, previewed.ContentType, null, null, CoverRefusal.None);

        // A dead cover on a pack must cost one request, not one per scene — and the batch page reads
        // this same cache, so it is also what stops a page of dead thumbnails re-asking on every render.
        if (coverCache?.RecentFailure(url) is { } replayed)
            return Refused(CoverRefusal.Unavailable, replayed);

        if (httpClients is null)
            return Refused(CoverRefusal.Unavailable, "Cover skipped: image fetching is not available in this context.");

        var fetched = await FetchOnceAsync(url, uri, maxWait, ct);

        if (fetched.Bytes is not null && fetched.ContentType is not null)
        {
            // Held for whoever asks next, in either direction: a previewed cover that is then ticked
            // costs no request, and an imported one that is then rendered costs none either.
            previews?.Store(url, fetched.Bytes, fetched.ContentType);
            return new CoverResolution(null, fetched.Bytes, fetched.ContentType, null, null, CoverRefusal.None);
        }

        var reason = fetched.Skipped ?? CoverFetcher.Unfetchable;

        // Refused by the limiter rather than by the host, so nothing was learned about this cover and
        // remembering it as dead would turn a moment's pacing into ten minutes of a missing image.
        // The import path used to do exactly that; it is the worst of the three drifts.
        if (fetched.RetryAfter is { } retryAfter)
            return Refused(CoverRefusal.Throttled, reason, retryAfter);

        coverCache?.RememberFailure(url, reason);
        return Refused(CoverRefusal.Unavailable, reason);
    }

    /// <summary>
    /// The same resolution, followed through to a blob — what an import wants.
    ///
    /// The blob write lives here rather than in the caller so that "fetched bytes become a blob and the
    /// persistent cache learns about it" is one step that cannot be half-done by one path and not the
    /// other. The preview entry is deliberately *not* dropped here: see <see cref="ForgetPreview"/>.
    /// </summary>
    public async Task<CoverStorage> StoreAsync(string? url, IBlobService? blobs, CancellationToken ct = default)
    {
        if (blobs is null)
            return new CoverStorage(null, "Cover skipped: image fetching is not available in this context.");

        var resolved = await ResolveAsync(url, blobs, maxWait: null, ct);

        if (resolved.BlobId is { } existing)
            return new CoverStorage(existing, null);

        if (resolved.Bytes is null || resolved.ContentType is null)
            return new CoverStorage(null, resolved.Skipped ?? CoverFetcher.Unfetchable);

        var stored = await StoreBytesAsync(resolved.Bytes, resolved.ContentType, blobs, ct);
        if (stored is null)
            return new CoverStorage(null, CoverFetcher.Unfetchable);

        if (coverCache is not null && url is not null)
            await coverCache.RememberAsync(url, stored, ct);

        return new CoverStorage(stored, null);
    }

    /// <summary>
    /// Drops the in-memory copy of a cover that is now a blob.
    ///
    /// Called by the importer **after its save has landed**, never before. The entry used to be dropped
    /// as soon as the blob was written, which is one failed <c>SaveChanges</c> away from an orphaned
    /// blob, lost bytes, and a re-download that the "at most one request per URL" promise says will not
    /// happen. Nothing is lost by keeping it a moment longer — it is the cheapest answer there
    /// is until the persistent cache can answer instead.
    /// </summary>
    public bool ForgetPreview(string? url) => url is not null && (previews?.Remove(url) ?? false);

    /// <summary>
    /// One outbound fetch per URL at a time, however many callers want it.
    ///
    /// Two details carry the weight. The shared fetch runs on <see cref="CancellationToken.None"/>, so
    /// one caller giving up — a browser aborting on unmount is the common case — does not cancel a
    /// request the other callers are still waiting on, and does not abandon the rate limiter's permit
    /// mid-transfer. And a <em>joiner</em> waits only its own <paramref name="maxWait"/>: a preview must
    /// not inherit an import's twenty seconds just because the import asked first, which is the whole
    /// reason those are two different numbers.
    ///
    /// A joiner, and only a joiner. <paramref name="maxWait"/> bounds how long a caller queues for
    /// someone else's answer, never how long its own fetch may take — those were one number once, and
    /// it made the caller that started a slow fetch refuse itself (see below).
    ///
    /// The <see cref="Lazy{T}"/> is not decoration. The fetch must not be *started* until its entry is
    /// in the map, because the entry is removed when the fetch finishes — start it first and a fast
    /// failure removes an entry that has not been added yet, stranding the completed task in the map
    /// for every later caller to join. `GetOrAdd` hands back the winner, only the winner is ever
    /// asked for its <c>Value</c>, and so the fetch starts exactly once, after insertion.
    /// </summary>
    private async Task<CoverBytes> FetchOnceAsync(string url, Uri uri, TimeSpan? maxWait, CancellationToken ct)
    {
        Lazy<Task<CoverBytes>>? holder = null;
        holder = new Lazy<Task<CoverBytes>>(() => RunAsync(url, uri, maxWait, holder!));

        var entry = _inFlight.GetOrAdd(url, holder);
        var shared = entry.Value;

        // The caller that *started* this fetch waits it out. Its `maxWait` is a ceiling on queueing at
        // the limiter's gate — CoverFetcher already applies it there — and reusing it here as a ceiling
        // on the transfer refused the one caller waiting on a request that had gone out and succeeded.
        // A cover slower than its caller's wait is the ordinary case, not a race: a 5 MB animated GIF
        // takes seconds and PreviewMaxWait is two, so every cold cover on such a host was refused with
        // "already being asked" — by itself, since GetOrAdd hands the winner back its own Lazy. What
        // bounds this is what has always bounded a transfer: the cover client's own 60s timeout.
        if (ReferenceEquals(entry, holder))
            return await shared.WaitAsync(ct);

        try
        {
            return await shared.WaitAsync(maxWait ?? CoverRateLimiter.MaxWait, ct);
        }
        catch (TimeoutException)
        {
            // Someone else's fetch of this exact cover is still running. Reported as throttling rather
            // than as a failure because it is neither the cover's fault nor permanent — the answer is
            // to come back, and by then a cache will answer.
            return new CoverBytes(
                null,
                null,
                $"Cover skipped: {uri.Host} is already being asked for this image.",
                CoverRateLimiter.MinimumInterval);
        }
    }

    /// <summary>
    /// The shared fetch, which clears its own entry on the way out.
    ///
    /// Removed by identity rather than by key, so a fetch finishing cannot evict the *next* fetch of
    /// the same URL that a later caller has just started.
    /// </summary>
    private async Task<CoverBytes> RunAsync(string url, Uri uri, TimeSpan? maxWait, Lazy<Task<CoverBytes>> holder)
    {
        try
        {
            return await CoverFetcher.FetchAsync(uri, httpClients!, coverHosts!, maxWait, CancellationToken.None);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<CoverBytes>>>(url, holder));
        }
    }

    /// <summary>
    /// Writes bytes to the blob store, or null if it would not take them.
    ///
    /// Swallowed rather than thrown because <c>ApplyAsync</c> is mid-flight by this point: a blob store
    /// that is full must cost the cover, not the tags the user just approved.
    /// </summary>
    private static async Task<string?> StoreBytesAsync(
        byte[] bytes, string contentType, IBlobService blobs, CancellationToken ct)
    {
        try
        {
            using var buffer = new MemoryStream(bytes, writable: false);
            return await blobs.StoreBlobAsync(buffer, contentType, ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static CoverResolution Refused(CoverRefusal refusal, string reason, TimeSpan? retryAfter = null) =>
        new(null, null, null, reason, retryAfter, refusal);
}
