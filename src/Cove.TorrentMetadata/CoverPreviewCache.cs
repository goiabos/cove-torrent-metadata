namespace Cove.TorrentMetadata;

/// <summary>
/// Holds the bytes of covers that have been *previewed* but not imported, so looking at one costs the
/// image host a single request however many times it is shown.
///
/// It exists because the blob store is the wrong place for a preview. Cove reference-counts blobs and
/// deletes the unreferenced ones (<c>BlobReferenceSaveChangesInterceptor</c>), and a previewed cover
/// is by definition an image no video points at yet — so a preview-blob is either swept immediately
/// or, if we defended it, leaked forever. Memory is the honest home for something whose lifetime is
/// "until the user decides".
///
/// The pairing that makes this work is <see cref="TorrentApplyService"/> reading through it: a cover
/// the reviewer looked at and then ticked is written to the blob store from these bytes, with no
/// second request, and the entry is dropped because the blob now covers it. Between the two the
/// network is hit **at most once per URL**, in whichever order preview and import happen.
///
/// Memory-only is also why nothing here is persisted: an imported cover graduates to
/// <see cref="CoverCache"/>, which is persistent, and a merely-previewed one is additionally held by
/// the browser through the proxy's <c>Cache-Control</c>. A restart re-previewing a handful of covers
/// is a request or two, not the 1913-scene pack the persistent cache exists for.
/// </summary>
public sealed class CoverPreviewCache(long budgetBytes = CoverPreviewCache.DefaultBudgetBytes)
{
    /// <summary>
    /// Total bytes held before the oldest entries are evicted.
    ///
    /// A byte budget rather than an entry count, because cover sizes span three orders of magnitude:
    /// real ones include animated WebP loops of several megabytes, so "keep 200 entries" is either a
    /// few megabytes or a gigabyte depending on what the user happens to be reviewing.
    ///
    /// Unlike the pacing numbers this one is ours — it was never quoted to anyone — so it can be
    /// tuned. Note it is not a per-entry cap: admission is bounded only by
    /// <see cref="CoverFetcher.MaxCoverBytes"/>, because a large cover is the *most* expensive one to
    /// have to fetch again.
    /// </summary>
    public const long DefaultBudgetBytes = 64 * 1024 * 1024;

    private readonly long _budget = budgetBytes;
    private readonly Dictionary<string, LinkedListNode<Entry>> _byUrl = new(StringComparer.Ordinal);

    /// <summary>Least recently used first, so eviction is a walk from the head.</summary>
    private readonly LinkedList<Entry> _order = new();

    private readonly Lock _gate = new();

    private long _held;

    /// <summary>Bytes currently held. Exposed so a test can assert the budget actually bounds it.</summary>
    public long HeldBytes
    {
        get
        {
            lock (_gate)
                return _held;
        }
    }

    /// <summary>Entries currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _byUrl.Count;
        }
    }

    /// <summary>
    /// The cover held for this URL, or null. A hit is a use, so it moves to the back of the eviction
    /// queue — the cover a user keeps reopening is the last one worth dropping.
    /// </summary>
    public (byte[] Bytes, string ContentType)? Get(string url)
    {
        lock (_gate)
        {
            if (!_byUrl.TryGetValue(url, out var node))
                return null;

            _order.Remove(node);
            _order.AddLast(node);
            return (node.Value.Bytes, node.Value.ContentType);
        }
    }

    /// <summary>
    /// Remembers a fetched cover, evicting oldest-first until the budget holds again.
    ///
    /// An entry larger than the whole budget is not admitted: storing it would evict everything else
    /// and still leave the cache over its limit, which is worse than not caching it at all.
    /// </summary>
    public void Store(string url, byte[] bytes, string contentType)
    {
        if (bytes.Length == 0 || bytes.LongLength > _budget)
            return;

        lock (_gate)
        {
            RemoveLocked(url);

            var node = _order.AddLast(new Entry(url, bytes, contentType));
            _byUrl[url] = node;
            _held += bytes.LongLength;

            while (_held > _budget && _order.First is { } oldest)
                RemoveLocked(oldest.Value.Url);
        }
    }

    /// <summary>
    /// Drops an entry. Called once its bytes have become a blob, because the persistent cache now
    /// answers for that URL and holding a second copy in memory buys nothing.
    /// </summary>
    public bool Remove(string url)
    {
        lock (_gate)
            return RemoveLocked(url);
    }

    private bool RemoveLocked(string url)
    {
        if (!_byUrl.Remove(url, out var node))
            return false;

        _order.Remove(node);
        _held -= node.Value.Bytes.LongLength;
        return true;
    }

    private sealed record Entry(string Url, byte[] Bytes, string ContentType);
}
