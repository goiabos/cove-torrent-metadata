import { extensionFetch } from "@cove/runtime/api";
import { chunkForUpload, mergeUploadResults, type UploadResult } from "./upload";
import type { FolderStateReport } from "./folderState";
import type { ReloadReport } from "./reloadStatus";
import type { FolderTorrent } from "./writeFolder";
import { readApiResponse } from "./response";

// Must match the endpoint constants in TorrentMetadataExtension.cs. A stale base here still fails every
// call — unmapped /api/* paths fall through to the SPA index.html, HTTP 200 — but no longer as a raw
// JSON.parse crash: `readApiResponse` in response.ts checks content type before it parses anything, so
// a stale base now surfaces as "Request failed (200)." rather than "Unexpected token '<' ...".
const BASE = "/api/extensions/torrent-metadata";

/**
 * The URL to render a torrent's cover from.
 *
 * **Never point an `<img>` — or any other fetch — at a cover URL out of a torrent directly.** That
 * was the original defect: the browser fetched the image itself, so none of the cover machinery applied. No
 * allowlist (the image loaded under the very notice saying it would not), no `TorrentMetadata/x.y`
 * User-Agent, no pacing, and no cache — which is three of the four conditions the tracker's staff
 * cleared publication on, bypassed by a page render. Everything goes through this endpoint, which is
 * the same pipeline an import uses.
 *
 * No `extensionFetch` here because an `<img>` cannot send an Authorization header. It does not need
 * one: this is same-origin, so the host's `cove_access_token` cookie authenticates it, exactly as it
 * does for the built-in `/api/videos/{id}/image` URLs rendered beside it.
 */
export const coverUrl = (torrentCoverUrl: string) =>
  `${BASE}/cover?url=${encodeURIComponent(torrentCoverUrl)}`;

export interface ProposedRelation {
  name: string;
  source: string | null;
  matchesExisting: boolean;
  alreadyApplied: boolean;
}

/**
 * Mirrors `ProposedPerformer` in `TorrentMatchService.cs`.
 *
 * No `matchesExisting`, because there is no other kind: the server only proposes performers the
 * library already holds, and the apply addresses them by `id` rather than by name. `source` is
 * the tag-list entry that found them, and is set only when an alias did and their own name never
 * appeared — the one case a reviewer could not otherwise account for the name they are reading.
 */
export interface ProposedPerformer {
  id: number;
  name: string;
  source: string | null;
  alreadyApplied: boolean;
}

/** A studio the reviewer may choose, and the domain that found it. */
export interface ProposedStudio {
  /** What the library calls it. */
  name: string;
  /** The tracker's spelling of the site tag — `lanternbay.com`. */
  source: string;
}

export interface TorrentMatchProposal {
  videoId: number;
  torrentName: string;
  fileName: string;
  matchedOn: string;
  fanOut: number;
  /**
   * The torrent's raw tag-list size. Not shown anywhere — it is echoed back on apply so the server can
   * record what this torrent looked like, and tell a later re-tag apart from tags left uncreated.
   */
  torrentTagCount: number;
  title: string | null;
  date: string | null;
  /** The one studio that resolved, or null. Never a name the library does not hold. */
  studioName: string | null;
  /** The two studios to choose between, or empty. Non-empty exactly when `studioName` is null. */
  studioChoices: ProposedStudio[];
  /** How many distinct library studios the site tags matched, so silence can explain itself. */
  studioMatchCount: number;
  coverUrl: string | null;
  /** False when the cover's host is not configured, so review can say so before the user waits. */
  coverHostAllowed: boolean;
  url: string | null;
  torrentId: string | null;
  /** Whether the library video has artwork of its own — asked, not discovered by a 404. */
  videoHasImage: boolean;
  currentTitle: string | null;
  currentDate: string | null;
  currentStudioName: string | null;
  currentUrls: string[];
  tags: ProposedRelation[];
  performers: ProposedPerformer[];
  tagNameStyle: string;
}

/**
 * Mirrors `TorrentApplyRequest` in `TorrentApplyService.cs`. A null scalar is not "clear this" — it
 * is absent, and the server leaves the video's own value alone, which is what makes an unticked
 * field safe. `overwrite` only ever reaches the fields actually present here.
 */
export interface ApplyRequest {
  videoId: number;
  coverUrl: string | null;
  tags: string[];
  /** Performer ids. A name is not an identity Cove resolves. */
  performers: number[];
  tagSources: Record<string, string>;
  title: string | null;
  date: string | null;
  studioName: string | null;
  url: string | null;
  torrentId: string | null;
  /** Echoed straight from the proposal; the server records it as the "has it changed" baseline. */
  torrentTagCount: number;
  overwrite: boolean;
}

