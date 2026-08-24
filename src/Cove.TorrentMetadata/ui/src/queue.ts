/**
 * The review queue: which matched rows a walk covers, and where in it the reviewer is.
 *
 * A row on the batch page is one library video described by one torrent, and reviewing them one at a
 * time used to mean returning to the page between each one. This module is the whole decision set behind walking
 * them in place: what the queue contains, how it advances, where it ends, and what it remembers about
 * rows already applied.
 *
 * It is deliberately free of React and of `@cove/runtime/*` — the host owns those singletons, so a
 * module without them needs no test-only stand-in for the host runtime. The shell that renders the
 * pager is a separate decision: the same functions serve the footer pager, an apply-and-advance
 * footer, and the split-pane page that is its declared end state.
 *
 * **The queue is a snapshot.** It is taken when the walk starts and does not change under the
 * reviewer. Re-deriving it from a refreshed overview would refetch the whole list per apply and, with
 * "Hide applied" on, delete the row being reviewed out from under the index.
 *
 * **It records that a row was applied, not what it became.** Whether a row then reads `applied` or
 * `updated` is decided by `TorrentBatchService` from the torrent's own tag count; deriving
 * that here would be a second copy of a server rule, and the copy that drifts is never the one that
 * matters. One refresh on close asks the server instead.
 */

import type { BatchRow, BatchRowRef } from "./api";

export interface ReviewQueue {
  /** The rows this walk covers, frozen at the moment it started. */
  readonly rows: readonly BatchRow[];
  /** Where the reviewer is. Always a valid index into `rows`. */
  readonly index: number;
  /** Keys of rows applied during this walk — the reason to refresh on close, and nothing more. */
  readonly applied: ReadonlySet<string>;
}

/**
 * A row's identity: the library video, and which torrent describes it.
 *
 * Not a video id alone — 2.32% of file sizes are shared, so one video can appear in more than one row
 * and naming the video names them all.
 *
 * And no longer `torrentName/fileName`, which identified a row in neither direction. The server strips
 * the directory from a torrent's file path, so a pack holding `Disc1/01.mp4` beside `Disc2/01.mp4`
 * produced two rows agreeing on both halves while pointing at different videos — the tick collapsed
 * them and `findIndex` always opened the first.
 *
 * **This mirrors `TorrentBatchService.RowKey` and must keep mirroring it.** The server decides which
 * rows exist and what an apply addresses; a client keying them differently either hides a row or ticks
 * the wrong one. The id wins wherever it exists so that two copies of one re-tagged release are one row
 * rather than two identical ones, and the prefixes keep the two spaces apart, so a torrent
 * *named* `12345` and one with *id* `12345` are not the same row. The join is injective because a video
 * id is digits: the first space is always the separator, whatever the torrent's name contains.
 *
 * The separator is written as `\u0000` rather than as a literal NUL byte. It used to be the byte
 * itself, which made every text tool treat this file as binary — `grep` reports "binary file matches"
 * and prints nothing, so a scan over the sources silently skipped it. The escape compiles to the same
 * character.
 */
export const rowKey = (row: BatchRow): string =>
  `${row.videoId}\u0000${row.torrentId ? `i:${row.torrentId}` : `n:${row.torrentName}`}`;

/** What an apply names a row by — the same identity, in the shape the endpoint takes. */
export const rowRef = (row: BatchRow): BatchRowRef => ({
  videoId: row.videoId,
  torrentId: row.torrentId,
  torrentName: row.torrentName,
});

/**
 * Where a row sits in a list, carrying a walk's record across.
 *
 * The one answer to "where in this list is that row", shared by every way a walk can be re-pointed:
 * opening one, clicking a row beside the review, and re-anchoring after the page's filters changed
 * what is on screen. It used to be one of those and is now three, which is exactly how a rule ends up
 * with three subtly different copies.
 *
 * **A row that is not in the list becomes a queue of one** rather than an index that cannot be
 * trusted. That is not a degenerate case: with the review beside the list rather than over it, a
 * filter can hide the row under review while the reviewer is still reading it, and a walk of one is
 * the honest thing to say about that — the pager reads `1 of 1` instead of pointing at a row nobody
 * can see, and the review itself is never closed or swapped out from under them.
 */
function anchor(rows: readonly BatchRow[], row: BatchRow, applied: ReadonlySet<string>): ReviewQueue {
  const key = rowKey(row);
  const index = rows.findIndex((candidate) => rowKey(candidate) === key);
  return index === -1 ? { rows: [row], index: 0, applied } : { rows, index, applied };
}

/**
 * Starts a walk at `row`, over the rows currently on screen.
 *
 * Callers pass the *visible* rows, filters included — not the bulk-apply eligible set, which drops
 * packs, and packs are precisely the rows that need reviewing one at a time.
 */
export function openQueue(rows: readonly BatchRow[], row: BatchRow): ReviewQueue {
  return anchor(rows, row, new Set());
}

/**
 * Moves the walk to a row the reviewer clicked in the list beside the review.
 *
 * The applied set is carried across, which is the whole reason this is not `openQueue`: that set is
 * what the refresh on close and the run summary are built from, and starting a fresh walk on every
 * click would quietly throw away the record of everything applied so far.
 */
export function jumpToRow(queue: ReviewQueue, row: BatchRow): ReviewQueue {
  return anchor(queue.rows, row, queue.applied);
}

