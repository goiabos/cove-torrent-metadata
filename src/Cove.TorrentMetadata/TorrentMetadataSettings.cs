using Cove.Plugins;

namespace Cove.TorrentMetadata;

/// <summary>
/// The extension's user-configurable settings, held in memory and persisted through the host's
/// per-extension key-value store.
///
/// Kept as a singleton and read on every proposal rather than cached per request, so changing the
/// setting takes effect on the next dialog without a restart.
/// </summary>
public sealed class TorrentMetadataSettings
{
    /// <summary>
    /// Every setting at once, so a reader gets one coherent answer rather than three that may be from
    /// either side of somebody's save.
    ///
    /// A record held in one field, rather than three fields, and that is the shape of the fix: a
    /// reference assignment publishes all three at once, so no reader can observe a half-applied PUT.
    /// Three independently assigned properties could hand the settings panel the new tag style beside
    /// the old cover-host list, and the panel treats what it reads back as the document it just
    /// saved.
    /// </summary>
    /// <param name="TagNameStyle">How tags that do not yet exist in the library are spelled when
    /// created.</param>
    /// <param name="CoverHosts">Hosts a cover image may be fetched from. Empty until the operator
    /// configures it, which means covers do not import until then — see
    /// <see cref="CoverHostAllowlist"/> for why that is the shipped default rather than a list of
    /// somebody's image hosts.</param>
    /// <param name="SourceFolders">Folders torrents are read from, in addition to the extension's own.
    /// Read-only sources: nothing is ever written into one. The extension's own folder is where uploads
    /// land, and it is not in this list because it is not the operator's to move.</param>
    public sealed record State(
        TagNameStyle TagNameStyle,
        IReadOnlyList<string> CoverHosts,
        IReadOnlyList<string> SourceFolders);

    private IExtensionStore? _store;

    /// <summary>
    /// Serialises write-then-assign, which was the actual defect rather than the partial PUT the
    /// review named.
    ///
    /// Each setter persists before it assigns, deliberately, so that a failed write leaves the store
    /// authoritative. That contract holds per call and did not hold *between* calls: two saves of the
    /// same setting could write A then B to the store and assign B then A in memory, leaving the two
    /// permanently disagreeing until a restart re-read the store. The gate makes the pair one step, so
    /// whichever write lands last is also the value in memory.
    ///
    /// A <see cref="SemaphoreSlim"/> rather than a <c>lock</c> because the body awaits the store —
    /// <see cref="TorrentIndex"/>'s equivalent gate is a plain lock precisely because its body does
    /// not.
    /// </summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private State _current = new(TagNameStyle.TitleCase, [], []);

    /// <summary>
    /// Every setting, as one value. Read the reference once and the three agree with each other; read
    /// the three properties below separately and they need not.
    /// </summary>
    public State Current => _current;

    /// <inheritdoc cref="State.TagNameStyle"/>
    public TagNameStyle TagNameStyle => _current.TagNameStyle;

    /// <inheritdoc cref="State.CoverHosts"/>
    public IReadOnlyList<string> CoverHosts => _current.CoverHosts;

    /// <inheritdoc cref="State.SourceFolders"/>
    public IReadOnlyList<string> SourceFolders => _current.SourceFolders;

    public void AttachStore(IExtensionStore store) => _store = store;

    /// <summary>
    /// Loads persisted values. Any failure leaves the defaults in place rather than throwing.
    ///
    /// Built to the side and published in one assignment, so "the defaults" means all three of them.
    /// Assigning as each key was read left a failure part-way through with a mix of loaded and default
    /// values, which is not what this method has always claimed to do.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_store is null)
            return;

