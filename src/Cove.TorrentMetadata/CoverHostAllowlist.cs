using System.Net;

namespace Cove.TorrentMetadata;

/// <summary>
/// Reading and writing the configured cover-host list.
///
/// Stored newline-separated rather than as JSON: it is a list of bare hostnames the operator typed,
/// and a value they may one day have to read or repair by hand should look like what they typed.
/// </summary>
public static class CoverHostSetting
{
    public const string SettingKey = "coverHosts";

    private static readonly char[] Separators = ['\n', '\r', ',', ';', ' ', '\t'];

    /// <summary>
    /// Splits a stored or submitted value. Commas and whitespace are accepted alongside newlines
    /// because the settings field is free text and lists get pasted in every shape.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : Clean(value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Normalises and de-duplicates already-separated entries.</summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string> tokens)
    {
        var hosts = new List<string>();
        foreach (var token in tokens)
        {
            var host = Normalise(token);
            if (host.Length > 0 && !hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                hosts.Add(host);
        }

        return hosts;
    }

    public static string Serialize(IEnumerable<string> hosts) => string.Join('\n', hosts);

    /// <summary>The prefix an entry carries when it also covers subdomains. See <see cref="Normalise"/>.</summary>
    public const string WildcardPrefix = "*.";

    /// <summary>
    /// Strips the brackets <c>Uri.Host</c> puts around an IPv6 literal, so one spelling reaches both
    /// the stored entry and the comparison. Everything else is returned unchanged.
    /// </summary>
    public static string Bare(string host) =>
        host.Length > 1 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    /// <summary>
    /// Reduces an entry to a bare host, keeping the wildcard marker if there was one.
    ///
    /// Pasting the cover URL that was just shown in the dialog is the obvious mistake to make here,
    /// and storing it verbatim would produce an entry that can never match — which reads as the
    /// setting being ignored. A port or a path is cut for the same reason: <c>Uri.Host</c> carries
    /// neither, so an entry holding one would never compare equal.
    ///
    /// <c>*.imghost.net</c> — and the bare-dot spelling <c>.imghost.net</c>, which is what a browser's
    /// cookie domain looks like and therefore what people type — means "this host and any subdomain".
    /// Without the marker an entry means that host and nothing else. Subdomains used to be included
    /// automatically; they are opt-in now because a listed apex is often a shared suffix, and the
    /// cover URL that gets matched against it comes out of an untrusted <c>.torrent</c>.
    ///
    /// An entry that cannot be a public image host is dropped rather than stored: an address on this
    /// server's own network, a name reserved for loopback, or a single-label intranet name. Dropping
    /// is how every other refusal here already works, and the panel compares the list before and
    /// after to tell the user it did not take (<c>ui/src/listEdit.ts</c>).
    /// </summary>
    private static string Normalise(string token)
    {
        var host = token.Trim();

        var wildcard = false;
        if (host.StartsWith(WildcardPrefix, StringComparison.Ordinal))
        {
            wildcard = true;
            host = host[WildcardPrefix.Length..];
        }
        else if (host.StartsWith('.'))
        {
            wildcard = true;
            host = host[1..];
        }

        // Guarded on "://" rather than attempting a parse first: `Uri.TryCreate("img.example:8080")`
        // succeeds by reading "img.example" as a *scheme*, which would silently keep the wrong half.
        if (host.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(host, UriKind.Absolute, out var uri)
            && uri.Host.Length > 0)
        {
            host = uri.Host;
        }
        else if (!IPAddress.TryParse(Bare(host), out _))
        {
            // Not cut on a bare IPv6 literal, whose every separator is a colon — cutting at the first
            // one would leave "" and refuse it for the wrong reason.
            var cut = host.IndexOfAny(['/', ':']);
            if (cut >= 0)
                host = host[..cut];
        }

        host = Bare(host.Trim().TrimEnd('.'));

        return IsFetchable(host) ? (wildcard ? WildcardPrefix + host : host) : string.Empty;
    }

    /// <summary>
    /// Whether a bare host is something a cover could legitimately come from.
    ///
    /// The literal check is the settings half of the SSRF fix: <c>127.0.0.1</c>,
    /// <c>169.254.169.254</c> and <c>10.x</c> all normalised to themselves before, and any account
    /// with the permission that guards this endpoint could turn the cover proxy into an authenticated
    /// probe of the host's network by adding one line. Names that *resolve* there are refused at
    /// connect time instead, by <see cref="CoverAddressPolicy"/> — a name cannot be settled here,
    /// which is the whole point.
    ///
    /// <c>localhost</c> is refused by name because RFC 6761 reserves it and its subdomains for
    /// loopback, so it is a literal in all but spelling. A single-label name is refused because a
    /// tracker's image host is always dotted, and an undotted one resolves through the host's own
    /// search domain onto the intranet.
    /// </summary>
    private static bool IsFetchable(string host)
    {
        if (host.Length == 0)
            return false;

        if (IPAddress.TryParse(host, out var address))
            return !CoverAddressPolicy.IsInternal(address);

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return host.Contains('.');
    }
}

/// <summary>
/// The hosts a cover image may be fetched from.
///
/// The list is a **user setting**, not a manifest declaration, and it ships empty. That is a
/// deliberate reversal of how this started: the manifest used to be both the declaration and
/// the enforcement, which was tidy but named one tracker's image hosts in an artifact that would be
/// published. Under the "works with a Luminance-based tracker" framing there is no set of hosts that
/// is correct for every operator, so a static list could only ever be wrong for someone — the
/// operator declares their own.
///
/// <c>permissions.network</c> in <c>extension.json</c> is therefore gone rather than emptied. Cove
/// reads it nowhere — <c>ExtensionPermissionManifest.Network</c> has no consumer in
/// <c>Cove.Plugins</c> or <c>Cove.Api</c>, so it was
/// never enforcement, and a field that can only hold a fixed list cannot express a scope the
/// operator chooses at runtime.
///
/// Enforcement has to happen somewhere, because a cover URL is untrusted input: it arrives as the
/// <c>cover url</c> entry in the metadata block of a .torrent downloaded from a tracker, and the
/// fetch runs server-side from wherever the Cove host sits. Unchecked, a crafted torrent points it
/// at <c>169.254.169.254</c>, at <c>localhost</c>, or at anything else on the host's network, and
/// the request is made.
///
/// Shipping empty keeps the fail-safe direction unchanged — empty allows nothing — but it moves the
/// default from "covers work" to "covers are off until configured", which is why every refusal now
/// carries a reason the user can act on (<see cref="Explain"/>). Silent refusal plus an empty
/// default reads as a broken feature.
/// </summary>
public sealed class CoverHostAllowlist
{
    private readonly Func<IReadOnlyList<string>> _hosts;

