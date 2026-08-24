using System.Net;
using System.Net.Sockets;

namespace Cove.TorrentMetadata;

/// <summary>
/// Raised when a cover host resolved somewhere the fetch must never connect to.
///
/// An exception rather than a return value because the refusal happens inside
/// <see cref="SocketsHttpHandler.ConnectCallback"/>, which has nowhere else to put an answer. It
/// surfaces to the reviewer as <see cref="CoverFetcher.Unfetchable"/> like any other transport
/// failure, which is the right amount to say: a name that answers public on one lookup and internal
/// on the next is an attack, and describing the internal address back to whoever crafted the torrent
/// tells them what the server can see.
/// </summary>
public sealed class CoverAddressRefusedException(string message) : Exception(message);

/// <summary>
/// Which IP addresses a cover may be fetched from, and the connect that pins the answer.
///
/// The allowlist in <see cref="CoverHostAllowlist"/> compares *names*, and a name is not where the
/// packet goes. The socket does its own DNS at connect time, so a host that answers public when the
/// allowlist checks it and <c>127.0.0.1</c> when the socket connects is fetched anyway — classic DNS
/// rebinding, and re-opened on every redirect hop because each hop is its own connection.
/// The cover URL comes out of an untrusted <c>.torrent</c>, so the name is attacker-chosen.
///
/// The fix is resolve-then-pin: resolve once, check every address the name gave back, and connect to
/// *those addresses* rather than to the name. There is no second lookup for an attacker to answer
/// differently.
///
/// Split from the connect callback on purpose. <c>SocketsHttpConnectionContext</c> cannot be
/// constructed by a test, so a policy living inside the callback would be a security rule nothing
/// could reach; <see cref="IsInternal"/> and <see cref="ResolveAsync"/> are plain functions, and
/// <see cref="ConnectAsync"/> is the thin wiring that does no deciding of its own.
/// </summary>
public static class CoverAddressPolicy
{
    /// <summary>
    /// True when this address is somewhere the cover fetch must never go: the server's own machine,
    /// the network it sits on, or a range that is not a public internet destination at all.
    ///
    /// Fail-closed on anything that is not IPv4 or IPv6 — an address family we did not ask for is not
    /// something to connect to on a guess.
    /// </summary>
    public static bool IsInternal(IPAddress address)
    {
        // Checked before the family switch: ::ffff:127.0.0.1 is loopback wearing an IPv6 hat, and a
        // v6-only check would read its bytes as an ordinary global-unicast prefix.
        if (address.IsIPv4MappedToIPv6)
            return IsInternal(address.MapToIPv4());

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsInternalV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsInternalV6(address.GetAddressBytes()),
            _ => true,
        };
    }

    /// <summary>
    /// The IPv4 ranges that are not a public destination.
    ///
    /// <c>169.254.0.0/16</c> is the one worth naming: it carries the cloud metadata service at
    /// <c>169.254.169.254</c>, which answers unauthenticated and hands out instance credentials. It
    /// is the reason this file exists rather than a loopback check.
    /// </summary>
    private static bool IsInternalV4(byte[] b) => b[0] switch
    {
        0 => true,                                   // 0.0.0.0/8 — "this network"; 0.0.0.0 itself reaches localhost
        10 => true,                                  // 10.0.0.0/8 private
        127 => true,                                 // 127.0.0.0/8 loopback
        100 when b[1] is >= 64 and <= 127 => true,   // 100.64.0.0/10 carrier-grade NAT
        169 when b[1] == 254 => true,                // 169.254.0.0/16 link-local, incl. the metadata service
        172 when b[1] is >= 16 and <= 31 => true,    // 172.16.0.0/12 private
        192 when b[1] == 0 && b[2] == 0 => true,     // 192.0.0.0/24 IETF protocol assignments
        192 when b[1] == 168 => true,                // 192.168.0.0/16 private
        198 when b[1] is 18 or 19 => true,           // 198.18.0.0/15 benchmarking
        >= 224 => true,                              // 224/4 multicast and 240/4 reserved, incl. 255.255.255.255
        _ => false,
    };

    /// <summary>
    /// The IPv6 equivalents, plus the transition prefixes.
    ///
    /// 6to4, Teredo and NAT64 each embed an IPv4 address inside a v6 one, so a global-looking address
    /// in those prefixes can name an internal v4 host. They are refused whole rather than decoded:
    /// they are effectively dead on the public internet, and a decoder is a second place for this
    /// rule to be subtly wrong.
    /// </summary>
    private static bool IsInternalV6(byte[] b)
    {
        // ::, ::1 and the deprecated ::a.b.c.d form all sit under ten zero bytes. The v4-mapped
        // ::ffff:a.b.c.d form has already been unwrapped by the caller.
        if (b.Take(10).All(octet => octet == 0))
            return true;

        return b[0] switch
        {
            0x00 when b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B => true,   // 64:ff9b::/96 NAT64
            0x20 when b[1] == 0x02 => true,                                   // 2002::/16 6to4
            0x20 when b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00 => true,   // 2001:0::/32 Teredo
            0xFE when (b[1] & 0xC0) == 0x80 => true,                          // fe80::/10 link-local
            0xFE when (b[1] & 0xC0) == 0xC0 => true,                          // fec0::/10 site-local (deprecated)
            0xFF => true,                                                     // ff00::/8 multicast
            _ => (b[0] & 0xFE) == 0xFC,                                       // fc00::/7 unique local
        };
    }

    /// <summary>
    /// Resolves <paramref name="host"/> and returns the addresses, or throws if any of them is
    /// internal.
    ///
    /// **Any**, not "the ones that are not". A rebinding answer is a mix — one public address to pass
    /// whatever check is looking, one internal address to be connected to — and filtering would leave
    /// the outcome to whichever the socket happened to pick. A real image host does not resolve to
    /// <c>127.0.0.1</c>, so refusing the whole name costs nothing legitimate and is a rule that can be
    /// stated in one sentence.
    ///
    /// <paramref name="resolve"/> exists so a test can drive this without a DNS server; production
    /// passes null and gets <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>.
    /// </summary>
    public static async ValueTask<IPAddress[]> ResolveAsync(
        string host,
        Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolve = null,
        CancellationToken ct = default)
    {
        // A literal is not resolved at all, so there is no lookup to disagree with itself — but it
        // still has to pass, because a stored allowlist entry predating this check is still on disk.
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = resolve is null
                ? await Dns.GetHostAddressesAsync(host, ct)
                : await resolve(host, ct);
        }

        if (addresses.Length == 0)
            throw new CoverAddressRefusedException($"Cover refused: {host} does not resolve.");

        foreach (var address in addresses)
        {
            if (IsInternal(address))
                throw new CoverAddressRefusedException($"Cover refused: {host} resolves onto this server's own network.");
        }

        return addresses;
    }

    /// <summary>
    /// The <see cref="SocketsHttpHandler.ConnectCallback"/> the cover client is registered with.
    ///
    /// Connecting to the resolved addresses rather than to the name is the whole point: this is the
    /// only place the packet's destination is decided, and it is decided from the list that was just
    /// checked. TLS is unaffected — the handler layers it on top of this stream and still validates
    /// the certificate against the request's hostname, so pinning the address does not weaken the
    /// certificate check.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken ct)
    {
        var endPoint = context.DnsEndPoint;
        var addresses = await ResolveAsync(endPoint.Host, ct: ct);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
