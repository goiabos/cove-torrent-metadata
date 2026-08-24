namespace Cove.TorrentMetadata;

/// <summary>
/// Stamps every cover request with a User-Agent that identifies this extension.
///
/// One of the three conditions the tracker's staff attached to clearing publication: a "proper and
/// unique user-agent so it can be identified and blocked if something goes wrong". The point is
/// therefore not politeness — it is that whoever is on the other end can attribute this traffic and
/// stop it specifically, rather than having to block a whole Cove instance or an IP.
///
/// It is a <see cref="DelegatingHandler"/> on the named client rather than a header set inside the
/// fetch, because the fetch has two callers today — single apply and bulk apply — and the
/// registration is one place. A third caller added later gets the header without knowing it needed
/// one, which is the property worth having here: a missing User-Agent is invisible from this side
/// and only ever visible to the tracker.
///
/// The version is read at request time rather than baked in, so it cannot drift from
/// <c>extension.json</c> — the file the packaging script and the release workflow both treat as the
/// authority on what version shipped.
///
/// The contact URL is the published repository, so whoever reads this header in a log can reach
/// the project it belongs to. The reply to staff promises exactly this string, so the UA they
/// observe is the one that was described to them. It must never point at anything under the
/// author's main account.
/// </summary>
public sealed class CoverUserAgentHandler(Func<string> version) : DelegatingHandler
{
    /// <summary>
    /// The product token. Deliberately stable across versions, and deliberately not the manifest id:
    /// this is the string a tracker would write a block rule against, so changing it would silently
    /// void whatever rule they wrote.
    /// </summary>
    public const string Product = "TorrentMetadata";

    /// <summary>
    /// Where the header points anyone who wants to know what is making these requests. A constant
    /// beside <see cref="Product"/> because the two together are the promise made to staff: a string
    /// they can identify, and an address behind it.
    /// </summary>
    public const string ContactUrl = "https://github.com/goiabos/cove-torrent-metadata";

    /// <summary>
    /// The exact header value. Public because the tests and the text sent to staff both need it, and
    /// three hand-written copies of one format string is how the promise and the behaviour diverge.
    /// </summary>
    public static string Format(string? version) => $"{Product}/{Token(version)} (+{ContactUrl})";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Cleared first. Whatever a default or another handler would otherwise contribute, what the
        // tracker sees has to be ours alone — a list of products turns their block rule into a parse.
        request.Headers.UserAgent.Clear();
        request.Headers.Add("User-Agent", Format(version()));
        return base.SendAsync(request, ct);
    }

    /// <summary>
    /// Reduces a version to characters valid in an HTTP token.
    ///
    /// Defensive rather than expected: the manifest carries a semver string and every character of
    /// one is already legal here. It earns its place because a rejected header value throws inside
    /// <see cref="SendAsync"/>, and the cover fetch swallows every exception — so a malformed version
    /// would turn into covers silently not importing, which is the exact failure the allowlist removed.
    /// </summary>
    private static string Token(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "0";

        var token = new string([.. version.Where(c => char.IsAsciiLetterOrDigit(c) || "-._~+".Contains(c))]);
        return token.Length > 0 ? token : "0";
    }
}
