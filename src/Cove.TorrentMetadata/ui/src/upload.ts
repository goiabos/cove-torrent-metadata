/**
 * Splitting a torrent upload into requests the host will actually accept, and putting the answers
 * back together.
 *
 * A folder of torrents is the normal thing to drop on the batch page — the measured corpus is 3,218
 * of them — and every file used to go into one multipart request. Cove sets neither
 * `MaxRequestBodySize` nor `FormOptions`, so Kestrel's 30,000,000-byte default applied and a real
 * folder came back as `Upload failed (413).` with nothing to act on: a bound nobody chose, reported
 * by nobody.
 *
 * Chunking is what makes the server's own file-count cap meaningful, too. A cap that only ever fires
 * on requests the client should not have sent is a cap guarding a door that is already shut.
 *
 * No React and no `@cove/runtime/*` here, so the packing is reachable from a test — the same rule
 * that put `review.ts`, `payload.ts`, `naming.ts` and `coverHosts.ts` where they are.
 */

/**
 * Whether a dropped file is one this extension will send.
 *
 * The extension of a torrent, and nothing more. The server re-checks it and also parses the bytes,
 * so this is not the guard — it is what decides whether a drop had anything in it at all, which is
 * the difference between "nothing happened" and a message saying why.
 *
 * One definition because two surfaces make this judgement: the batch page's drop area and the
 * per-video drop zone. They each carried their own copy of the test *and* of the sentence below, and
 * the copies are exactly the kind that drift into disagreeing about the same drop.
 */
export const isTorrentFile = (file: { name: string }): boolean =>
  file.name.toLowerCase().endsWith(".torrent");

/**
 * What a drop carrying no torrent is told.
 *
 * Two wordings rather than one, and that is the point of naming them here. The batch page takes a
 * folder-sized drop and the per-video zone takes a single file, so one sentence would be wrong on one
 * of them. What must not differ is the *rule* above that decides when either is shown — which is what
 * was actually duplicated, and what would have drifted.
 */
export const NOT_TORRENTS_IN_DROP = "No .torrent files in that drop.";
export const NOT_A_TORRENT = "That is not a .torrent file.";

/**
 * Files per request. Deliberately well under the server's own cap
 * (`TorrentMetadataExtension.MaxTorrentUploadFiles`), so a large drop is handled by *splitting* it
 * rather than by having most of it refused.
 */
export const UPLOAD_CHUNK_FILES = 100;

/**
 * Bytes per request, counting file contents only.
 *
 * Kestrel's default ceiling is ~28.6 MB and multipart adds a boundary and headers per part, so this
 * leaves room rather than aiming at the limit. It is not a rule about torrents — the per-file cap is
 * 8 MB and lives on the server — it is only how much is sent at once.
 */
export const UPLOAD_CHUNK_BYTES = 16 * 1024 * 1024;

/**
 * Greedy packing: fill a request until the next file would breach either limit, then start another.
 *
 * A single file larger than `maxBytes` still goes on its own rather than being dropped here. Whether
 * it is acceptable is the server's judgement, and silently discarding it client-side would report
 * fewer files than the user selected with no reason given for the difference.
 */
export function chunkForUpload<T extends { size: number }>(
  items: readonly T[],
  maxItems: number = UPLOAD_CHUNK_FILES,
  maxBytes: number = UPLOAD_CHUNK_BYTES,
): T[][] {
  const chunks: T[][] = [];
  let current: T[] = [];
  let bytes = 0;

  for (const item of items) {
    const wouldExceed = current.length >= maxItems || (current.length > 0 && bytes + item.size > maxBytes);
    if (wouldExceed) {
      chunks.push(current);
      current = [];
      bytes = 0;
    }

    current.push(item);
    bytes += item.size;
  }

  if (current.length > 0) chunks.push(current);
  return chunks;
}

/** One request's answer, as the upload endpoint shapes it. */
export interface UploadResult {
  saved: number;
  rejected: string[];
  added: Array<{ torrentName: string; fileName: string; fanOut: number }>;
  torrents: number;
  files: number;
}

/**
 * Folds several requests' answers into the one the callers already expect.
 *
 * `saved`, `rejected` and `added` accumulate, because they describe what was uploaded. `torrents` and
 * `files` do not: they are the *index totals* after the reload each request ends with, so the last
 * response holds the only true figures and summing them would report the folder several times over.
 */
export function mergeUploadResults(results: readonly UploadResult[]): UploadResult {
  const last = results.at(-1);
  return {
    saved: results.reduce((total, result) => total + result.saved, 0),
    rejected: results.flatMap((result) => result.rejected),
    added: results.flatMap((result) => result.added),
    torrents: last?.torrents ?? 0,
    files: last?.files ?? 0,
  };
}
