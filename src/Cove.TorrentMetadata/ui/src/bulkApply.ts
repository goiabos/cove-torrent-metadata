/**
 * What a bulk apply run reports: the chunk answers folded together, and the sentence that describes
 * them.
 *
 * A module for the reason `upload.ts` is one — that path also splits a large request into chunks and
 * has to put the answers back together, and the merge is the part worth pinning. The run is not one
 * request: the page sends the selection in slices, so every figure the user reads is a sum the browser
 * computed, and a sum that is dropped on the way is indistinguishable from work that never happened.
 *
 * That is exactly what the original defect was: a chunk that threw discarded every total accumulated before it and
 * reported the raw exception instead, over a run whose earlier chunks had already been committed.
 *
 * Imports no React and no `@cove/runtime/*`, so the wording and the folding are testable without a DOM.
 */

import type { BatchApplyResult } from "./api";

export interface BulkApplyTotals {
  videosTouched: number;
  tagsAdded: number;
  tagsCreated: number;
  performersAdded: number;
  aliasesSeeded: number;
  coversImported: number;
  coversSkipped: number;
  /** One sample across the whole run, not one per chunk — every chunk skips for the same reason. */
  coverSkipReason: string | null;
  /** Rows whose apply threw. A floor: a row can fail after writing some of its tags. */
  rowsFailed: number;
  /** The first failure across the run, on the same one-sample rule as `coverSkipReason`. */
  failureReason: string | null;
  /** The server's breaker fired — it stopped rather than working through the rest of that chunk. */
  stoppedEarly: boolean;
}

export const emptyTotals = (): BulkApplyTotals => ({
  videosTouched: 0,
  tagsAdded: 0,
  tagsCreated: 0,
  performersAdded: 0,
  aliasesSeeded: 0,
  coversImported: 0,
  coversSkipped: 0,
  coverSkipReason: null,
  rowsFailed: 0,
  failureReason: null,
  stoppedEarly: false,
});

/**
 * Folds one chunk's answer into the running totals.
 *
 * The two reason fields keep the **first** non-null rather than the last, which is the rule the server
 * already applies within a chunk. A run that fails the same way on every row should say that reason
 * once, and the one that happened first is the one that explains the rest.
 */
export function foldApplyResult(totals: BulkApplyTotals, result: BatchApplyResult): BulkApplyTotals {
  return {
    videosTouched: totals.videosTouched + result.videosTouched,
    tagsAdded: totals.tagsAdded + result.tagsAdded,
    tagsCreated: totals.tagsCreated + result.tagsCreated,
    performersAdded: totals.performersAdded + result.performersAdded,
    aliasesSeeded: totals.aliasesSeeded + result.aliasesSeeded,
    coversImported: totals.coversImported + result.coversImported,
    coversSkipped: totals.coversSkipped + result.coversSkipped,
    coverSkipReason: totals.coverSkipReason ?? result.coverSkipReason,
    rowsFailed: totals.rowsFailed + result.rowsFailed,
    failureReason: totals.failureReason ?? result.failureReason,
    stoppedEarly: totals.stoppedEarly || result.stoppedEarly,
  };
}

const plural = (count: number, word: string) => `${count} ${word}${count === 1 ? "" : "s"}`;

/**
 * The status line after a run.
 *
 * `halted` is the client's own half: the message from a chunk request that threw, which is a different
 * event from a row failing. A row failure is data the server reported; a halt means the run stopped
 * being observed, so the totals are what completed rather than what was attempted.
 *
 * Three rules, each of which was a way to mislead before that was fixed:
 *
 * - **The totals are always stated**, even when the run ended badly. They describe writes that have
 *   already happened, and suppressing them tells the user their library is unchanged when it is not.
 * - **A run that wrote nothing does not open with "Applied to 0 videos"**, which reads as a no-op
 *   rather than as a failure.
 * - **A failure is never reported as a bare count.** The sample reason is what makes it actionable,
 *   and it is the same reason for every row when the cause is systemic.
 */
export function describeBulkApply(totals: BulkApplyTotals, halted?: string | null): string {
  const wrote =
    `Applied to ${plural(totals.videosTouched, "video")}: ${plural(totals.tagsAdded, "tag")}` +
    (totals.tagsCreated ? ` (${totals.tagsCreated} created)` : "") +
    (totals.performersAdded ? `, ${plural(totals.performersAdded, "performer")}` : "") +
    (totals.aliasesSeeded ? `, ${plural(totals.aliasesSeeded, "alias")}` : "") +
    (totals.coversImported ? `, ${plural(totals.coversImported, "cover")}` : "") +
    ".";

  const parts: string[] = [];

  // Named rather than left as a silent zero. A run that imports no covers because nothing is
  // configured looks identical to one where covers were never requested.
  if (totals.coversSkipped)
    parts.push(`${plural(totals.coversSkipped, "cover")} skipped — ${totals.coverSkipReason ?? ""}`.trim());

  if (totals.rowsFailed)
    parts.push(
      totals.stoppedEarly
        ? `Stopped after ${plural(totals.rowsFailed, "row")} failed — ${totals.failureReason ?? ""}`.trim()
        : `${plural(totals.rowsFailed, "row")} failed — ${totals.failureReason ?? ""}`.trim(),
    );

  if (halted) parts.push(`The run stopped: ${halted}`);

  // Nothing was written and something went wrong: leading with a zero count reads as "there was
  // nothing to do", which is the opposite of what happened.
  const opening = totals.videosTouched === 0 && (totals.rowsFailed || halted) ? null : wrote;

  return [opening, ...parts].filter(Boolean).join(" ");
}

/**
 * Whether to send the next chunk.
 *
 * The breaker lives on the server but has to be honoured here, because the run is many requests: a
 * server that stops after five consecutive failures and a client that keeps sending slices produces
 * five failures per chunk for the whole selection, which is most of what the breaker exists to
 * prevent.
 */
export const shouldContinue = (totals: BulkApplyTotals): boolean => !totals.stoppedEarly;