export interface TorrentApplyResult {
  tagsAdded: number;
  tagsCreated: number;
  /** Links written. There is no `performersCreated`: this path cannot invent a performer. */
  performersAdded: number;
  aliasesSeeded: number;
  titleChanged: boolean;
  dateChanged: boolean;
  studioChanged: boolean;
  urlAdded: boolean;
  coverChanged: boolean;
  /** Why a requested cover was not imported, or null when it was — or when none was asked for. */
  coverSkipped: string | null;
}

async function send<T>(path: string, method: string, body?: unknown): Promise<T> {
  const response = await extensionFetch(`${BASE}${path}`, {
    method,
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  return readApiResponse<T>(response, `Request failed (${response.status}).`);
}

/**
 * What identifies one row of the overview: the library video, and which torrent describes it.
 *
 * Mirrors `BatchRowRef` on the server, which is where the rule lives — see `queue.ts`'s `rowKey` for
 * why the torrent id and the name are not interchangeable, and why neither alone is enough.
 */
export interface BatchRowRef {
  videoId: number;
  torrentId: string | null;
  torrentName: string;
}

export interface BatchRow {
  torrentName: string;
  fileName: string;
  torrentId: string | null;
  fanOut: number;
  /** "updated" is applied, but the torrent has gained tags this video does not carry. */
  status: "applied" | "matched" | "updated";
  /** Always present — an unmatched file is counted in `unmatched`, not sent as a row. */
  videoId: number;
  videoTitle: string | null;
  /** Whether the library video has artwork, so the row does not request one that is not there. */
  videoHasImage: boolean;
  videoTagCount: number;
  /** Tags this torrent would add to this video — not what exists in the library. */
  tagsToAdd: number;
  /** How many of `tagsToAdd` do not exist in the library yet. A subset, not a second bucket. */
  tagsToCreate: number;
  /** Performers this torrent would add to this video — not the number the torrent names. */
  performersToAdd: number;
  torrentCoverUrl: string | null;
  /** False when that cover's host is not configured, in which case the proxy would refuse it. */
  torrentCoverAllowed: boolean;
}

/**
 * Only matched rows come back. A torrent describing a file the library does not have has nothing to
 * review, and a real folder is mostly those — 3218 bookmarked torrents index 139,141 video files of
 * which 715 match, so a row each was a 45 MB response that was 99.5% padding. `unmatched` is what
 * those rows were actually saying.
 */
export interface BatchOverview {
  rows: BatchRow[];
  unmatched: number;
  /**
   * Videos whose size match missed but whose name match would not — a file held under the same name at
   * a different size. Split out of `unmatched` because only this half is something the user can act on:
   * the video's own dialog already offers those torrents, reporting `matched on file name`.
   *
   * **Videos, not video files** — the only count in this shape that is not in torrent-file units.
   */
  videosMatchableByName: number;
  /** Video files across every indexed torrent — `rows` and `unmatched` are both per video file. */
  indexedFiles: number;
  /** Torrents those files came from; packs make the two differ by orders of magnitude. */
  torrents: number;
}

export interface BatchApplyResult {
  videosTouched: number;
  tagsAdded: number;
  tagsCreated: number;
  performersAdded: number;
  aliasesSeeded: number;
  coversImported: number;
  coversSkipped: number;
  /** One sample reason for the skipped covers, not one per video. */
  coverSkipReason: string | null;
  /** Rows whose apply threw and were skipped. A floor — a row can fail after writing some of it. */
  rowsFailed: number;
  /** One sample reason for the failed rows, on the same rule as `coverSkipReason`. */
  failureReason: string | null;
  /** The server's breaker fired. The caller must stop sending chunks, or it trips once per chunk. */
  stoppedEarly: boolean;
}

export interface Settings {
  tagNameStyle: string;
  /** Hosts covers may be fetched from. Ships empty — covers do not import until it is filled in. */
  coverHosts: string[];
  /** Folders torrents are read from, in addition to the extension's own. Read-only; never written to. */
  sourceFolders: string[];
  /** Where uploads land. Not editable — it is the one folder the extension owns. */
  writeFolder: string | null;
}

export const batchApi = {
  list: () => send<BatchOverview>("/batch", "GET"),

  /**
   * Re-reads the watched folder. Needed for torrents copied in by hand rather than uploaded.
   *
   * The shape lives in `reloadStatus.ts` rather than here, so the module that turns it into the status
   * line owns it and this file imports no runtime from that direction — the same way `upload.ts` owns
   * `UploadResult`.
   */
  reload: () => send<ReloadReport>("/reload", "POST"),

  /**
   * Asks whether the folders have moved since the index was last built. Stats them and opens nothing,
   * so it is cheap enough to call whenever the page comes back into view — unlike `/batch`, which
   * lists the whole library against the whole index.
   *
   * The shape lives in `folderState.ts`, with the module that turns it into a sentence.
   */
  folderState: () => send<FolderStateReport>("/folder-state", "GET"),

  apply: (request: {
    /** The rows to apply, by what identifies one. Empty means every eligible row. */
    rows: BatchRowRef[];
    createNewTags: boolean;
    includePacks: boolean;
    importCovers: boolean;
  }) =>
    send<BatchApplyResult>("/batch/apply", "POST", request),

  /**
   * Uploads torrents, in as many requests as it takes.
   *
   * Sent in chunks rather than all at once: a folder of torrents is the normal drop on the batch page,
   * and one request for all of them exceeded the host's default body limit and came back as a bare 413
   *. `onProgress` reports files sent so far out of the total, so a long upload can say something
   * — the same reason the bulk apply on that page is chunked.
   */
  async upload(files: File[], onProgress?: (sent: number, total: number) => void) {
    const chunks = chunkForUpload(files);
    const results: UploadResult[] = [];
    let sent = 0;

    for (const chunk of chunks) {
      results.push(await uploadOne(chunk));
      sent += chunk.length;
      onProgress?.(sent, files.length);
    }

    return mergeUploadResults(results);
  },
};

async function uploadOne(files: File[]): Promise<UploadResult> {
  const form = new FormData();
  for (const file of files) form.append("files", file, file.name);

  // Sent as multipart, so this bypasses `send` and its JSON body handling.
  const response = await extensionFetch(`${BASE}/upload`, { method: "POST", body: form });
  return readApiResponse<UploadResult>(response, `Upload failed (${response.status}).`);
}

export const matchApi = {
  /**
   * Builds a proposal for a video. Passing a torrent pins the result to it instead of searching the
   * whole folder by size — used when the user hands us a specific .torrent. Naming a file inside it is
   * optional and only worth doing when the caller knows which one; otherwise the server picks the file
   * whose size this video has.
   */
  match: (videoId: number, torrentName?: string, fileName?: string) =>
    send<TorrentMatchProposal>("/match", "POST", {
      entityId: videoId,
      torrentName: torrentName ?? null,
      fileName: fileName ?? null,
    }),

  apply: (request: ApplyRequest) => send<TorrentApplyResult>("/apply", "POST", request),

  getSettings: () => send<Settings>("/settings", "GET"),

  // Each setter sends only its own field. The endpoint leaves anything absent alone, so the two
  // controls cannot reset one another.
  setTagNameStyle: (tagNameStyle: string) => send<Settings>("/settings", "PUT", { tagNameStyle }),

  setCoverHosts: (coverHosts: string[]) => send<Settings>("/settings", "PUT", { coverHosts }),

  /** Does not rebuild the index — the folders take effect on the next rescan, by design. */
  setSourceFolders: (sourceFolders: string[]) => send<Settings>("/settings", "PUT", { sourceFolders }),
};

/** What the folder listing returns. The row shape itself lives in `writeFolder.ts`, which owns it. */
export interface WriteFolderListing {
  folder: string | null;
  torrents: FolderTorrent[];
}

/** What a remove did, plus the index totals the reload behind it produced. */
export interface WriteFolderRemoveResult {
  removed: number;
  refused: string[];
  torrents: number;
  files: number;
}

/**
 * The one folder this extension writes, listed and emptied.
 *
 * Separate from `batchApi` because it is the settings panel's, and separate from `matchApi` because it
 * is not a setting — nothing here is stored, it is the folder itself.
 */
export const writeFolderApi = {
  list: () => send<WriteFolderListing>("/write-folder", "GET"),

  /**
   * Removes the named files. Every name is relative to the folder and re-checked server-side, so a
   * stale list produces refusals rather than a deletion somewhere else.
   *
   * Takes the full filtered list rather than the page of it on screen, which is what the button's own
   * label promises — see `wipeLabel` in `writeFolder.ts`.
   */
  remove: (files: string[]) => send<WriteFolderRemoveResult>("/write-folder/remove", "POST", { files }),
};
