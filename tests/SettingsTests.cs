using Cove.TorrentMetadata;
using Cove.Plugins;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// Covers the tag-name-style setting end to end: parsed from the wire, written to the host's
/// per-extension store, and read back by a fresh extension at startup.
///
/// Each of those steps was individually untested, which is a worse position than it sounds —
/// the setting appears to work for the rest of the session and reverts on restart, so the failure
/// only ever shows up long after the change that caused it.
///
/// The two failure modes are deliberately asymmetric, and both are asserted here: a failed *read*
/// leaves the defaults, because defaults are a usable answer and a settings problem must never stop
/// the extension loading; a failed *write* leaves the style unchanged and throws, because the user
/// asked for something and did not get it.
///
/// The cover-host list joined it later and matters more than the style does: it ships empty, so
/// until it round-trips correctly no cover imports at all.
/// </summary>
public class SettingsTests
{
    // ---------------------------------------------------------------------
    // Parsing the wire value
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("spaced", TagNameStyle.Spaced)]
    [InlineData("dotted", TagNameStyle.Dotted)]
    [InlineData("titlecase", TagNameStyle.TitleCase)]
    [InlineData("DOTTED", TagNameStyle.Dotted)]
    [InlineData("  dotted  ", TagNameStyle.Dotted)]
    // Anything unrecognised is the default rather than an error: this value arrives from a stored
    // string and from the wire, and neither is worth failing a load or a request over.
    [InlineData("no such style", TagNameStyle.TitleCase)]
    [InlineData("", TagNameStyle.TitleCase)]
    [InlineData(null, TagNameStyle.TitleCase)]
    public void Parses_a_stored_or_posted_style(string? value, TagNameStyle expected)
        => Assert.Equal(expected, TagNameStyler.Parse(value));

    [Theory]
    [InlineData(TagNameStyle.Spaced, "spaced")]
    [InlineData(TagNameStyle.Dotted, "dotted")]
    [InlineData(TagNameStyle.TitleCase, "titlecase")]
    public void Round_trips_every_style_through_its_serialized_form(TagNameStyle style, string expected)
    {
        Assert.Equal(expected, TagNameStyler.Serialize(style));

        // The two halves have to agree: Serialize writes what Parse will later read out of the store.
        Assert.Equal(style, TagNameStyler.Parse(TagNameStyler.Serialize(style)));
    }

    // ---------------------------------------------------------------------
    // The cover-host list
    //
    // A user setting rather than a manifest declaration, shipped empty. Empty allows nothing,
    // so a parse that quietly drops an entry is not a cosmetic bug — it is covers not importing.
    // ---------------------------------------------------------------------

    [Theory]
    // The shapes a free-text field actually receives.
    [InlineData("img.example", "img.example")]
    [InlineData("a.example\nb.example", "a.example|b.example")]
    [InlineData("a.example, b.example", "a.example|b.example")]
    [InlineData("  a.example  ;  b.example  ", "a.example|b.example")]
    // Pasting the cover URL from the dialog is the obvious mistake; storing it verbatim would leave an
    // entry that can never equal a Uri.Host, which reads as the setting being ignored.
    [InlineData("https://img.example/cover.jpg", "img.example")]
    [InlineData("http://img.example:8080/a/b", "img.example")]
    // A port or a path on a bare host has the same problem and the same fix.
    [InlineData("img.example:8080", "img.example")]
    [InlineData("img.example/covers", "img.example")]
    // Duplicates collapse case-insensitively, so the list the user reads back is the list that matters.
    [InlineData("img.example\nIMG.example", "img.example")]
    // The wildcard marker survives, because it is the difference between two entries rather than
    // noise to be cleaned off. The bare-dot spelling is what a browser cookie domain looks like, so
    // it is what people type; both normalise to one form so the list reads consistently.
    [InlineData("*.img.example", "*.img.example")]
    [InlineData(".img.example", "*.img.example")]
    [InlineData("*.img.example\n.IMG.example", "*.img.example")]
    // A wildcard and its apex are different entries, not duplicates: one means the host, the other
    // means the host and everything under it.
    [InlineData("img.example\n*.img.example", "img.example|*.img.example")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Reduces_each_entry_to_a_bare_host(string? value, string expected)
    {
        var hosts = CoverHostSetting.Parse(value);

        Assert.Equal(expected.Length == 0 ? [] : expected.Split('|'), hosts);
    }

    [Theory]
    // The settings half of the SSRF fix. These all normalised to themselves before, so any
    // account holding the permission that guards the settings endpoint could add one line and turn
    // the cover proxy into an authenticated probe of the host's own network.
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.1")]
    [InlineData("0.0.0.0")]
    [InlineData("http://127.0.0.1:8080/cover.jpg")]
    [InlineData("[::1]")]
    [InlineData("http://[::1]/cover.jpg")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    // Reserved for loopback by RFC 6761, so a literal in all but spelling.
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("api.localhost")]
    // An undotted name resolves through the host's own search domain, onto the intranet. A tracker's
    // image host is always dotted.
    [InlineData("intranet-wiki")]
    [InlineData("*.localhost")]
    public void Refuses_an_entry_that_could_never_be_a_public_image_host(string value)
        => Assert.Empty(CoverHostSetting.Parse(value));

    [Fact]
    public void Keeps_the_entries_around_one_it_refuses()
    {
        // Dropping is per-entry. A pasted list with one bad line must not cost the operator the rest
        // of it — they would have no way to tell which line was the problem.
        Assert.Equal(
            ["a.example", "b.example"],
            CoverHostSetting.Parse("a.example\n127.0.0.1\nb.example"));
    }

    [Fact]
    public async Task Drops_an_internal_literal_a_previous_version_had_already_stored()
    {
        var store = new FakeExtensionStore();
        store.Values[CoverHostSetting.SettingKey] = "img.example\n169.254.169.254";

        var settings = new TorrentMetadataSettings();
        settings.AttachStore(store);
        await settings.LoadAsync();

        // The check runs on read as well as on write, which is what makes it a migration rather than
        // a rule that only applies to lists edited after the upgrade.
        Assert.Equal(["img.example"], settings.CoverHosts);
    }

    // ---------------------------------------------------------------------
    // What the list then admits
    // ---------------------------------------------------------------------

    [Theory]
    // A bare entry means that host and nothing else.
    [InlineData("img.example", "https://img.example/cover.jpg", true)]
    [InlineData("img.example", "https://cdn.img.example/cover.jpg", false)]
    [InlineData("img.example", "https://elsewhere.example/cover.jpg", false)]
    // A wildcard entry means the host and anything under it — including the apex, because someone
    // who typed "*.img.example" is describing that image host, not excluding its own front door.
    [InlineData("*.img.example", "https://img.example/cover.jpg", true)]
    [InlineData("*.img.example", "https://cdn.img.example/cover.jpg", true)]
    [InlineData("*.img.example", "https://a.b.img.example/cover.jpg", true)]
    // Matched with the separating dot and something in front of it: a bare suffix check would let
    // "evilimg.example" pass as "img.example".
    [InlineData("*.img.example", "https://evilimg.example/cover.jpg", false)]
    [InlineData("img.example", "https://evilimg.example/cover.jpg", false)]
    // Refused whatever the list says. Normalise stops such an entry being stored, but this is the
    // check the fetch actually runs, and a list written before that check is still on disk.
    [InlineData("127.0.0.1", "http://127.0.0.1/cover.jpg", false)]
    [InlineData("*.example", "http://169.254.169.254/latest/meta-data", false)]
    public void Admits_a_subdomain_only_where_the_operator_asked_for_one(
        string entry, string url, bool expected)
    {
        // Built from the raw entry rather than a cleaned one, so the stored spelling and the
        // comparison are checked together — they are the two halves of one rule.
        var allowlist = new CoverHostAllowlist([entry]);

        Assert.Equal(expected, allowlist.Allows(new Uri(url)));
    }

    [Fact]
    public void Tells_an_operator_relying_on_the_old_subdomain_rule_what_to_change()
    {
        var allowlist = new CoverHostAllowlist(["img.example"]);

        var explanation = allowlist.Explain(new Uri("https://cdn.img.example/cover.jpg"));

        // The one refusal this change newly produces: a list that worked before the upgrade refuses
        // covers now. "Not in the allowlist" would send them to add a host already sitting there, so
        // the message has to name the entry and the edit instead.
        Assert.Contains("img.example", explanation, StringComparison.Ordinal);
        Assert.Contains("*.img.example", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_an_internal_address_is_internal_rather_than_merely_unlisted()
    {
        var allowlist = new CoverHostAllowlist(["img.example"]);

        // "Add it in the extension's settings" would be advice that cannot be taken — the settings
        // endpoint refuses to store it. Naming what it is says why there is nothing to do.
        Assert.Contains(
            "this server's own network",
            allowlist.Explain(new Uri("http://169.254.169.254/latest/meta-data")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_the_cover_hosts_to_the_store_under_the_key_it_reads_back()
    {
        var store = new FakeExtensionStore();
        var settings = new TorrentMetadataSettings();
        settings.AttachStore(store);

        await settings.SetCoverHostsAsync(["https://img.example/cover.jpg", "other.example"]);

        // Normalised on the way in, so what a later session loads is what the user will be shown —
        // rather than the raw text needing the same cleanup again on every read.
        Assert.Equal("img.example\nother.example", store.Values[CoverHostSetting.SettingKey]);
        Assert.Equal(["img.example", "other.example"], settings.CoverHosts);
    }

    [Fact]
    public async Task Loads_the_persisted_cover_hosts_on_startup()
    {
        var store = new FakeExtensionStore();
        store.Values[CoverHostSetting.SettingKey] = "img.example\nother.example";

        var settings = new TorrentMetadataSettings();
        settings.AttachStore(store);
        await settings.LoadAsync();

        // The whole point of the setting: an operator configures it once and covers keep working
        // across restarts. Without this the allowlist is empty on every boot and nothing imports.
        Assert.Equal(["img.example", "other.example"], settings.CoverHosts);
    }

    [Fact]
    public async Task Ships_with_no_cover_hosts_at_all()
    {
        var settings = new TorrentMetadataSettings();
        settings.AttachStore(new FakeExtensionStore());
        await settings.LoadAsync();

        // The shipped default, and a deliberate one: a populated default would have to name somebody's
        // tracker. Empty means covers are off until configured, which is why the skip is explained.
        Assert.Empty(settings.CoverHosts);
        Assert.True(new CoverHostAllowlist(settings).IsEmpty);
    }

    [Fact]
    public async Task Serves_an_edited_host_list_to_an_allowlist_that_was_built_earlier()
    {
        var settings = new TorrentMetadataSettings();
        var allowlist = new CoverHostAllowlist(settings);

        Assert.False(allowlist.Allows(new Uri("https://img.example/cover.jpg")));

        await settings.SetCoverHostsAsync(["img.example"]);

        // The allowlist is a singleton built once at startup, so it has to read the setting live —
        // otherwise editing the list appears to do nothing until the host is restarted, and the skip
        // message would keep naming a host the user has just added.
        Assert.True(allowlist.Allows(new Uri("https://img.example/cover.jpg")));
    }

    // ---------------------------------------------------------------------
    // Source folders
    // ---------------------------------------------------------------------

    [Theory]
    // Relative paths would resolve against the server process's working directory — somewhere the
    // operator did not choose and cannot see. Refusing beats silently addressing elsewhere.
    [InlineData("torrents")]
    [InlineData("../torrents")]
    [InlineData("./torrents")]
    [InlineData("")]
    [InlineData("   ")]
    public void Refuses_a_folder_that_is_not_an_absolute_path(string value)
        => Assert.Empty(SourceFolderSetting.Clean([value]));

    [Fact]
    public void Refuses_a_filesystem_root()
    {
        // The index enumerates with AllDirectories, so a root turns every rescan into a crawl of the
        // whole disk. A mistyped path must not be able to do that.
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;

        Assert.Empty(SourceFolderSetting.Clean([root]));
    }

    [Fact]
    public void Keeps_an_absolute_folder_and_strips_its_trailing_separator()
    {
        var folder = Path.Combine(Path.GetTempPath(), "torrents");

        var cleaned = SourceFolderSetting.Clean([folder + Path.DirectorySeparatorChar]);

        // Two spellings of one folder would otherwise be stored twice and indexed twice.
        Assert.Equal([Path.TrimEndingDirectorySeparator(folder)], cleaned);
    }

    [Fact]
    public void Collapses_two_spellings_of_the_same_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "torrents");
        var roundabout = Path.Combine(folder, "..", "torrents");

        Assert.Single(SourceFolderSetting.Clean([folder, roundabout]));
    }

    [Fact]
    public void Splits_stored_folders_on_newlines_only()
    {
        var withSpaces = Path.Combine(Path.GetTempPath(), "my torrents");
        var other = Path.Combine(Path.GetTempPath(), "more");

        var parsed = SourceFolderSetting.Parse($"{withSpaces}\n{other}");

        // Unlike the host list, which also splits on commas and spaces: a path legitimately contains
        // spaces, and cutting there hands back two entries addressing nothing.
        Assert.Equal([withSpaces, other], parsed);
    }

    [Fact]
    public async Task Persists_source_folders_under_the_key_it_reads_back()
    {
        var store = new FakeExtensionStore();
        var settings = new TorrentMetadataSettings();
        settings.AttachStore(store);
        var folder = Path.Combine(Path.GetTempPath(), "torrents");

        await settings.SetSourceFoldersAsync([folder, "relative/no"]);

        Assert.Equal(folder, store.Values[SourceFolderSetting.SettingKey]);
        Assert.Equal([folder], settings.SourceFolders);
    }

    // ---------------------------------------------------------------------
    // Persisting
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Writes_the_chosen_style_to_the_store_under_the_key_it_reads_back()
    {
        var store = new FakeExtensionStore();
        var settings = new TorrentMetadataSettings();
        settings.AttachStore(store);

        await settings.SetTagNameStyleAsync(TagNameStyle.Dotted);

        Assert.Equal("dotted", store.Values[TagNameStyler.SettingKey]);
        Assert.Equal(TagNameStyle.Dotted, settings.TagNameStyle);
    }

    [Fact]
    public async Task Changes_the_style_with_no_store_attached()
    {
        var settings = new TorrentMetadataSettings();

        await settings.SetTagNameStyleAsync(TagNameStyle.Spaced);

        // Nothing to persist to is not a failure — the services are constructed before the host hands
        // over the store, and the tests for every other suite run this way.
        Assert.Equal(TagNameStyle.Spaced, settings.TagNameStyle);
    }

    [Fact]
    public async Task Leaves_the_style_unchanged_when_the_store_write_fails()
    {
        var store = new FakeExtensionStore { FailWrites = true };
        var settings = new TorrentMetadataSettings();
        settings.AttachStore(store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => settings.SetTagNameStyleAsync(TagNameStyle.Dotted));

        // The store decides what the next startup sees, so it decides whether the change happened.
        // Assigning first would leave the session showing Dotted, the store holding nothing, and the
        // caller having been told it failed — three answers, no two alike.
        Assert.Equal(TagNameStyle.TitleCase, settings.TagNameStyle);
    }

    // ---------------------------------------------------------------------
    // Loading
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Loads_the_persisted_style_on_startup()
    {
        var store = new FakeExtensionStore();
        store.Values[TagNameStyler.SettingKey] = "spaced";
        var settings = new TorrentMetadataSettings();
        settings.AttachStore(store);

        await settings.LoadAsync();

        Assert.Equal(TagNameStyle.Spaced, settings.TagNameStyle);
    }

    [Fact]
    public async Task Keeps_the_default_when_nothing_has_been_stored_yet()
    {
        var settings = new TorrentMetadataSettings();
        settings.AttachStore(new FakeExtensionStore());

        await settings.LoadAsync();

        Assert.Equal(TagNameStyle.TitleCase, settings.TagNameStyle);
    }

    [Fact]
    public async Task Keeps_the_default_when_the_store_read_throws()
    {
        var settings = new TorrentMetadataSettings();
        settings.AttachStore(new FakeExtensionStore { FailReads = true });

        await settings.LoadAsync();

        // Unlike a write, a failed read has a usable answer: the defaults. A settings problem must
        // never be the reason the extension does not load.
        Assert.Equal(TagNameStyle.TitleCase, settings.TagNameStyle);
    }

    [Fact]
    public async Task Loads_nothing_and_does_not_throw_when_no_store_was_attached()
    {
        var settings = new TorrentMetadataSettings();

        await settings.LoadAsync();

        Assert.Equal(TagNameStyle.TitleCase, settings.TagNameStyle);
    }

    // ---------------------------------------------------------------------
    // The store and memory never disagree
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Leaves_the_store_and_memory_agreeing_when_two_saves_of_one_setting_overlap()
    {
        // Each save persists before it assigns, so a failed write leaves the store authoritative. That
        // contract held per call and not between calls: two saves could reach the store in one order
        // and memory in the other, leaving the two disagreeing until a restart re-read the store.
        //
        // The interleaving is forced rather than raced. The store blocks inside its *first* write, so
        // the second save is guaranteed to be the one that lands last — no timing, no retries.
        var store = new BlockingWriteStore();
        var settings = new TorrentMetadataSettings();
        settings.AttachStore(store);

        var first = settings.SetCoverHostsAsync(["first.example"]);
        await store.EnteredFirstWrite;

        var second = settings.SetCoverHostsAsync(["second.example"]);
        store.ReleaseFirstWrite();
        await Task.WhenAll(first, second);

        // Stated as an invariant between the two rather than as a winner, which is the point: which
        // save wins is the caller's race and is not this object's business, but the store and the
        // value every proposal reads have to be the same answer.
        Assert.Equal(
            CoverHostSetting.Serialize(settings.CoverHosts),
            store.Values[CoverHostSetting.SettingKey]);
    }

    [Fact]
    public async Task Applies_the_fields_a_request_carried_and_leaves_the_rest_alone()
    {
        var store = new FakeExtensionStore();
        var settings = new TorrentMetadataSettings();
        settings.AttachStore(store);
        await settings.SetSourceFoldersAsync([Path.Combine(Path.GetTempPath(), "torrents")]);
        var folders = settings.SourceFolders;

        var applied = await settings.ApplyAsync(TagNameStyle.Dotted, ["images.example"], null);

        // The two it carried are applied, and the one it did not is untouched — a PUT from the
        // cover-host editor must not reset the folders the other panel owns.
        Assert.Equal(TagNameStyle.Dotted, applied.TagNameStyle);
        Assert.Equal(["images.example"], applied.CoverHosts);
        Assert.Equal(folders, applied.SourceFolders);

        // The returned state is the whole document, read under the gate that wrote it. The panel
        // treats its read-back as what its own save produced, so it has to be one moment's worth of
        // settings rather than three properties read one after another.
        Assert.Equal(settings.Current, applied);
    }

    [Fact]
    public async Task Leaves_a_setting_it_could_not_persist_out_of_memory_too()
    {
        var settings = new TorrentMetadataSettings();
        settings.AttachStore(new FakeExtensionStore { FailWrites = true });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => settings.ApplyAsync(TagNameStyle.Dotted, null, null));

        // Not a transaction — `IExtensionStore` has no batch write, so a multi-key save can still land
        // one key and fail the next. The narrower guarantee is the one that has to hold: a key that
        // did not reach the store is not in memory either, so the two never describe different
        // settings.
        Assert.Equal(TagNameStyle.TitleCase, settings.TagNameStyle);
    }

    [Fact]
    public async Task Keeps_every_default_when_the_load_fails_part_way_through()
    {
        var store = new FailOnNthReadStore(fails: 2);
        store.Values[TagNameStyler.SettingKey] = "dotted";
        var settings = new TorrentMetadataSettings();
        settings.AttachStore(store);

        await settings.LoadAsync();

        // "Any failure leaves the defaults in place" is what this method has always claimed, and
        // assigning each key as it was read did not deliver it: the style loaded, the next key threw,
        // and the extension ran on a mix of stored and default values. The state is now built to the
        // side and published in one assignment, so a partial read publishes nothing.
        Assert.Equal(TagNameStyle.TitleCase, settings.TagNameStyle);
        Assert.Empty(settings.CoverHosts);
        Assert.Empty(settings.SourceFolders);
    }

    // The restart itself — a style written in one session being what the next one starts with — is
    // asserted in EndpointContractTests, where a host already exists. TorrentMetadataExtension exposes no
    // settings accessor, and adding one so a test could read it would be testing a hole cut for the
    // test; GET /settings is how anything actually observes this.
}

/// <summary>
/// A store that parks inside its first write until told to continue, so two overlapping saves can be
/// interleaved deterministically rather than raced.
///
/// The shape matters: the *first* writer is the one held, so the second is guaranteed to reach the
/// store last. Without the write gate that second save also assigns memory first, and the release
/// then lets the first save overwrite it — the store ends up holding one value and memory the other.
/// </summary>
internal sealed class BlockingWriteStore : IExtensionStore
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _writes;

    public Dictionary<string, string> Values { get; } = [];

    /// <summary>Completes once a writer is parked inside the store, so the test knows it holds the gate.</summary>
    public Task EnteredFirstWrite => _entered.Task;

    public void ReleaseFirstWrite() => _release.TrySetResult();

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        // The value lands *before* the park, and that ordering is the whole fixture. It makes the held
        // writer the one that reaches the store first and memory last — the interleaving where the two
        // end up disagreeing. Parking before the write instead makes the held writer last in both, so
        // they agree either way and the test proves nothing.
        Values[key] = value;

        if (Interlocked.Increment(ref _writes) == 1)
        {
            _entered.TrySetResult();
            await _release.Task;
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Values.Remove(key);
        return Task.CompletedTask;
    }

    public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult(new Dictionary<string, string>(Values));
}

/// <summary>
/// A store whose reads succeed until the nth, which throws — the shape of a load that fails part way
/// rather than at the first key, which is the only way to tell a partial load from no load at all.
/// </summary>
internal sealed class FailOnNthReadStore(int fails) : IExtensionStore
{
    private int _reads;

    public Dictionary<string, string> Values { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Interlocked.Increment(ref _reads) == fails
            ? throw new InvalidOperationException("store unavailable")
            : Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Values.Remove(key);
        return Task.CompletedTask;
    }

    public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult(new Dictionary<string, string>(Values));
}

/// <summary>
/// Stands in for the host's per-extension key-value store. Dictionary-backed, with the two failure
/// modes the extension has to answer for: a read that throws and a write that throws.
///
/// Namespace-level rather than nested because <c>EndpointContractTests</c> uses it too, for the
/// restart assertion.
/// </summary>
internal sealed class FakeExtensionStore : IExtensionStore
{
    public Dictionary<string, string> Values { get; } = [];

    public bool FailReads { get; init; }

    public bool FailWrites { get; init; }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => FailReads
            ? throw new InvalidOperationException("store unavailable")
            : Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        if (FailWrites)
            throw new InvalidOperationException("store unavailable");
        Values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Values.Remove(key);
        return Task.CompletedTask;
    }

    /// <summary>
    /// How many times the whole store has been materialised. `IExtensionStore` offers no prefix query,
    /// so this is the only bulk read there is and every caller pays for every key — which is what makes
    /// counting it worth doing.
    /// </summary>
    public int GetAllCalls { get; private set; }

    public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        GetAllCalls++;
        return Task.FromResult(new Dictionary<string, string>(Values));
    }
}
