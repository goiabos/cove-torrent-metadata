using System.Text.Json;
using Cove.Core.Auth;
using Cove.TorrentMetadata;
using Cove.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// Pins the manifest and action declarations.
///
/// Most of these compare a literal to a literal, and that is the point: they are not here because
/// the values are hard to get right, they are here because each one is a single-word change that
/// builds clean, ships, and breaks something with no error message. The test names carry the
/// consequence, because the name is the only thing that will explain the failure to whoever hits it.
/// A red <c>Names_the_page_route_without_a_leading_slash</c> says what a red
/// <c>Manifest_route_is_correct</c> never would.
///
/// <see cref="Uses_the_id_from_extension_json_rather_than_one_written_in_C_sharp"/> is the exception
/// and the reason this issue mattered: it is a real cross-artifact check, and the divergence it
/// guards takes down every extension's UI rather than just this one.
/// </summary>
public class ManifestTests
{
    // ---------------------------------------------------------------------
    // Identity
    // ---------------------------------------------------------------------

    [Fact]
    public void Uses_the_id_from_extension_json_rather_than_one_written_in_C_sharp()
    {
        var manifest = ShippedManifest();
        var extension = Configured(manifest);

        // The install directory is named for the manifest id and the host resolves asset URLs from the
        // code Id. A divergence 404s the UI bundle, which fails the frontend's whole reconcile pass and
        // silently withdraws EVERY extension's UI, not only this one.
        //
        // Compared against the manifest actually shipped — read off disk, not a fixture — because the
        // hazard is a divergence between two artifacts. A hand-built manifest would catch someone
        // overriding Id in C# and be blind to someone editing extension.json, which is the likelier
        // edit: that file is opened at every release to bump `version`, three lines under `id`.
        Assert.Equal(manifest.Id, extension.Id);
        Assert.Equal("io.github.goiabos.torrent-metadata", extension.Id);
    }

    /// <summary>
    /// The compiled assembly carries the manifest's version, because MSBuild reads it from there.
    ///
    /// `Directory.Build.props` used to hard-code `<c>&lt;Version&gt;0.1.0&lt;/Version&gt;</c>` while the
    /// release procedure bumps only `extension.json` — two copies, no comparison, and nothing that
    /// reads the assembly version at all. From the first release on, every shipped DLL would have
    /// claimed 0.1.0 while the manifest said otherwise, silently.
    ///
    /// It is read with a regex, because MSBuild has no JSON support and this has to be a property
    /// rather than a target. This test is the other half of that trade: it parses the manifest
    /// properly, so a crude read that stops matching — a reformatted file, a `version` key nested
    /// somewhere else — fails here instead of shipping.
    /// </summary>
    [Fact]
    public void Stamps_the_assembly_with_the_version_from_extension_json()
    {
        var manifest = ShippedManifest();
        var assembly = typeof(TorrentMetadataExtension).Assembly;

        // AssemblyInformationalVersion is what `<Version>` sets verbatim; AssemblyVersion drops any
        // prerelease suffix and gains a `.0`, so it cannot be compared to the manifest as written.
        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Single()
            .InformationalVersion;

        // The SDK appends `+<commit sha>` when the build knows one; the version is the part before it.
        Assert.Equal(manifest.Version, informational.Split('+')[0]);
    }

    [Fact]
    public void Attributes_the_page_and_the_action_to_this_extension()
    {
        var extension = Configured(ShippedManifest());

        var page = Assert.Single(extension.GetUIManifest().Pages);
        var action = Assert.Single(extension.GetActions());

        Assert.Equal(extension.Id, page.ExtensionId);
        Assert.Equal(extension.Id, action.ExtensionId);
    }

    // ---------------------------------------------------------------------
    // The settings panel
    // ---------------------------------------------------------------------

    [Fact]
    public void Names_the_settings_component_the_bundle_registers()
    {
        var panel = Assert.Single(Configured(ShippedManifest()).GetUIManifest().SettingsPanels);

        // Matches the key in main.tsx's `components: { TorrentBatchPage, TorrentMetadataSettings }`.
        // The host resolves a panel by this name and renders nothing at all when it misses — an empty
        // SectionCard on the Settings page, with no error anywhere.
        Assert.Equal("TorrentMetadataSettings", panel.ComponentName);
    }