/**
 * Re-anchors the walk on a new visible list, keeping the row under review.
 *
 * The queue is a snapshot, and with the list beside the review its filters are reachable during a
 * walk — so the rows on screen can change under a walk that was frozen when it started. The position
 * follows the row being reviewed rather than the index, because an index into a list that has been
 * refiltered means nothing.
 *
 * The applied set survives: it records acts, and an act is not undone by a filter.
 */
export function resyncQueue(queue: ReviewQueue, rows: readonly BatchRow[]): ReviewQueue {
  const current = currentRow(queue);
  return current === null ? queue : anchor(rows, current, queue.applied);
}

/** The row being reviewed, or null for a queue with nothing in it. */
export function currentRow(queue: ReviewQueue): BatchRow | null {
  return queue.rows[queue.index] ?? null;
}

/**
 * Whether the queue can move by `delta`.
 *
 * The ends stop rather than wrap. A wrapping queue cannot answer "have I been through all of them",
 * which is the only question a long walk actually has.
 */
export function canStep(queue: ReviewQueue, delta: number): boolean {
  const next = queue.index + delta;
  return next >= 0 && next < queue.rows.length && next !== queue.index;
}

/** Moves by `delta`, clamped to the ends. Returns the same queue when it cannot move. */
export function stepQueue(queue: ReviewQueue, delta: number): ReviewQueue {
  return canStep(queue, delta) ? { ...queue, index: queue.index + delta } : queue;
}

/** Records that the current row was applied. Idempotent — applying twice is still one row. */
export function markApplied(queue: ReviewQueue): ReviewQueue {
  const row = currentRow(queue);
  if (row === null) return queue;

  const key = rowKey(row);
  if (queue.applied.has(key)) return queue;
  return { ...queue, applied: new Set([...queue.applied, key]) };
}

/**
 * Whether this walk applied a row.
 *
 * The list's own mark, and the only claim the browser makes about a row it has applied. What the row
 * *becomes* — `applied` or `updated` — is derived by `TorrentBatchService` from the torrent's own tag
 * count, so the mark says what happened rather than what the row now is, and the status
 * beside it does not move until the refresh on close asks the server.
 */
export function wasApplied(queue: ReviewQueue, row: BatchRow): boolean {
  return queue.applied.has(rowKey(row));
}

/** A key press, reduced to what deciding about it needs. The component reads the event; this decides. */
export interface WalkKey {
  key: string;
  /** True if any of Ctrl, Meta or Alt was held. Those shortcuts belong to the browser or the host. */
  withModifier: boolean;
  /** True when focus is in something text goes into — a filter box, a field, a contenteditable. */
  typing: boolean;
}

/**
 * Which way a key press moves the walk, or null for one that does not.
 *
 * Keyboard walking is a **modifier on a visible affordance, never the only route** — the arrows in the
 * footer are the control, and this is the same thing without the mouse. It went in only once that
 * affordance existed, because an undiscoverable-only route is not a control and this extension
 * is going public.
 *
 * Two rules do the real work here. **Nothing fires while the reviewer is typing** — both filters are
 * text boxes a step away from the list they filter, and a `j` that jumped to the next row mid-word
 * would be the whole feature's reputation. And **a modifier means it is not ours**: Ctrl-arrow and
 * Meta-arrow belong to the browser and to whatever Cove binds.
 *
 * There is deliberately **no key for apply.** Stepping is reversible and apply writes to the library;
 * a bare letter that tags a video is the wrong kind of fast, and the button is one click away.
 */
/**
 * The keys a walk answers to, as data, so the hint below and the function beneath it cannot disagree.
 *
 * They already had. The pager's hint was written into the component as `← → or J K`, which reads as
 * ←=J and →=K — while `keyStep` maps `j` forward and `k` back, the other way round. Nothing could
 * catch it, because prose in a component is reachable by no test.
 */
export const STEP_KEYS = {
  forward: { label: ["→", "J"], keys: ["ArrowRight", "j", "J"] },
  back: { label: ["←", "K"], keys: ["ArrowLeft", "k", "K"] },
} as const;

/** What the pager tells the reviewer. Built from the table above rather than restated. */
export const KEY_STEP_HINT =
  `${STEP_KEYS.forward.label.join(" or ")} next, ${STEP_KEYS.back.label.join(" or ")} previous`;

export function keyStep(input: WalkKey): number | null {
  if (input.typing || input.withModifier) return null;

  if ((STEP_KEYS.forward.keys as readonly string[]).includes(input.key)) return 1;
  if ((STEP_KEYS.back.keys as readonly string[]).includes(input.key)) return -1;
  return null;
}

/** The position, for the pager. One-based, because it is read by a person. */
export function describeQueuePosition(queue: ReviewQueue): string {
  return `${queue.index + 1} of ${queue.rows.length}`;
}

/**
 * What the page says after a walk that changed something, or null when it changed nothing.
 *
 * Counted in rows applied, not in tags: the per-row detail was already reported in the dialog as it
 * happened, and the page's own list is about to be refreshed with the server's own numbers.
 */
export function describeQueueRun(queue: ReviewQueue): string | null {
  const applied = queue.applied.size;
  if (applied === 0) return null;
  return `Applied ${applied} of the ${queue.rows.length} rows in this walk.`;
}
