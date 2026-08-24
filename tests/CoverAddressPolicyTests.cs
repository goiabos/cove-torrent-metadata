using System.Net;
using Cove.TorrentMetadata;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// Where a cover fetch is allowed to connect.
///
/// The allowlist compares names, and a name is not where the packet goes: the socket resolves it
/// again at connect time, so a host that answers public when the allowlist checks it and
/// <c>127.0.0.1</c> when the socket connects is fetched regardless. That is DNS rebinding, the URL
/// comes out of an untrusted <c>.torrent</c>, and every redirect hop is a fresh connection and a
/// fresh chance to do it.
///
/// Driven against the policy rather than through a socket on purpose.
/// <c>SocketsHttpConnectionContext</c> cannot be constructed by a test, so a rule that lived inside
/// the connect callback would be a security check nothing could reach — which is why the callback
/// decides nothing and this does all of it.
/// </summary>
public class CoverAddressPolicyTests
{
    // ---------------------------------------------------------------------
    // Which addresses are off limits
    // ---------------------------------------------------------------------

    [Theory]
    // The one that is not merely internal: the cloud metadata service answers unauthenticated and
    // hands out instance credentials, and it is reachable from anything that can make a request.
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("192.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    // Loopback wearing an IPv6 hat. Read as a v6 prefix rather than unwrapped, its bytes look like
    // ordinary global unicast — which is exactly why it is checked before the family switch.
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    // The transition prefixes embed an IPv4 address inside a global-looking v6 one, so a public
    // prefix can name an internal v4 host. Refused whole rather than decoded.
    [InlineData("2002:7f00:1::")]
    [InlineData("2001::1")]
    [InlineData("64:ff9b::7f00:1")]
    public void Refuses_an_address_on_this_servers_own_network(string address)
        => Assert.True(CoverAddressPolicy.IsInternal(IPAddress.Parse(address)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("198.20.0.1")]
    [InlineData("223.255.255.255")]
    [InlineData("2606:4700::1111")]
    [InlineData("2a00:1450:4009::200e")]
    public void Allows_an_ordinary_public_address(string address)
        => Assert.False(CoverAddressPolicy.IsInternal(IPAddress.Parse(address)));

    // ---------------------------------------------------------------------
    // Resolving, which is the half a name check cannot do
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Refuses_a_name_that_resolves_onto_this_servers_own_network()
    {
        // The rebinding shape: the allowlist saw a name it was happy with, and the lookup answers
        // with the address the attacker actually wanted reached.
        await Assert.ThrowsAsync<CoverAddressRefusedException>(() =>
            CoverAddressPolicy.ResolveAsync("images.example.invalid", Resolves("127.0.0.1")).AsTask());
    }

    [Fact]
    public async Task Refuses_a_name_that_answers_with_one_good_address_and_one_bad_one()
    {
        // Any, not "the ones that are not". A rebinding answer is a mix — one public address to pass
        // whatever check is looking and one internal address to be connected to — and filtering would
        // leave the outcome to whichever the socket happened to pick.
        await Assert.ThrowsAsync<CoverAddressRefusedException>(() =>
            CoverAddressPolicy.ResolveAsync("images.example.invalid", Resolves("93.184.216.34", "169.254.169.254")).AsTask());
    }

    [Fact]
    public async Task Refuses_a_name_that_resolves_to_nothing()
    {
        await Assert.ThrowsAsync<CoverAddressRefusedException>(() =>
            CoverAddressPolicy.ResolveAsync("images.example.invalid", Resolves()).AsTask());
    }

    [Fact]
    public async Task Hands_back_the_addresses_it_checked_so_the_connect_needs_no_second_lookup()
    {
        var resolved = await CoverAddressPolicy.ResolveAsync(
            "images.example.invalid", Resolves("93.184.216.34", "2606:4700::1111"));

        // The whole point of resolve-then-pin: the socket is given these, not the name, so there is
        // no second lookup for an attacker to answer differently between the check and the connect.
        Assert.Equal(
            [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:4700::1111")],
            resolved);
    }

    [Fact]
    public async Task Checks_a_literal_without_resolving_it()
    {
        var resolver = new CountingResolver();

        await Assert.ThrowsAsync<CoverAddressRefusedException>(() =>
            CoverAddressPolicy.ResolveAsync("169.254.169.254", resolver.ResolveAsync).AsTask());

        // A literal has no lookup to disagree with itself, but it still has to pass: an allowlist
        // written before the settings-time check landed is still on disk.
        Assert.Equal(0, resolver.Calls);
    }

    private static Func<string, CancellationToken, ValueTask<IPAddress[]>> Resolves(params string[] addresses) =>
        (_, _) => ValueTask.FromResult(Array.ConvertAll(addresses, IPAddress.Parse));

    private sealed class CountingResolver
    {
        public int Calls { get; private set; }

        public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult<IPAddress[]>([IPAddress.Loopback]);
        }
    }
}
