namespace Cove.TorrentMetadata;

/// <summary>One video file inside an indexed torrent, and the metadata that file would contribute.</summary>
public sealed record TorrentIndexEntry(TorrentVideoFile Video, TorrentRelease Torrent)
{
    /// <summary>See <see cref="TorrentRelease.FanOut"/> — greater than one means shared pack metadata.</summary>
    public int FanOut => Torrent.FanOut;
}

/// <summary>
/// Which of two entries describing the same file wins.
///
/// There are three places that have to answer this — <see cref="TorrentIndex.Find"/> resolving a size
/// or basename collision, <c>TorrentBatchService.ApplyAsync</c> resolving a (torrent, file) row key,
/// and <c>TorrentMatchService</c>'s forced branch resolving a torrent the caller named — and they used
/// to answer it three different ways, one of them not at all. The batch overview would build a row
/// from the lowest-fan-out entry and clicking that row could open the other one.
///
/// **Only the first key means anything.** Lowest fan-out wins because a single-scene torrent's
/// metadata is about this video while a pack's is the union across its whole release — measured
/// against the corpus, that resolved all 20 of the library's real collisions correctly, every time
/// picking the torrent whose filename matched the library's. It is load-bearing: the rule is what
/// keeps a pack's union from being read as one video's metadata.
///
/// Everything after it is arbitrary, and exists only so the answer is the *same* arbitrary one every
/// time. The entries reach a comparison through dictionary enumeration, whose order .NET does not
/// define, so without a total order the choice moves between runs — and it is a user-visible choice:
/// which torrent's metadata a video is offered.
///
/// **The order is total, and the last key is what makes it so.** The four keys before it can all tie:
/// two video files inside one pack share its fan-out, its name and its tracker id, and a pack holding
/// `Disc1/01.mp4` beside `Disc2/01.mp4` shares the basename too. Video path is unique by construction
/// within a torrent, so it separates them.
///
/// One case it still cannot separate: two *different* copies of one tracker id — a re-download after
/// the tracker re-tagged the release, kept under a second filename. Nothing here says which is newer,
/// and the corpus offers no evidence for choosing a key that would: of 52 ids appearing in more than
/// one file, every pair is byte-identical, so the content de-duplication in `ReloadIndex` collapses
/// them before this comparer ever sees them.
/// </summary>
public sealed class TorrentEntryPreference : IComparer<TorrentIndexEntry>
{
    public static TorrentEntryPreference Instance { get; } = new();

    private TorrentEntryPreference()
    {
    }

    /// <summary>The winner among <paramref name="entries"/>, which must hold at least one.</summary>
    public static TorrentIndexEntry Best(IEnumerable<TorrentIndexEntry> entries) =>
        entries.Min(Instance)
        ?? throw new InvalidOperationException("No entries to choose between.");

    public int Compare(TorrentIndexEntry? x, TorrentIndexEntry? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        var byFanOut = x.FanOut.CompareTo(y.FanOut);
        if (byFanOut != 0)
            return byFanOut;

        // Ordinal throughout: this is a stable ordering, not a human-facing one, and a culture-aware
        // comparison would move the winner with the host's locale.
        var byTorrent = string.CompareOrdinal(x.Torrent.Name, y.Torrent.Name);
        if (byTorrent != 0)
            return byTorrent;

        var byFile = string.CompareOrdinal(x.Video.Basename, y.Video.Basename);
        if (byFile != 0)
            return byFile;

        // Separates two uploads of the same release under the same name — the re-upload case the size
        // collision exists for. Null sorts first and consistently.
        var byTorrentId = string.CompareOrdinal(x.Torrent.TorrentId, y.Torrent.TorrentId);
        if (byTorrentId != 0)
            return byTorrentId;

        // The last resort, and the only key that is unique by construction: two files in one torrent
        // cannot share a path. Every tie measured in the real corpus is this case — a pack holding
        // `Disc1/01.mp4` beside `Disc2/01.mp4`, which the four keys above cannot separate because both
        // entries carry the *same* release. 53 basename buckets and 4 size buckets over 3,202 torrents
        // and 139,142 video files, and not one tie between two different torrents.
        //
        // Without it the comparer is not the total order its own summary promises, and `Min` falls back
        // on dictionary enumeration order — so which of a pack's two same-named scenes a row displays
        // could move between rescans of an unchanged folder. The metadata is identical either way,
        // which is why this reads as cosmetic; it is fixed because a comparer documented as total and
        // silently not is the kind of thing later work builds on.
        return string.CompareOrdinal(x.Video.Path, y.Video.Path);
    }
}

