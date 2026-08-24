using System.Text.Json;
using Cove.Plugins;
using Microsoft.Extensions.Configuration;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// The artifacts a test can assert against the *shipped* extension rather than a fixture.
///
/// <see cref="Manifest"/> reads the real <c>extension.json</c> from the test output where the build
/// copies it, because the hazards it guards are divergences between two artifacts — the code and the
/// manifest, or the manifest and a string written somewhere else. A hand-built manifest catches
/// someone editing the C# and is blind to someone editing the JSON, which is the likelier edit:
/// that file is opened at every release to bump <c>version</c>.
///
/// Safe to read because it is committed and always copied to the output, unlike <c>dist-ui/</c>,
/// which is gitignored — asserting against the built JS bundle would fail on a fresh clone.
/// </summary>
internal static class Shipped
{
    public static ExtensionManifestFile Manifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "extension.json");
        Assert.True(File.Exists(path), $"extension.json should be copied to the test output; looked in {path}");

        return JsonSerializer.Deserialize<ExtensionManifestFile>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    /// <summary>An extension with the shipped manifest applied, which is what the host does on load.</summary>
    public static TorrentMetadataExtension Extension()
    {
        var extension = new TorrentMetadataExtension();
        ((IManifestAware)extension).ApplyManifest(Manifest());
        return extension;
    }

    public static ExtensionContext Context() => new()
    {
        Configuration = new ConfigurationBuilder().Build(),
        DataDirectory = Path.GetTempPath(),
        CoveVersion = "test",
    };
}