        try
        {
            var loaded = new State(
                TagNameStyler.Parse(await _store.GetAsync(TagNameStyler.SettingKey, ct)),
                CoverHostSetting.Parse(await _store.GetAsync(CoverHostSetting.SettingKey, ct)),
                SourceFolderSetting.Parse(await _store.GetAsync(SourceFolderSetting.SettingKey, ct)));

            await _writeGate.WaitAsync(ct);
            try
            {
                _current = loaded;
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (Exception)
        {
            // A settings read must never stop the extension from loading; defaults are always usable.
        }
    }

    /// <summary>
    /// Applies every field the caller sent, as one serialised unit, and returns the state that
    /// resulted from it.
    ///
    /// A null field is one the request did not carry and is left alone — a PUT changing the tag style
    /// must not reset the cover hosts to their default, because the two are separate controls on one
    /// endpoint.
    ///
    /// The returned <see cref="State"/> is read under the same gate that wrote it, so it is the
    /// document this save produced rather than whatever a later save has since made true. That is the
    /// read-back the settings panel assumes it is getting.
    ///
    /// **This is not a transaction, and does not pretend to be one.** <c>IExtensionStore</c> is a
    /// key-value store with no batch write, so a store failure on the second of two keys leaves the
    /// first written. What is guaranteed is narrower and is the part that was broken: memory and the
    /// store never disagree, because a key that fails to persist is not assigned either.
    /// </summary>
    public async Task<State> ApplyAsync(
        TagNameStyle? tagNameStyle,
        IEnumerable<string>? coverHosts,
        IEnumerable<string>? sourceFolders,
        CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            if (tagNameStyle is { } style)
                await WriteTagNameStyleAsync(style, ct);
            if (coverHosts is not null)
                await WriteCoverHostsAsync(coverHosts, ct);
            if (sourceFolders is not null)
                await WriteSourceFoldersAsync(sourceFolders, ct);

            return _current;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Changes the style, persisting it before it takes effect in memory.
    ///
    /// The write goes first deliberately. <see cref="LoadAsync"/> treats the store as authoritative at
    /// startup, so the store has to be what decides whether the change happened at all — assigning
    /// first would leave a failed write showing the new style for the rest of the session and
    /// reverting on restart, while the caller was told it failed. Every signal would disagree with the
    /// next one.
    ///
    /// A failure is not swallowed here, unlike in <see cref="LoadAsync"/>. There the defaults are a
    /// usable answer; here the user asked for something and did not get it, so the caller needs to
    /// know rather than be told it worked.
    ///
    /// A one-field <see cref="ApplyAsync"/>, so this and a multi-field save take the same gate rather
    /// than being two ways of writing a setting that can interleave with each other.
    /// </summary>
    public Task SetTagNameStyleAsync(TagNameStyle style, CancellationToken ct = default) =>
        ApplyAsync(style, null, null, ct);

    private async Task WriteTagNameStyleAsync(TagNameStyle style, CancellationToken ct)
    {
        if (_store is not null)
            await _store.SetAsync(TagNameStyler.SettingKey, TagNameStyler.Serialize(style), ct);
        _current = _current with { TagNameStyle = style };
    }

    /// <summary>
    /// Replaces the cover-host list. Persists before taking effect, for the same reason
    /// <see cref="SetTagNameStyleAsync"/> does.
    ///
    /// Entries are normalised on the way in rather than on the way out, so what a later read sees is
    /// what was stored, and the value in the store is the value the user will be shown back.
    /// </summary>
    public Task SetCoverHostsAsync(IEnumerable<string> hosts, CancellationToken ct = default) =>
        ApplyAsync(null, hosts, null, ct);

    private async Task WriteCoverHostsAsync(IEnumerable<string> hosts, CancellationToken ct)
    {
        var cleaned = CoverHostSetting.Clean(hosts);
        if (_store is not null)
            await _store.SetAsync(CoverHostSetting.SettingKey, CoverHostSetting.Serialize(cleaned), ct);
        _current = _current with { CoverHosts = cleaned };
    }

    /// <summary>
    /// Replaces the source-folder list, on the same write-then-assign contract as the two above.
    ///
    /// The index is not rebuilt here. A folder can be added, removed and added again while the
    /// operator settles on the right one, and rebuilding on each write would re-read every torrent in
    /// every folder for edits that are still in progress. The reload is the caller's, and it is the
    /// action the page already offers.
    /// </summary>
    public Task SetSourceFoldersAsync(IEnumerable<string> folders, CancellationToken ct = default) =>
        ApplyAsync(null, null, folders, ct);

    private async Task WriteSourceFoldersAsync(IEnumerable<string> folders, CancellationToken ct)
    {
        var cleaned = SourceFolderSetting.Clean(folders);
        if (_store is not null)
            await _store.SetAsync(SourceFolderSetting.SettingKey, SourceFolderSetting.Serialize(cleaned), ct);
        _current = _current with { SourceFolders = cleaned };
    }
}
