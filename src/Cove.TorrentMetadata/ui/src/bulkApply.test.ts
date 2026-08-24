import { describe, expect, it } from "vitest";
import { describeBulkApply, emptyTotals, foldApplyResult, shouldContinue, type BulkApplyTotals } from "./bulkApply";
import type { BatchApplyResult } from "./api";

const chunk = (over: Partial<BatchApplyResult> = {}): BatchApplyResult => ({
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
  ...over,
});

const totals = (over: Partial<BulkApplyTotals> = {}): BulkApplyTotals => ({ ...emptyTotals(), ...over });

describe("foldApplyResult", () => {
  it("sums the counters across chunks", () => {
    const run = [chunk({ videosTouched: 10, tagsAdded: 100 }), chunk({ videosTouched: 3, tagsAdded: 25 })]
      .reduce(foldApplyResult, emptyTotals());

    expect(run.videosTouched).toBe(13);
    expect(run.tagsAdded).toBe(125);
  });

  it("keeps the first reason rather than the last", () => {
    // The one that happened first is the one that explains the rest — the last is simply whichever
    // row the chunking happened to end on.
    const run = [
      chunk({ rowsFailed: 1, failureReason: "tag namespace conflict" }),
      chunk({ rowsFailed: 1, failureReason: "something later" }),
    ].reduce(foldApplyResult, emptyTotals());

    expect(run.rowsFailed).toBe(2);
    expect(run.failureReason).toBe("tag namespace conflict");
  });

  it("keeps the first cover reason on the same rule", () => {
    const run = [
      chunk({ coversSkipped: 3, coverSkipReason: "no hosts are allowed yet" }),
      chunk({ coversSkipped: 2, coverSkipReason: "something later" }),
    ].reduce(foldApplyResult, emptyTotals());

    expect(run.coversSkipped).toBe(5);
    expect(run.coverSkipReason).toBe("no hosts are allowed yet");
  });

  it("latches stoppedEarly, so a later clean chunk cannot clear it", () => {
    const run = [chunk({ stoppedEarly: true }), chunk()].reduce(foldApplyResult, emptyTotals());
    expect(run.stoppedEarly).toBe(true);
  });
});

describe("shouldContinue", () => {
  it("keeps going on an ordinary chunk", () => {
    expect(shouldContinue(totals({ videosTouched: 10 }))).toBe(true);
  });

  it("stops once the server's breaker has fired", () => {
    // The breaker resets per request, so a client that ignored this would turn "stop after five" into
    // "five failures per chunk", for the whole selection.
    expect(shouldContinue(totals({ stoppedEarly: true }))).toBe(false);
  });
});

describe("describeBulkApply", () => {
  it("reports a clean run", () => {
    expect(describeBulkApply(totals({ videosTouched: 47, tagsAdded: 512, tagsCreated: 30 })))
      .toBe("Applied to 47 videos: 512 tags (30 created).");
  });

  it("omits the counters that are zero", () => {
    expect(describeBulkApply(totals({ videosTouched: 1, tagsAdded: 2 })))
      .toBe("Applied to 1 video: 2 tags.");
  });

  it("names skipped covers, because a silent zero looks like covers were never asked for", () => {
    const line = describeBulkApply(
      totals({ videosTouched: 5, tagsAdded: 10, coversSkipped: 5, coverSkipReason: "no hosts are allowed yet" }),
    );
    expect(line).toContain("5 covers skipped — no hosts are allowed yet");
  });

  it("still reports what was written when rows failed", () => {
    // The writes already happened. Suppressing them to lead with the error tells the user their
    // library is untouched when it is not.
    const line = describeBulkApply(
      totals({ videosTouched: 47, tagsAdded: 512, rowsFailed: 3, failureReason: "tag namespace conflict" }),
    );
    expect(line).toContain("Applied to 47 videos: 512 tags.");
    expect(line).toContain("3 rows failed — tag namespace conflict");
  });

  it("says the run stopped itself rather than merely counting the failures", () => {
    const line = describeBulkApply(
      totals({ videosTouched: 2, tagsAdded: 4, rowsFailed: 5, failureReason: "database is locked", stoppedEarly: true }),
    );
    expect(line).toContain("Stopped after 5 rows failed — database is locked");
  });

  it("reports a chunk that threw separately from the rows that failed", () => {
    // Different events: a row failure is something the server reported, a halt means the run stopped
    // being observed at all.
    const line = describeBulkApply(totals({ videosTouched: 20, tagsAdded: 200 }), "Failed to fetch");
    expect(line).toContain("Applied to 20 videos: 200 tags.");
    expect(line).toContain("The run stopped: Failed to fetch");
  });

  it("does not open with a zero count when nothing was written", () => {
    // "Applied to 0 videos" reads as "there was nothing to do", which is the opposite of what happened.
    const line = describeBulkApply(totals({ rowsFailed: 5, failureReason: "database is locked", stoppedEarly: true }));
    expect(line).not.toContain("Applied to 0 videos");
    expect(line).toBe("Stopped after 5 rows failed — database is locked");
  });

  it("does not open with a zero count when the first chunk threw", () => {
    const line = describeBulkApply(emptyTotals(), "Failed to fetch");
    expect(line).toBe("The run stopped: Failed to fetch");
  });
});
