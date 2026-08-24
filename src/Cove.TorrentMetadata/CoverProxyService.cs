using System.Net;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Cove.TorrentMetadata;

/// <summary>What the proxy endpoint should answer with.</summary>
/// <param name="Status">The HTTP status. 200 is the only one carrying bytes.</param>
/// <param name="Bytes">The image, on success.</param>
/// <param name="ContentType">The media type to serve it as.</param>
/// <param name="Error">Why not, phrased for the reviewer — the same text the apply path would report.</param>
/// <param name="RetryAfter">Set on 429 only, and turned into the header of that name.</param>
public readonly record struct CoverProxyResult(
    HttpStatusCode Status,
    byte[]? Bytes,
    string? ContentType,
    string? Error,
    TimeSpan? RetryAfter);

/// <summary>
/// Serves a torrent's cover to the review UI through the same pipeline an import uses.
///
/// This exists because the UI used to point an <c>&lt;img&gt;</c> straight at the URL out of the
/// torrent, so the *browser* fetched it. Nothing in the cover machinery was involved: no
/// allowlist, no <c>User-Agent</c>, no pacing, no cache. Three of the four conditions the tracker's
/// staff attached to clearance are about exactly those requests — traffic they can identify, traffic
/// that is paced, and images that are not re-downloaded — and preview traffic met none of them. It was
/// also a request made *before* the user had named the host, on a page whose own notice said the
/// extension "only requests images from hosts you have named".
///
/// Routing it through here makes preview and import the same request. A previewed cover warms the
/// caches, so ticking the box afterwards costs nothing at all.
///
/// **This is not an open proxy.** It takes a URL, but it can only ever fetch from a host the operator
/// has explicitly named, and the list ships empty — so it grants no reach the import path did not
/// already grant. The allowlist check is what makes a URL-keyed endpoint safe here, and it is the
/// first thing that happens after the URL parses.
///
/// It is a **thin adapter** now, and deliberately so: the resolution sequence lives in
/// <see cref="CoverResolver"/>, and everything below is the translation from "what happened" into the
/// three things only HTTP cares about — a status code, a content type, and a <c>Retry-After</c>. This
/// class and <see cref="TorrentApplyService"/> each held their own copy of that sequence and had
/// already drifted three ways; a copy is the only way it can drift again.
/// </summary>
public sealed class CoverProxyService(
    CoverResolver? covers = null, IBlobService? blobs = null, ILogger<CoverProxyService>? log = null)
{
    public async Task<CoverProxyResult> GetAsync(string? url, CancellationToken ct = default)
    {
        var answer = await AnswerAsync(url, ct);

        // The reason reaches the log as well as the response body. Every refusal used to exist only
        // as `{"error": …}` in an answer one browser read once — from the outside each was an
        // indistinguishable status code, and this was diagnosed by pasting the body out of DevTools
        // while the log said nothing however many covers were declined. One line per refusal,
        // at Information because Debug is off exactly when it is needed, and unaggregated because
        // refusals are rare by construction now: the client asks serially and obeys Retry-After
        // page-wide, and the caller that starts a slow fetch is no longer refused by it.
        // The sentence names the host where one is involved; the URL stays out on purpose — a log
        // file should carry no more of the tracker than the reason needs.
        if (answer.Status != HttpStatusCode.OK && log is not null)
            log.LogInformation("Cover refused with {Status}: {Reason}", (int)answer.Status, answer.Error);

        return answer;
    }

    private async Task<CoverProxyResult> AnswerAsync(string? url, CancellationToken ct)
    {
        // Failing closed on an unwired resolver for the same reason the apply path does: the sequence
        // it holds is the only thing standing in front of the request.
        if (covers is null)
            return Refused(HttpStatusCode.Forbidden, "Cover skipped: no cover hosts are configured.");

        // PreviewMaxWait, not the import's twenty seconds: covers reach this endpoint one at a time
        // through the client's serial queue, so a request parked at the gate stalls every cover
        // behind it on a page the user is looking at. Refusing early costs no extra host request —
        // the answer comes from this side, and the retry finds the cache warmed.
        var resolved = await covers.ResolveAsync(url, blobs, CoverRateLimiter.PreviewMaxWait, ct);

        // A blob a sibling scene already imported. Reading it back is the whole point of previewing
        // through the server: the browser gets the image and the image host is not asked a second time.
        if (resolved.BlobId is { } blobId)
        {
            if (blobs is not null && await ReadBlobAsync(blobs, blobId, ct) is { } stored)
            {
                // Re-checked rather than trusted: a blob written before CoverFetcher's raster allowlist
                // existed can carry a content type today's policy refuses, and this is the one read
                // path a fetch-time filter cannot reach. Refused rather than re-fetched — the
                // blob is what is wrong, not the request.
                return CoverFetcher.IsSafeRasterContentType(stored.ContentType)
                    ? Served(stored.Bytes, stored.ContentType)
                    : Refused(HttpStatusCode.BadGateway, CoverFetcher.Unfetchable);
            }

            // The blob went missing between the cache's existence check and this read. One request
            // costs less than a failed page, and the cache prunes itself on the next look.
            return Refused(HttpStatusCode.BadGateway, CoverFetcher.Unfetchable);
        }

        if (resolved.Bytes is { } bytes && resolved.ContentType is { } contentType)
        {
            return CoverFetcher.IsSafeRasterContentType(contentType)
                ? Served(bytes, contentType)
                : Refused(HttpStatusCode.BadGateway, CoverFetcher.Unfetchable);
        }

        var reason = resolved.Skipped ?? CoverFetcher.Unfetchable;

        // The status is picked from the refusal *kind* rather than from the message text, so the two
        // never drift: an <img> reacts to the code, and the batch page's retry rides on the 429.
        return resolved.Refusal switch
        {
            // Not a URL this could ever fetch — the caller got it wrong. Worded for a caller rather
            // than for the reviewer, which is why it is not the resolver's own sentence.
            CoverRefusal.Malformed => Refused(HttpStatusCode.BadRequest, "That is not a usable cover URL."),
            CoverRefusal.NotAllowed => Refused(HttpStatusCode.Forbidden, reason),
            CoverRefusal.Throttled =>
                new CoverProxyResult(HttpStatusCode.TooManyRequests, null, null, reason, resolved.RetryAfter),
            _ => Refused(HttpStatusCode.BadGateway, reason),
        };
    }

    private static CoverProxyResult Served(byte[] bytes, string contentType) =>
        new(HttpStatusCode.OK, bytes, contentType, null, null);

    private static CoverProxyResult Refused(HttpStatusCode status, string error) =>
        new(status, null, null, error, null);

    /// <summary>
    /// Reads a blob out in full. Null on anything at all going wrong, so a blob store that has lost
    /// the file costs a fetch rather than a failed page.
    /// </summary>
    private static async Task<(byte[] Bytes, string ContentType)?> ReadBlobAsync(
        IBlobService blobs, string blobId, CancellationToken ct)
    {
        try
        {
            var blob = await blobs.GetBlobAsync(blobId, ct);
            if (blob is null)
                return null;

            await using var stream = blob.Value.Stream;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            return (buffer.ToArray(), blob.Value.ContentType);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
