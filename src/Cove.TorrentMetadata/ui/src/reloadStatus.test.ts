import { describe, expect, it } from "vitest";
import { reloadStatus, skippedSummary, type ReloadReport } from "./reloadStatus";

const NOTHING_SKIPPED: ReloadReport["skipped"] = {
  unreadable: 0,
  malformed: 0,
  withoutVideo: 0,
  duplicates: 0,
  total: 0,
};

const report = (over: Partial<ReloadReport> = {}): ReloadReport => ({
  torrents: 3,
  files: 12,
  folder: "/data/torrent-metadata",
  folders: [{ path: "/data/torrent-metadata", exists: true, torrents: 3, writable: true }],
  truncated: false,
  unreadableDirectories: 0,
  skipped: NOTHING_SKIPPED,
  ...over,
});

const skipped = (over: Partial<ReloadReport["skipped"]>): ReloadReport["skipped"] => {
  const counts = { ...NOTHING_SKIPPED, ...over };
  // The server's total is its own number; unless a case is deliberately testing disagreement, it is
  // the sum of the reasons.
  return { ...counts, total: over.total ?? counts.unreadable + counts.malformed + counts.withoutVideo + counts.duplicates };
};

describe("skippedSummary", () => {
  it("says nothing at all when the walk indexed everything it saw", () => {
    // A clean rescan reads as one clean sentence. Four zeroes would invite the user to worry about
    // four things that did not happen, and would make a real number harder to notice.
    expect(skippedSummary(NOTHING_SKIPPED)).toBe("");
  });

  it("names only the reasons that happened", () => {
    expect(skippedSummary(skipped({ malformed: 2 }))).toBe("Skipped 2 not readable as a torrent.");
  });

  it("reads the actionable reasons before the routine ones", () => {
    // A folder full of duplicates is normal; a file that will not open is not. The order is the
    // difference between a line that leads with the problem and one that buries it.
    expect(skippedSummary(skipped({ unreadable: 1, malformed: 2, withoutVideo: 3, duplicates: 4 }))).toBe(
      "Skipped 1 unreadable, 2 not readable as a torrent, 3 with no video, 4 already indexed.",
    );
  });

  it("accounts for a total larger than the reasons it knows about", () => {
    // A reason added server-side and not here. Under-reporting would be the silent failure this whole
    // change exists to remove, so the shortfall is stated rather than dropped.
    expect(skippedSummary({ ...NOTHING_SKIPPED, malformed: 1, total: 4 })).toBe(
      "Skipped 1 not readable as a torrent, 3 for other reasons.",
    );
  });

  it("still reports a total it can attribute to nothing", () => {
    expect(skippedSummary({ ...NOTHING_SKIPPED, total: 2 })).toBe("Skipped 2.");
  });
});

describe("reloadStatus", () => {
  it("reports what was read when there is nothing else to say", () => {
    expect(reloadStatus(report())).toBe("Read 1 folder(s): 3 torrents, 12 video files.");
  });

  it("counts only the folders it could read", () => {
    // A source on an unmounted drive. The count has to describe what was actually walked, or the
    // sentence claims a folder contributed nothing when it was never opened.
    const status = reloadStatus(
      report({
        folders: [
          { path: "/data/torrent-metadata", exists: true, torrents: 3, writable: true },
          { path: "/mnt/archive", exists: false, torrents: 0, writable: false },
        ],
      }),
    );

    expect(status).toBe("Read 1 folder(s): 3 torrents, 12 video files. Not found: /mnt/archive.");
  });

  it("carries the missing folders, the skips and the cap together", () => {
    // All three at once, because each is the only place its state surfaces and a line that drops one
    // when another is present is the failure they were separated to avoid.
    const status = reloadStatus(
      report({
        truncated: true,
        folders: [
          { path: "/data/torrent-metadata", exists: true, torrents: 3, writable: true },
          { path: "/mnt/archive", exists: false, torrents: 0, writable: false },
        ],
        skipped: skipped({ unreadable: 1, duplicates: 5 }),
      }),
    );

    expect(status).toBe(
      "Read 1 folder(s): 3 torrents, 12 video files. Not found: /mnt/archive. " +
        "Skipped 1 unreadable, 5 already indexed. Stopped at the index cap — narrow a folder and rescan.",
    );
  });

  it("says nothing about unreadable directories when there were none", () => {
    // Same rule as the skip counts: a zero here would invite the user to check permissions on a
    // folder that is perfectly readable.
    expect(reloadStatus(report({ unreadableDirectories: 0 }))).toBe(
      "Read 1 folder(s): 3 torrents, 12 video files.",
    );
  });

  it("names a directory it could not open, and says what to do about it", () => {
    // The whole of the locked-directory fix from the user's side. Before it, a locked subdirectory aborted the reload and
    // the operator was told nothing at all — not which folder, not that anything had gone wrong.
    expect(reloadStatus(report({ unreadableDirectories: 1 }))).toBe(
      "Read 1 folder(s): 3 torrents, 12 video files. 1 folder(s) could not be read — check permissions.",
    );
  });

  it("keeps unreadable directories apart from the files it skipped", () => {
    // Two counts in two units, said as two sentences. Folding a directory into the file skips would
    // make one number that means neither, and the directory hides an unknown number of torrents
    // behind it — so it is the count that cannot be added to anything.
    const status = reloadStatus(report({ unreadableDirectories: 2, skipped: skipped({ unreadable: 1 }) }));

    expect(status).toBe(
      "Read 1 folder(s): 3 torrents, 12 video files. 2 folder(s) could not be read — check permissions. " +
        "Skipped 1 unreadable.",
    );
  });
});
