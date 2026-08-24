using Cove.Plugins;

namespace Cove.TorrentMetadata;

/// <summary>
/// What a torrent's tag list looked like when it was applied to a video, so that "the tracker
/// re-tagged this torrent" can be told apart from "you applied it with the defaults".
///
/// Without this the two are indistinguishable, and the second is the normal case. A default bulk
/// apply creates no tags, so every tag the library does not already know stays outstanding forever —
/// measured against the real corpus, 692 of 709 matched rows (97.6%) were left with something
/// outstanding the moment they were applied, which made `updated` mean nothing and kept almost every
/// row past the "Hide applied" filter. The signal that replaced it was drowned by that noise.
///
/// The baseline is the torrent's **raw tag-list count**, deliberately, rather than the number of
/// content tags it offers a particular video. Content-tag counts move when the library gains a
/// performer or a tag alias, because classification and the performer split both read library state —
/// so a library edit would read as a torrent edit. The raw count is a property of the .torrent file
/// alone, which is exactly the thing "has this torrent changed" is asking about.
///
/// Stored one key per (video, torrent) for the same reason <see cref="CoverCache"/> is: a single
/// value holding the whole map would be rewritten on every apply, turning a bulk run into a
/// read-modify-write race against itself.
/// </summary>
public sealed class AppliedTorrentBaseline
{
    public const string KeyPrefix = "applied:";

    private IExtensionStore? _store;

    public void AttachStore(IExtensionStore store) => _store = store;

    internal static string KeyFor(int videoId, string torrentId) =>
        $"{KeyPrefix}{videoId}:{torrentId}";

    /// <summary>
    /// Remembers the tag-list size this torrent had when it was applied to this video.
    ///
    /// Overwrites rather than skipping an existing entry: re-applying a torrent means the reviewer has
    /// seen its current list, so that list becomes the new baseline. Leaving the old one would keep the
    /// row reading "updated" after the update had been dealt with.
    /// </summary>
    public async Task RecordAsync(int videoId, string torrentId, int tagListCount, CancellationToken ct = default)
    {
        if (_store is null || string.IsNullOrWhiteSpace(torrentId))
            return;

        await _store.SetAsync(
            KeyFor(videoId, torrentId),
            tagListCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ct);
    }

    /// <summary>
    /// Every baseline, in one store read.
    ///
    /// The overview needs a baseline per applied row, and the store's only bulk read is everything at
    /// once — so this is one query per overview rather than one per row. Entries that do not parse are
    /// skipped rather than thrown on: a hand-edited store must not take the batch page down.
    /// </summary>
    public async Task<IReadOnlyDictionary<(int VideoId, string TorrentId), int>> LoadAsync(
        CancellationToken ct = default)
    {
        var baselines = new Dictionary<(int, string), int>();
        if (_store is null)
            return baselines;

        foreach (var (key, value) in await _store.GetAllAsync(ct))
        {
            if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                continue;

            // "applied:{videoId}:{torrentId}" — split once from the left so a torrent id containing a
            // colon stays whole.
            var rest = key[KeyPrefix.Length..];
            var separator = rest.IndexOf(':');
            if (separator <= 0 || separator == rest.Length - 1)
                continue;

            if (!int.TryParse(rest[..separator], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var videoId))
                continue;

            if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var count))
                continue;

            baselines[(videoId, rest[(separator + 1)..])] = count;
        }

        return baselines;
    }
}