    [Fact]
    public void Puts_the_settings_panel_on_a_tab_of_its_own()
    {
        var extension = Configured(ShippedManifest());

        var tab = Assert.Single(extension.GetUIManifest().SettingsTabs);
        var panel = Assert.Single(extension.GetUIManifest().SettingsPanels);

        // The two halves of one contribution, and the host joins them by this string alone:
        // getSettingsPanelsForTab compares the panel's TargetTab against the tab's key. Naming a tab
        // that does not exist does not fail — the panel is simply rendered on none of them, and the
        // tab draws its own empty state instead.
        Assert.Equal(tab.Key, panel.TargetTab);
        Assert.Null(panel.TargetSection);
    }

    [Fact]
    public void Keys_the_settings_tab_on_the_manifest_id()
    {
        var extension = Configured(ShippedManifest());

        var tab = Assert.Single(extension.GetUIManifest().SettingsTabs);

        // The key is the route segment after /settings/, and the host registers the part after
        // "extensions/" as a shorthand alias for it. Shaped any other way, only one of the two URLs
        // resolves.
        Assert.Equal($"extensions/{extension.Id}", tab.Key);
        Assert.Equal(extension.Id, tab.ExtensionId);
        // Lowercase already, and the host lowercases the key before matching it against the URL — so a
        // manifest id with a capital in it would be addressable only in the form the host produced.
        Assert.Equal(tab.Key.ToLowerInvariant(), tab.Key);
    }

    [Fact]
    public void Gives_the_settings_panel_an_id_of_its_own()
    {
        var extension = Configured(ShippedManifest());

        var panel = Assert.Single(extension.GetUIManifest().SettingsPanels);

        // The host keys the rendered panel by this id. Sharing it with the page's route, or with a
        // second panel added later, silently renders one of them only.
        Assert.Equal($"{extension.Id}:naming", panel.Id);
    }

    // ---------------------------------------------------------------------
    // The page
    // ---------------------------------------------------------------------

    [Fact]
    public void Names_the_page_route_without_a_leading_slash()
    {
        var page = Assert.Single(Configured(ShippedManifest()).GetUIManifest().Pages);

        // The router builds the URL as `/${route.page}`, so a leading slash yields "//torrent-metadata" — a
        // protocol-relative URL the browser resolves against a different host entirely. Built-in pages
        // follow the same convention ("videos", "images").
        Assert.Equal("torrent-metadata", page.Route);
        Assert.DoesNotContain("/", page.Route);
    }

    [Fact]
    public void Uses_a_page_icon_the_host_can_actually_render()
    {
        var page = Assert.Single(Configured(ShippedManifest()).GetUIManifest().Pages);

        // ICON_MAP in the host's ExtensionLoader.tsx holds exactly two entries. Any other name is
        // accepted and then renders nothing at all — no icon, no warning.
        Assert.Contains(page.Icon, new[] { "music", "puzzle" });
    }

    [Fact]
    public void Keeps_the_page_behind_the_same_permission_as_the_endpoints_it_calls()
    {
        var page = Assert.Single(Configured(ShippedManifest()).GetUIManifest().Pages);

        // The convenience AddPage overload takes no permission, so the full definition is used. Without
        // this the page is listed in the nav for users whose every request from it would be refused.
        Assert.Equal(Permissions.VideosScrape, page.RequiredPermission);
    }

    [Fact]
    public void Names_the_component_the_bundle_registers()
    {
        var page = Assert.Single(Configured(ShippedManifest()).GetUIManifest().Pages);

        // Matches the key in main.tsx's `components: { TorrentBatchPage }`. Renaming one side leaves a
        // page that loads and renders nothing.
        Assert.Equal("TorrentBatchPage", page.ComponentName);
    }

    // ---------------------------------------------------------------------
    // The action
    // ---------------------------------------------------------------------

