/**
 * What the server actually answered, turned into either the payload or the message a user sees.
 *
 * `response.ok` alone does not cover the failure this extension actually hits: an unmapped
 * `/api/*` path in Cove falls through to `MapFallbackToFile("index.html")` and comes back with
 * **HTTP 200**, not a 404. A stale `BASE` in `api.ts` — or any
 * other bug that makes a call miss its endpoint — therefore looks *successful* to an `ok` check
 * and dies instead on `JSON.parse("<!DOCTYPE ...")`, throwing `Unexpected token '<' ...` rather
 * than the prepared message. The only thing that tells the SPA shell apart from a real answer is
 * its content type, so that is what gets checked, before any parsing happens — on both the success
 * and the error path, since a non-JSON body can arrive either way (a proxy's own error page, a
 * truncated response, JSON that failed to serialize server-side).
 *
 * No React and no `@cove/runtime/*` here, so it is reachable from a test without a DOM or a
 * stand-in for the host runtime — `send` and `uploadOne` in `api.ts` are its only two callers,
 * kept thin so this decision lives in one place rather than twice.
 */
export async function readApiResponse<T>(response: Response, fallbackMessage: string): Promise<T> {
  const contentType = response.headers.get("content-type") ?? "";
  const looksLikeJson = contentType.includes("application/json");
  const text = await response.text();

  if (!text) {
    if (!response.ok) throw new Error(fallbackMessage);
    return null as T;
  }

  if (!looksLikeJson) {
    // Includes the SPA-fallback case: HTTP 200, `content-type: text/html`, body is `index.html`.
    throw new Error(fallbackMessage);
  }

  let payload: unknown;
  try {
    payload = JSON.parse(text);
  } catch {
    throw new Error(fallbackMessage);
  }

  if (!response.ok) {
    throw new Error((payload as { error?: string } | null)?.error ?? fallbackMessage);
  }
  return payload as T;
}