/// <summary>
/// A finished lookup, published by reference and never mutated afterwards.
///
/// Plain dictionaries and plain lists are correct here precisely because nothing writes to one once it
/// has been handed to <see cref="TorrentIndex"/>: the only mutation is on the builder that produced it,
/// before any reader could hold it. Concurrency is handled by replacing the whole object, not by making
/// its parts thread-safe — a concurrent collection would have made each individual operation safe while
/// leaving a reader free to observe a half-filled index, which is the failure that actually mattered.
/// </summary>
internal sealed class TorrentIndexSnapshot(
    Dictionary<long, List<TorrentIndexEntry>> bySize,
    Dictionary<string, List<TorrentIndexEntry>> byBasename,
    int count)
{
    public static readonly TorrentIndexSnapshot Empty =
        new([], new Dictionary<string, List<TorrentIndexEntry>>(StringComparer.OrdinalIgnoreCase), 0);

    public Dictionary<long, List<TorrentIndexEntry>> BySize { get; } = bySize;

    public Dictionary<string, List<TorrentIndexEntry>> ByBasename { get; } = byBasename;

    public int Count { get; } = count;
}

/// <summary>
/// Accumulates a replacement index off to the side of the one being queried.
///
/// Deliberately mutable and deliberately not thread-safe: one builder belongs to one thread, and the
/// only sharing happens at <see cref="TorrentIndex.Replace"/>, which publishes the finished result as a
/// single reference assignment. That is what makes a rebuild cost one pass rather than one copy per
/// torrent, and what stops a reader ever seeing a partially rebuilt index.
///
/// A builder is single-use. <see cref="TorrentIndex.Replace"/> takes ownership of its dictionaries
/// rather than copying them, so a later <see cref="Add"/> would be writing into a live index behind the
/// backs of its readers; that throws instead.
/// </summary>
public sealed class TorrentIndexBuilder
{
    private readonly Dictionary<long, List<TorrentIndexEntry>> _bySize = [];
    private readonly Dictionary<string, List<TorrentIndexEntry>> _byBasename = new(StringComparer.OrdinalIgnoreCase);
    private int _count;
    private bool _published;

    /// <summary>Video files accumulated so far.</summary>
    public int Count => _count;

    /// <summary>
    /// Adds every video in <paramref name="torrent"/>. Returns false for a payload with no video —
    /// an image set, comic or audio-only release — which has nothing to match against.
    /// </summary>
    public bool Add(TorrentRelease torrent)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        if (_published)
        {
            throw new InvalidOperationException(
                "This builder has already been published into a TorrentIndex, which took ownership of its "
                + "contents. Adding now would mutate a live index behind its readers. Build a new one.");
        }

        if (!torrent.HasVideo)
            return false;

        foreach (var video in torrent.Videos)
        {
            var entry = new TorrentIndexEntry(video, torrent);
            Bucket(_bySize, video.Length).Add(entry);
            Bucket(_byBasename, video.Basename).Add(entry);
            _count++;
        }

        return true;
    }

    private static List<TorrentIndexEntry> Bucket<TKey>(Dictionary<TKey, List<TorrentIndexEntry>> buckets, TKey key)
        where TKey : notnull
    {
        if (!buckets.TryGetValue(key, out var bucket))
            buckets[key] = bucket = [];

        return bucket;
    }

    /// <summary>Hands the accumulated state over, and refuses any further <see cref="Add"/>.</summary>
    internal TorrentIndexSnapshot Publish()
    {
        _published = true;
        return new TorrentIndexSnapshot(_bySize, _byBasename, _count);
    }

    /// <summary>A builder pre-loaded with everything <paramref name="snapshot"/> holds, copied out of it.</summary>
    internal static TorrentIndexBuilder From(TorrentIndexSnapshot snapshot)
    {
        var builder = new TorrentIndexBuilder();

        foreach (var (size, entries) in snapshot.BySize)
            builder._bySize[size] = [.. entries];
        foreach (var (basename, entries) in snapshot.ByBasename)
            builder._byBasename[basename] = [.. entries];
        builder._count = snapshot.Count;

        return builder;
    }
}

/// <summary>
/// Lookup from a local video file to the torrent metadata describing it.
///
/// Indexed per video file rather than per torrent: a pack has to be able to match each of its scenes
/// independently, and keying on one "main" file per torrent would let a fifty-scene release match a
/// single video. Every video in a payload therefore gets its own entry pointing at the shared torrent.
///
/// File size is the primary key. It is exact, already indexed on <c>VideoFile.Size</c>, and unique in
/// practice — across the sample corpus every video file had a distinct length. Basename is a secondary
/// key for the case where a file was remuxed or renamed but kept its name, and is only consulted when
/// size finds nothing.
///
/// Piece hashes are deliberately unused: pieces span file boundaries in a multi-file torrent, so
/// verifying one file means reconstructing the whole offset layout for no gain over an exact size match.
///
/// Concurrency: safe for readers and writers together, and for writers against each other. The contents
/// are an immutable <see cref="TorrentIndexSnapshot"/> held behind one volatile reference; every write
/// builds a whole replacement and swaps it in. A reader therefore sees exactly one build — the one
/// before a rebuild or the one after it, never a partial state — and two writers cannot interleave,
/// because the read-build-publish cycle runs under a write gate.
///
/// This is registered as a singleton and rebuilt from two endpoints (reload, and the tail of every
/// upload), so concurrent writes are reachable rather than theoretical. It used to mutate in place —
/// non-atomic <c>Count++</c>, plain lists inside a concurrent dictionary, and a clear-then-refill that
/// let a match land against an empty index mid-rebuild. Do not reintroduce any of those by making a
/// part of the snapshot mutable again; the safety here is in the swap, not in the collections.
/// </summary>
public sealed class TorrentIndex
{
    /// <summary>Serialises writers so a read-build-publish cycle cannot interleave with another one.</summary>
    private readonly Lock _writeGate = new();

