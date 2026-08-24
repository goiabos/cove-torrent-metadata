/**
 * Whether `TorrentDropZone` accepts a new drop right now.
 *
 * `ingest` is asynchronous — a chunked upload followed by a match lookup — and nothing stopped a
 * second drop landing on the same zone while the first was still in flight. Both ran to completion
 * and both called `onProposal`, and `main.tsx` mounts a fresh detached root per call — untestable by
 * design, since it imports `@cove/runtime/react` and `@cove/runtime/react-dom-client`, which resolve
 * only through the host's import map — so the second call stacked a second dialog over the first
 * rather than replacing it.
 *
 * **A held flag, not a queue.** Once the first ingest's proposal arrives, `onProposal` unmounts the
 * drop zone (`main.tsx`'s `openForVideo` unmounts before opening `MatchDialog`), so there is nothing
 * left on screen for a queued second drop to apply to — deferring it would mean running it against a
 * component that is already gone, or inventing a second surface to receive it. A drop that lands
 * mid-ingest is refused, not deferred; the "Reading…" notice already on screen is why nothing further
 * needs to say so.
 *
 * **`busy` state cannot be this guard.** `ingest` is memoized with `useCallback` on
 * `[onProposal, videoId]`, so its closure captures `busy` from the render it was last recreated on —
 * not the current one — because those deps rarely change and React reuses the stale function across
 * renders where they don't. Reading state inside a long-lived closure like that is unreliable by
 * construction. A flag reached through a stable reference (a ref holding one of these, in the
 * component) does not have that problem: `start`/`finish` read and write through the same object on
 * every call, however stale the closure around them is.
 */
export interface IngestGuard {
  /** Marks the guard busy and returns true, or returns false when an ingest is already in flight. */
  start(): boolean;
  /** Frees the guard so the next drop is accepted. */
  finish(): void;
}

export function createIngestGuard(): IngestGuard {
  let active = false;
  return {
    start(): boolean {
      if (active) return false;
      active = true;
      return true;
    },
    finish(): void {
      active = false;
    },
  };
}
