namespace Cove.TorrentMetadata;

/// <summary>
/// A fetched cover, or the reason there is not one.
///
/// Bytes rather than a stored blob, because the two callers want different things with them: an
/// import writes them to the blob store, a preview writes them to the response. Blob storage is
/// deliberately not this type's business — a previewed cover is by definition an image no video
/// references yet, and the host garbage-collects unreferenced blobs.
/// </summary>
/// <param name="Bytes">The image, or null when nothing was fetched.</param>
/// <param name="ContentType">The media type the host served, kept verbatim so an animated WebP stays one.</param>
/// <param name="Skipped">Why there are no bytes, phrased for the reviewer. Null on success.</param>
/// <param name="RetryAfter">Set only when the rate limiter refused: how long to leave it.</param>
public readonly record struct CoverBytes(
    byte[]? Bytes,
    string? ContentType,
    string? Skipped,
    TimeSpan? RetryAfter);

/// <summary>
/// The outbound cover request itself: allowlist re-checked on every hop, size bounded while
/// streaming, and everything shaped by the named client so the User-Agent and the rate limiter apply.
///
/// It exists as one place on purpose. Import and preview both need this exact sequence, and a second
/// copy of the redirect check is how the allowlist grows a hole later — the check has to survive a
/// 302, and a caller that forgot it would still look correct in review.
///
/// Never throws: a cover is the least important thing in a proposal, and an unreachable image host
/// must not cost the user the tags they just approved.
/// </summary>
public static class CoverFetcher
{
    /// <summary>Cap on a fetched cover. Real ones are well under this; the limit bounds a hostile response.</summary>
    public const long MaxCoverBytes = 16 * 1024 * 1024;

    /// <summary>Redirect hops followed before giving up. Real image hosts use one, at most.</summary>
    private const int MaxCoverRedirects = 3;

    /// <summary>
    /// The one generic reason. Everything past the allowlist is transient or hostile-input, and
    /// enumerating those to the reviewer would describe the image host's behaviour rather than
    /// anything they can act on.
    /// </summary>
    public const string Unfetchable = "Cover skipped: the image could not be fetched.";

    /// <summary>
    /// The one refusal past the allowlist that is worth naming, for the same reason a redirect off the
    /// allowlist is: it describes something the image host did that we declined to follow, not
    /// something transient the reviewer should retry.
    /// </summary>
    public const string Downgraded =
        "Cover skipped: the image host redirected an https cover to an insecure http address.";

