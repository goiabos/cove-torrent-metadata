import { describe, expect, it } from "vitest";
import { createIngestGuard } from "./ingestGuard";

describe("createIngestGuard", () => {
  it("accepts the first drop", () => {
    const guard = createIngestGuard();
    expect(guard.start()).toBe(true);
  });

  it("refuses a second drop while the first is still in flight", () => {
    // The bug this guards against: two drops on the same zone both ran `ingest` to completion, and
    // both called `onProposal`, stacking a second MatchDialog over the first.
    const guard = createIngestGuard();
    expect(guard.start()).toBe(true);
    expect(guard.start()).toBe(false);
  });

  it("refuses every drop that arrives before finish, not just the second", () => {
    const guard = createIngestGuard();
    expect(guard.start()).toBe(true);
    expect(guard.start()).toBe(false);
    expect(guard.start()).toBe(false);
    expect(guard.start()).toBe(false);
  });

  it("accepts the next drop once the in-flight one has finished", () => {
    const guard = createIngestGuard();
    expect(guard.start()).toBe(true);
    guard.finish();
    expect(guard.start()).toBe(true);
  });

  it("tolerates a finish with no matching start", () => {
    // `ingest`'s error branch and its two success branches all call `finish` on paths that do not
    // overlap, but a future edit could still call it twice for one `start`. Finishing an idle guard
    // must not flip it into some third state that then refuses the next legitimate drop.
    const guard = createIngestGuard();
    guard.finish();
    expect(guard.start()).toBe(true);
  });

  it("keeps two guards independent", () => {
    // Each `TorrentDropZone` instance holds its own guard (one per ref), so a busy one must never
    // block a drop onto a different video's dialog.
    const first = createIngestGuard();
    const second = createIngestGuard();
    expect(first.start()).toBe(true);
    expect(second.start()).toBe(true);
  });
});