    /// <summary>A fixed list. Used by tests and anywhere the setting has already been resolved.</summary>
    public CoverHostAllowlist(IEnumerable<string> hosts)
    {
        var snapshot = CoverHostSetting.Clean(hosts);
        _hosts = () => snapshot;
    }

    /// <summary>
    /// Reads the live setting on every check, so editing the list takes effect on the next apply
    /// rather than at the next restart — the same contract the tag-name style already has.
    /// </summary>
    public CoverHostAllowlist(TorrentMetadataSettings settings) => _hosts = () => settings.CoverHosts;

    public IReadOnlyList<string> Hosts => _hosts();

    /// <summary>Nothing configured. The shipped default, and the case worth explaining separately.</summary>
    public bool IsEmpty => _hosts().Count == 0;

    /// <summary>
    /// True when this URI may be fetched: http(s), on a configured host — or on a subdomain of one
    /// the operator marked with <see cref="CoverHostSetting.WildcardPrefix"/>.
    ///
    /// This is a **name** check, and a name is not where the packet goes. What stops the name from
    /// resolving somewhere internal between this check and the socket is
    /// <see cref="CoverAddressPolicy"/>, wired into the cover client's connect. Neither half is
    /// sufficient alone: this one decides whose image host it is, that one decides where the bytes
    /// come from.
    ///
    /// An empty list allows nothing, which means a cover simply does not import rather than failing
    /// an apply — <c>TryStoreCoverAsync</c> treats every refusal the same way, and an unreachable
    /// image host must never cost the user the tags they just approved.
    /// </summary>
    public bool Allows(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return false;

        var target = CoverHostSetting.Bare(uri.Host);
        if (target.Length == 0)
            return false;

        // Refused whatever the list says. `Normalise` stops such an entry being stored, but a list
        // written before that check is still on disk, and this is the check the fetch actually runs.
        if (IPAddress.TryParse(target, out var literal) && CoverAddressPolicy.IsInternal(literal))
            return false;

        foreach (var entry in _hosts())
        {
            if (!entry.StartsWith(CoverHostSetting.WildcardPrefix, StringComparison.Ordinal))
            {
                if (string.Equals(target, entry, StringComparison.OrdinalIgnoreCase))
                    return true;

                continue;
            }

            // A wildcard covers the apex as well as anything under it: someone who typed
            // "*.imghost.net" is describing that image host, not excluding its own front door.
            var apex = entry[CoverHostSetting.WildcardPrefix.Length..];
            if (string.Equals(target, apex, StringComparison.OrdinalIgnoreCase))
                return true;

            // Matched with the separating dot, and requiring something in front of it: a bare suffix
            // check would let "evilimghost.net" pass as "imghost.net".
            if (target.Length > apex.Length + 1
                && target.EndsWith($".{apex}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Why a URL was refused, phrased for whoever ticked the cover box and naming what to do next.
    ///
    /// The empty case is worded separately on purpose. It is the shipped default, so "not in the
    /// allowlist" would describe an unconfigured feature as a rejected one — the difference between
    /// "you have not set this up" and "your tracker is blocked".
    /// </summary>
    public string Explain(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return $"Cover skipped: \"{uri.Scheme}\" is not an http or https URL.";

        if (IsEmpty)
        {
            return "Cover skipped: no cover hosts are configured. Add your tracker's image hosts in the "
                + "extension's settings — covers are off until you do.";
        }

        var target = CoverHostSetting.Bare(uri.Host);

        if (IPAddress.TryParse(target, out var literal) && CoverAddressPolicy.IsInternal(literal))
            return $"Cover skipped: {target} is an address on this server's own network.";

        // Named separately because it is the one refusal the operator has already half-configured,
        // and the one this release newly produces: subdomains used to be included automatically, so
        // a list that worked before the change refuses covers now until the entry gains its marker.
        // "Not in the allowlist" would send them to add a host that is already sitting there.
        if (ListedApexOf(target) is { } apex)
        {
            return $"Cover skipped: {target} is a subdomain of {apex}, which is listed on its own. "
                + $"Change that entry to {CoverHostSetting.WildcardPrefix}{apex} to include subdomains.";
        }

        return $"Cover skipped: {target} is not in the cover-host allowlist. Add it in the extension's settings.";
    }

    /// <summary>The listed non-wildcard entry <paramref name="target"/> sits under, if there is one.</summary>
    private string? ListedApexOf(string target)
    {
        foreach (var entry in _hosts())
        {
            if (entry.StartsWith(CoverHostSetting.WildcardPrefix, StringComparison.Ordinal))
                continue;

            if (target.Length > entry.Length + 1
                && target.EndsWith($".{entry}", StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}