    /// <summary>
    /// Concrete raster content types this fetcher will store or serve — never a prefix test. A prefix
    /// test on <c>"image/"</c> admits <c>image/svg+xml</c>, and an SVG is not a raster image: it is an
    /// active document that runs script, and its own <c>&lt;image href&gt;</c>/<c>&lt;use&gt;</c>/CSS
    /// references are fetched by the *browser* with none of this pipeline's allowlist, User-Agent or
    /// pacing — the exact failure this whole fetcher was built to close, reopened one content
    /// type at a time. Every entry here is inert: none can carry script or reach a second URL.
    ///
    /// <c>image/jpg</c> is included alongside the correct <c>image/jpeg</c>. It is not a standard media
    /// type, but real image hosts send it, and it is still an inert raster spelling — accepting it
    /// costs nothing this comment doesn't already cover. Nothing that can be an active document
    /// belongs on this list, whatever an image host calls it.
    /// </summary>
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/gif",
        "image/webp",
        "image/avif",
    };

    /// <summary>
    /// Whether <paramref name="contentType"/> is a raster type this pipeline will store or serve.
    /// Shared by every path that can hand bytes to the browser — a fresh fetch, and the cache/blob-store
    /// reads that can return a type this policy predates (see <see cref="CoverProxyService"/>) — so the
    /// policy lives in exactly one place rather than being re-derived per caller.
    /// </summary>
    public static bool IsSafeRasterContentType(string? contentType) =>
        contentType is not null && AllowedContentTypes.Contains(contentType);

    /// <summary>
    /// Fetches <paramref name="uri"/>, with the allowlist already satisfied for the first hop and the
    /// caches already missed.
    ///
    /// <paramref name="maxWait"/> caps how long the request may sit at the rate limiter's gate; null
    /// takes <see cref="CoverRateLimiter.MaxWait"/>. A preview passes
    /// <see cref="CoverRateLimiter.PreviewMaxWait"/> instead — that field owns the reason a preview
    /// refuses early rather than waiting.
    /// </summary>
    public static async Task<CoverBytes> FetchAsync(
        Uri uri,
        IHttpClientFactory httpClients,
        CoverHostAllowlist coverHosts,
        TimeSpan? maxWait = null,
        CancellationToken ct = default)
    {
        try
        {
            using var client = httpClients.CreateClient(TorrentApplyService.CoverHttpClientName);
            // Covers the rate limiter's wait as well as the transfer: the gate can hold a request for
            // up to CoverRateLimiter.MaxWait before it is sent, and a timeout that did not allow for
            // that would cancel requests for being polite.
            client.Timeout = TimeSpan.FromSeconds(60);

            var current = uri;
            for (var hop = 0; hop <= MaxCoverRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                if (maxWait is { } bounded)
                    request.Options.Set(CoverRateLimitHandler.MaxWaitOption, bounded);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                // Followed by hand so the allowlist survives a 302. A redirect is the obvious way around
                // a URL check: the declared host answers, and points somewhere internal.
                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location is null)
                        return Failed(Unfetchable);

                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);

                    // The allowlist checks the host, not the transport, so without this a hop from
                    // https to http *on an allowed host* was followed silently — putting the request
                    // and the identifying User-Agent on the wire in cleartext at a redirect the
                    // attacker chose. Never followed downward; upward is fine.
                    if (current.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
                        return Failed(Downgraded);

                    // Named separately from the first check: a redirect off the allowlist is the
                    // attack this hand-rolled hop-following exists to stop, and reporting it as
                    // "could not be fetched" would hide the one refusal worth noticing.
                    if (!coverHosts.Allows(next))
                        return Failed(coverHosts.Explain(next));

                    current = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return Failed(Unfetchable);

                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (!IsSafeRasterContentType(contentType))
                    return Failed(Unfetchable);
                if (response.Content.Headers.ContentLength is > MaxCoverBytes)
                    return Failed(Unfetchable);

                // Buffered so an over-long body is rejected even when the server declared no length.
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(chunk, ct)) > 0)
                {
                    if (buffer.Length + read > MaxCoverBytes)
                        return Failed(Unfetchable);
                    await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
                }

                if (buffer.Length == 0)
                    return Failed(Unfetchable);

                return new CoverBytes(buffer.ToArray(), contentType, null, null);
            }

            // Out of hops. A chain this long is either a loop or a host doing something we should not
            // be following anyway.
            return Failed(Unfetchable);
        }
        catch (CoverThrottledException throttled)
        {
            // Reported as itself rather than folded into "could not be fetched". This is the one
            // failure that is neither the user's fault nor permanent — the answer is to try later,
            // and saying so is what stops it reading as a broken cover.
            return new CoverBytes(null, null, throttled.Reason, throttled.RetryAfter);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Let it out, rather than folding it into "could not be fetched". The caller cannot
            // otherwise tell "the image host has no such cover" from "the browser navigated away
            // mid-request", and it negative-caches the first — so closing the review dialog used to
            // mark that cover dead for every row and every apply for ten minutes.
            //
            // Guarded on the caller's token because HttpClient reports its own timeout as a
            // TaskCanceledException with nothing cancelled: that one really is a failed fetch.
            throw;
        }
        catch (Exception)
        {
            return Failed(Unfetchable);
        }
    }

    private static CoverBytes Failed(string reason) => new(null, null, reason, null);

    /// <summary>
    /// The redirect statuses followed by hand above.
    ///
    /// Public because <see cref="CoverRateLimitHandler"/> has to agree with it exactly: a hop this
    /// list names is a request the fetcher will make again, so the limiter must not score it as a
    /// failed cover. Two copies of this list would drift, and the drift would be silent —
    /// a status one side follows and the other counts against the breaker.
    /// </summary>
    public static bool IsRedirect(System.Net.HttpStatusCode status) => status is
        System.Net.HttpStatusCode.MovedPermanently
        or System.Net.HttpStatusCode.Found
        or System.Net.HttpStatusCode.SeeOther
        or System.Net.HttpStatusCode.TemporaryRedirect
        or System.Net.HttpStatusCode.PermanentRedirect;
}
