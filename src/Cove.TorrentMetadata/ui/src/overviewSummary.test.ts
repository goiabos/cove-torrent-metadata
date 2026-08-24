import { describe, expect, it } from "vitest";
import { overviewSummary, type OverviewCounts } from "./overviewSummary";

const counts = (over: Partial<OverviewCounts> = {}): OverviewCounts => ({
  torrents: 3218,
  indexed: 139141,
  matched: 715,
  applied: 0,
  updated: 0,
  noMatch: 138426,
  matchableByName: 0,
  ...over,
});

describe("overviewSummary", () => {
  it("says it is loading before the first overview arrives", () => {
    // Null is "not fetched yet", which is not the same as an empty folder — that reads as a real
    // answer with zeroes in it.
    expect(overviewSummary(null)).toBe("Loading…");
  });

  it("reports the folder in torrent-file units", () => {
    expect(overviewSummary(counts())).toBe(
      "3218 torrents · 139141 video files · 715 to apply · 0 applied · 138426 not in your library",
    );
  });

  it("names its unit when it stops counting torrent files", () => {
    // The one figure on this line whose subject is a video. Appending a bare "· 12" to a row of
    // per-file counts would read as a thirteenth kind of file, which is the confusion the wording is
    // paying for.
    expect(overviewSummary(counts({ matchableByName: 12 }))).toBe(
      "3218 torrents · 139141 video files · 715 to apply · 0 applied · 138426 not in your library · " +
        "12 of your videos match one by name",
    );
  });

  it("omits the name-only count when there is nothing to say", () => {
    expect(overviewSummary(counts({ matchableByName: 0 }))).not.toContain("by name");
  });

  it("omits the update count when nothing has new tags", () => {
    expect(overviewSummary(counts({ updated: 0 }))).not.toContain("with new tags");
  });

  it("keeps the name-only count last, after the number it is carved out of", () => {
    // It qualifies "not in your library" — it is the actionable half of that number — so it has to
    // read after it rather than somewhere in the middle of the totals.
    const summary = overviewSummary(counts({ updated: 4, matchableByName: 12 }));

    expect(summary.indexOf("with new tags")).toBeLessThan(summary.indexOf("not in your library"));
    expect(summary.indexOf("not in your library")).toBeLessThan(summary.indexOf("by name"));
  });
});