    [Fact]
    public void Uses_an_action_type_the_host_renders()
    {
        var action = Assert.Single(Configured(ShippedManifest()).GetActions());

        // The record documents three types, but only two are ever asked for: ExtensionEntityActions
        // requests "toolbar" and ExtensionSelectionActions requests "bulk". A "context-menu" action is
        // accepted by the manifest and then never displayed.
        Assert.Contains(action.ActionType, new[] { "toolbar", "bulk" });
        Assert.Equal("toolbar", action.ActionType);
    }

    [Fact]
    public void Keeps_the_action_behind_the_same_permission_as_the_endpoints_it_calls()
    {
        var action = Assert.Single(Configured(ShippedManifest()).GetActions());

        Assert.Equal(Permissions.VideosScrape, action.RequiredPermission);
    }

    [Fact]
    public void Opens_a_dialog_rather_than_invoking_an_endpoint_directly()
    {
        var action = Assert.Single(Configured(ShippedManifest()).GetActions());

        // The whole point of this extension is that a torrent is reviewed before anything is written.
        // An ApiEndpoint here would make the toolbar button apply metadata on click.
        Assert.Null(action.ApiEndpoint);
        Assert.Equal("openTorrentMatchDialog", action.HandlerName);
    }

    [Fact]
    public void Suppresses_the_hosts_queued_alert_because_the_action_opens_a_dialog()
    {
        var action = Assert.Single(Configured(ShippedManifest()).GetActions());

        // Defaults to false on the record, so this is one deleted word away. The host would then show
        // "…queued for video" over the review dialog, describing work it has not done.
        Assert.True(action.SuppressSuccessAlert);
    }

    [Fact]
    public void Offers_the_action_on_videos()
    {
        var action = Assert.Single(Configured(ShippedManifest()).GetActions());

        Assert.Equal(["video"], action.EntityTypes);
    }

    // ---------------------------------------------------------------------
    // Permissions the extension enforces on itself
    // ---------------------------------------------------------------------

    [Fact]
    public void Names_no_image_host_in_the_manifest()
    {
        var manifest = ShippedManifest();

        // The allowlist moved into settings, so a host listed here would be doing nothing except
        // naming somebody's tracker in the one artifact that gets published. Cove reads
        // permissions.network nowhere, so this cannot regress into enforcement by accident
        // either — a host added back would be inert *and* published.
        Assert.Empty(manifest.Permissions.Network);
    }

    [Fact]
    public void Feeds_the_cover_allowlist_from_the_settings_rather_than_the_manifest()
    {
        var services = new ServiceCollection();
        Configured(ShippedManifest()).ConfigureServices(services, Context());

        var allowlist = services.BuildServiceProvider().GetRequiredService<CoverHostAllowlist>();

        // The wiring, not the rule — CoverHostAllowlist's own behaviour is covered in
        // CoverImportTests. What this catches is the registration being dropped or pointed back at a
        // hand-written list: covers would then either stop working entirely, or start ignoring the
        // setting the user was told to edit, and nothing else in the build would say so.
        Assert.Empty(allowlist.Hosts);
        Assert.True(allowlist.IsEmpty);

        // Empty allows nothing, which is the fail-safe direction and the shipped default.
        Assert.False(allowlist.Allows(new Uri("https://img.example/cover.jpg")));
        Assert.False(allowlist.Allows(new Uri("http://169.254.169.254/latest/meta-data")));
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static ExtensionContext Context() => Shipped.Context();

    /// <summary>The real <c>extension.json</c> — see <see cref="Shipped"/> for why it is read off disk.</summary>
    private static ExtensionManifestFile ShippedManifest() => Shipped.Manifest();

    /// <summary>
    /// An extension with its manifest applied, which is what the host does immediately after
    /// construction. Without it every member here throws: Id reads Manifest.Id, and both GetUIManifest
    /// and GetActions go through Id.
    /// </summary>
    private static TorrentMetadataExtension Configured(ExtensionManifestFile manifest)
    {
        var extension = new TorrentMetadataExtension();
        ((IManifestAware)extension).ApplyManifest(manifest);
        return extension;
    }
}
