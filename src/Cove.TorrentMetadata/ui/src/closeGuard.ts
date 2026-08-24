/**
 * The rule behind every way to leave the review dialog — the Close button inside `ReviewBody`, and
 * Escape and a click on the backdrop in whichever shell wraps it (`MatchDialog`, `ReviewPane`). All
 * three ask the same question — "can this close happen now?" — and must get the same answer, or the
 * three doors drift apart the way two earlier defects already show they will.
 *
 * Idle, a close is granted at once. While an apply is in flight, it is deferred: the caller tears its
 * own state down inside `onClose` (`TorrentBatchPage`'s `closeReview` nulls the queue, `main.tsx`'s
 * reload flag is only set inside `onApplied`), so a close that beats `apply()`'s bookkeeping to the
 * punch drops the applied record and leaves the page behind the dialog describing the video as it
 * was — that failure, through however many doors have not yet learned to check this first.
 *
 * `ReviewBody` is the only thing that knows whether an apply is in flight, so it is the only thing
 * that can answer this; a shell holds no state of its own and asks rather than decides (`docs` calls
 * this split honest only while no shell holds `useState` — asking a question is not holding state).
 */
export type CloseRequestOutcome = "close" | "defer";

/** What a close request resolves to, given whether an apply is in flight when it arrives. */
export function resolveCloseRequest(applyInFlight: boolean): CloseRequestOutcome {
  return applyInFlight ? "defer" : "close";
}

/** What the Close button reads while a deferred close is waiting on the apply it deferred behind. */
export const CLOSE_PENDING_LABEL = "Closing…";