    private volatile TorrentIndexSnapshot _snapshot = TorrentIndexSnapshot.Empty;

    public int Count => _snapshot.Count;

    /// <summary>
    /// Swaps in everything <paramref name="builder"/> accumulated, discarding the current contents.
    ///
    /// This is the bulk path, and the one a rebuild should use: it costs a single reference assignment
    /// however large the index is, and the builder was filled without any reader being able to see it.
    /// Takes ownership — the builder refuses further additions afterwards.
    /// </summary>
    public void Replace(TorrentIndexBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        lock (_writeGate)
            _snapshot = builder.Publish();
    }

    /// <summary>
    /// Adds every video in <paramref name="torrent"/>. Returns false for a payload with no video —
    /// an image set, comic or audio-only release — which has nothing to match against.
    ///
    /// **Nothing in the extension calls this.** Every production path goes through
    /// <see cref="Replace"/>: a reload builds the whole index to the side, and an upload triggers a
    /// reload. It is kept because it is how a test states "the index holds this torrent" without a
    /// folder and a parse behind it, and it is what <c>TorrentMetadataExtension.AddToIndex</c> exposes
    /// for the same purpose. Read a caller of it as a fixture, not as an ingest path.
    ///
    /// Copies the whole index to add one torrent, because the copy is what keeps the published snapshot
    /// immutable. That is fine for a single ingest and wrong for a rebuild: filling a
    /// <see cref="TorrentIndexBuilder"/> and calling <see cref="Replace"/> is linear where a loop over
    /// this is quadratic — which is the other half of why no production path uses it.
    /// </summary>
    public bool Add(TorrentRelease torrent)
    {
        ArgumentNullException.ThrowIfNull(torrent);

        if (!torrent.HasVideo)
            return false;

        lock (_writeGate)
        {
            var builder = TorrentIndexBuilder.From(_snapshot);
            builder.Add(torrent);
            _snapshot = builder.Publish();
        }

        return true;
    }

    /// <summary>
    /// Finds the metadata for a local file. Size is tried first and basename only as a fallback, so a
    /// coincidental name collision can never override an exact size match.
    /// </summary>
    public TorrentIndexEntry? Find(long? sizeBytes, string? path)
    {
        // Read the snapshot once: the size lookup and the basename fallback have to be answered by the
        // same index, or a rebuild landing between them could report neither.
        var snapshot = _snapshot;

        if (sizeBytes is { } size && snapshot.BySize.TryGetValue(size, out var bySize) && bySize.Count > 0)
        {
            // Two torrents can describe the same file (a re-upload); prefer the one whose metadata is
            // about a single scene, since pack metadata is a union and mostly wrong for one video.
            // `TorrentEntryPreference` is that rule, and it is shared with the two other places that
            // resolve the same collision.
            return TorrentEntryPreference.Best(bySize);
        }

        if (!string.IsNullOrEmpty(path))
        {
            // `Path.GetFileName` here and a split on `/` in `TorrentVideoFile.Basename`, and the
            // asymmetry is deliberate rather than an oversight. This argument is a *library* path, so
            // it carries whatever separator the host's filesystem uses and needs the platform's rule;
            // the other side is a path out of a bencode `info.files` list, which BEP-3 joins with `/`
            // on every platform, so the platform's rule is the wrong one there — on Unix it would
            // leave a Windows-authored `a\b.mp4` whole. They agree on every real input because each
            // is the correct rule for its own input, which is what makes the two keys comparable.
            var basename = System.IO.Path.GetFileName(path);
            if (snapshot.ByBasename.TryGetValue(basename, out var byName) && byName.Count > 0)
                return TorrentEntryPreference.Best(byName);
        }

        return null;
    }

    /// <summary>Every indexed video file, for batch views that iterate the whole torrent folder.</summary>
    public IReadOnlyList<TorrentIndexEntry> All() => [.. _snapshot.BySize.Values.SelectMany(entries => entries)];
}
