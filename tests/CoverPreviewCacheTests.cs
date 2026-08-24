using Cove.TorrentMetadata;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// The in-memory cache that holds previewed covers.
///
/// It is the one cover cache with no persistence and no blob store behind it, which is deliberate:
/// a previewed cover is an image no video references yet, and Cove deletes unreferenced blobs. So the
/// thing that has to be right here is the bound — an unbounded map of image bytes in a singleton is a
/// leak that only shows up on a long session over a big folder.
/// </summary>
public class CoverPreviewCacheTests
{
    private static byte[] Bytes(int size, byte fill = 0x11) => Enumerable.Repeat(fill, size).ToArray();

    [Fact]
    public void Hands_back_what_it_was_given()
    {
        var cache = new CoverPreviewCache();

        cache.Store("https://images.invalid/a.webp", [1, 2, 3], "image/webp");

        var held = cache.Get("https://images.invalid/a.webp");
        Assert.Equal([1, 2, 3], held!.Value.Bytes);
        // Kept verbatim, like the import path does: an animated WebP served as image/webp has to be
        // stored as one or it cannot render as one later.
        Assert.Equal("image/webp", held.Value.ContentType);
        Assert.Null(cache.Get("https://images.invalid/never-seen.jpg"));
    }

    [Fact]
    public void Evicts_the_oldest_entries_once_the_budget_is_spent()
    {
        var cache = new CoverPreviewCache(budgetBytes: 300);

        cache.Store("a", Bytes(100), "image/jpeg");
        cache.Store("b", Bytes(100), "image/jpeg");
        cache.Store("c", Bytes(100), "image/jpeg");
        Assert.Equal(300, cache.HeldBytes);

        cache.Store("d", Bytes(100), "image/jpeg");

        // A byte budget rather than an entry count, because covers span three orders of magnitude:
        // "keep 200 of them" is a few megabytes or a gigabyte depending on what is being reviewed.
        Assert.Equal(300, cache.HeldBytes);
        Assert.Equal(3, cache.Count);
        Assert.Null(cache.Get("a"));
        Assert.NotNull(cache.Get("d"));
    }

    [Fact]
    public void Keeps_the_cover_that_is_being_looked_at_over_the_one_that_is_not()
    {
        var cache = new CoverPreviewCache(budgetBytes: 300);

        cache.Store("a", Bytes(100), "image/jpeg");
        cache.Store("b", Bytes(100), "image/jpeg");
        cache.Store("c", Bytes(100), "image/jpeg");

        // Reopening the dialog on the same video is the normal thing a reviewer does, so a hit has to
        // count as a use. Evicting by insertion order alone would drop exactly the cover being
        // compared against the library's, and re-fetch it from the image host.
        Assert.NotNull(cache.Get("a"));
        cache.Store("d", Bytes(100), "image/jpeg");

        Assert.NotNull(cache.Get("a"));
        Assert.Null(cache.Get("b"));
    }

    [Fact]
    public void Refuses_an_entry_larger_than_the_whole_budget()
    {
        var cache = new CoverPreviewCache(budgetBytes: 300);
        cache.Store("a", Bytes(100), "image/jpeg");

        cache.Store("huge", Bytes(400), "image/jpeg");

        // Admitting it would evict everything else and still leave the cache over its limit. Note
        // this is not a per-entry cap in disguise: admission is bounded only by
        // CoverFetcher.MaxCoverBytes, because a large cover is the most expensive one to re-fetch.
        Assert.Null(cache.Get("huge"));
        Assert.NotNull(cache.Get("a"));
        Assert.Equal(100, cache.HeldBytes);
    }

    [Fact]
    public void Stops_holding_an_entry_that_has_become_a_blob()
    {
        var cache = new CoverPreviewCache();
        cache.Store("a", Bytes(100), "image/jpeg");

        Assert.True(cache.Remove("a"));

        // The read-through in TorrentApplyService drops the entry once the bytes are a blob: the
        // persistent cache answers for that URL from then on, and a second copy in memory is bytes
        // held for nothing.
        Assert.Null(cache.Get("a"));
        Assert.Equal(0, cache.HeldBytes);
        Assert.False(cache.Remove("a"));
    }

    [Fact]
    public void Replacing_an_entry_does_not_double_count_its_bytes()
    {
        var cache = new CoverPreviewCache(budgetBytes: 300);

        cache.Store("a", Bytes(100), "image/jpeg");
        cache.Store("a", Bytes(200), "image/jpeg");

        // Two previews of the same URL is the normal case — the batch page and the dialog both ask.
        // Counting the first one forever would evict live entries to make room for bytes that are
        // not there.
        Assert.Equal(200, cache.HeldBytes);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task Stays_consistent_under_concurrent_use()
    {
        // It is a singleton reached from every request, and a batch page opens a screenful of covers
        // at once, so the accounting is touched concurrently by construction.
        var cache = new CoverPreviewCache(budgetBytes: 10_000);

        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
        {
            for (var round = 0; round < 40; round++)
            {
                var key = $"cover-{(i + round) % 32}";
                cache.Store(key, Bytes(500, (byte)i), "image/jpeg");
                cache.Get(key);
                if (round % 5 == 0)
                    cache.Remove(key);
            }
        })));

        Assert.InRange(cache.HeldBytes, 0, 10_000);
        Assert.Equal(cache.Count * 500, cache.HeldBytes);
    }
}
