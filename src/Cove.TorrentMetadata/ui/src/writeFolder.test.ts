import { describe, expect, it } from "vitest";
import {
  afterRemoval,
  canToggleCap,
  filterTorrents,
  listingLabel,
  listingFailureMessage,
  folderCount,
  folderSectionState,
  folderTorrentState,
  planRemoval,
  wipeLabel,
  type FolderTorrent,
} from "./writeFolder";

const torrent = (over: Partial<FolderTorrent> = {}): FolderTorrent => ({
  file: "dropped.torrent",
  name: "Dropped Release",
  torrentId: "9001",
  videoFiles: 1,
  inLibrary: 1,
  applied: 0,
  ...over,
});

describe("folderTorrentState", () => {
  it("calls a file that will not parse unreadable", () => {
    // The only entry in this folder whose removal is unambiguously right, so it has to be visible and
    // it has to say why.
    const state = folderTorrentState(torrent({ name: null, videoFiles: 0, inLibrary: 0 }));
    expect(state.kind).toBe("unreadable");
    expect(state.label).toBe("unreadable");
  });

  it("separates a release carrying no video from one that is broken", () => {
    // Image sets, comics and audio-only releases are routine. Reporting the routine as a failure would
    // describe neither — the same split `ReloadIndex` makes between its two skip counters.
    const state = folderTorrentState(torrent({ videoFiles: 0, inLibrary: 0 }));
    expect(state.kind).toBe("no-video");
    expect(state.label).toBe("no video");
  });

  it("reports a pack part way through as a fraction, not as applied", () => {
    const state = folderTorrentState(torrent({ videoFiles: 47, inLibrary: 47, applied: 12 }));

    // "Applied" here would be false, and false in the way this design already refused once: no
    // file-level answer can express partial completion, which is why completion is per video.
    expect(state.kind).toBe("partial");
    expect(state.label).toBe("12 / 47 applied");
    expect(state.isPack).toBe(true);
  });

  it("calls a pack applied once every scene is", () => {
    const state = folderTorrentState(torrent({ videoFiles: 47, inLibrary: 47, applied: 47 }));
    expect(state.kind).toBe("applied");
    expect(state.label).toBe("applied");
  });

  it("still reads as applied when more links exist than the torrent has files", () => {
    // A link outlives the library file it was written against, so the two counts can disagree. It must
    // not fall through to a fraction reading "48 / 47".
    expect(folderTorrentState(torrent({ videoFiles: 47, applied: 48 })).label).toBe("applied");
  });

  it("offers a torrent the library can use", () => {
    expect(folderTorrentState(torrent({ inLibrary: 1, applied: 0 })).kind).toBe("to-apply");
  });

  it("states the library's position rather than blaming the torrent", () => {
    // Almost every torrent in a real folder is one whose video was never downloaded — 138,426 of
    // 139,141 indexed files. Nothing is wrong with it, and the wording must not imply there is.
    const state = folderTorrentState(torrent({ inLibrary: 0, applied: 0 }));
    expect(state.kind).toBe("absent");
    expect(state.label).toBe("not in your library");
  });

  it("does not call a single scene a pack", () => {
    expect(folderTorrentState(torrent({ videoFiles: 1 })).isPack).toBe(false);
  });
});

describe("filterTorrents", () => {
  const all = [
    torrent({ file: "north-quay.torrent", name: "North Quay Collection" }),
    torrent({ file: "atlas-03.torrent", name: "Atlas Sessions" }),
    torrent({ file: "broken.torrent", name: null }),
  ];

  it("returns everything for an empty filter", () => {
    expect(filterTorrents(all, "   ")).toHaveLength(3);
  });

  it("matches the filename and the release name alike", () => {
    // Two different strings, and the user may remember either — the file is what they dragged in, the
    // name is what the batch page showed them.
    expect(filterTorrents(all, "atlas-03").map((item) => item.file)).toEqual(["atlas-03.torrent"]);
    expect(filterTorrents(all, "Sessions").map((item) => item.file)).toEqual(["atlas-03.torrent"]);
  });

  it("ignores case, because a filename's is not a decision the user made", () => {
    expect(filterTorrents(all, "NORTH")).toHaveLength(1);
  });

  it("finds an unreadable file by the only handle it has", () => {
    expect(filterTorrents(all, "broken").map((item) => item.file)).toEqual(["broken.torrent"]);
  });

  it("does not mutate the list it was given", () => {
    const source = [...all];
    filterTorrents(source, "atlas");
    expect(source).toHaveLength(3);
  });
});

describe("folderCount", () => {
  it("never claims a total it is not showing", () => {
    // "3182 torrents" under a list of 25 reads as a complete list, and the user stops looking for the
    // one that is missing.
    expect(folderCount(25, 3182, "")).toBe("Showing 25 of 3182");
  });

  it("states the whole folder when the whole folder is on screen", () => {
    expect(folderCount(6, 6, "")).toBe("6 torrents");
    expect(folderCount(1, 1, "")).toBe("1 torrent");
  });

  it("counts matches rather than torrents once a filter is on", () => {
    expect(folderCount(12, 12, "quay")).toBe("12 matches");
    expect(folderCount(1, 1, "quay")).toBe("1 match");
    expect(folderCount(25, 90, "quay")).toBe("Showing 25 of 90 matches");
  });

  it("says which filter found nothing, not just that nothing was found", () => {
    expect(folderCount(0, 0, " quay ")).toBe("No torrent matches “quay”");
  });

  it("tells an empty folder apart from a filter that missed", () => {
    expect(folderCount(0, 0, "")).toBe("The folder is empty");
  });
});

describe("wipeLabel", () => {
  it("names the whole folder when nothing is filtered", () => {
    expect(wipeLabel(3182, "")).toBe("Remove all 3182");
  });

  it("names the filter and its count when one is on", () => {
    // The label is the specification: a "Remove all" that ignored the filter would delete 3182 files
    // while twelve rows were on screen, which is where the worst accidents live.
    expect(wipeLabel(12, "quay")).toBe("Remove 12 matching “quay”");
  });

  it("counts the matches rather than the page of them being shown", () => {
    // 90 matches with 25 listed still removes 90 — safe only because the label says 90.
    expect(wipeLabel(90, "quay")).toBe("Remove 90 matching “quay”");
  });

  it("offers nothing when there is nothing to remove", () => {
    expect(wipeLabel(0, "quay")).toBeNull();
    expect(wipeLabel(0, "")).toBeNull();
  });
});

describe("planRemoval", () => {
  it("warns about the file, not the metadata, for a single torrent", () => {
    // The one respect in which our folder differs from a source folder the operator manages: they
    // dragged this in, and their torrent client may not have it.
    const plan = planRemoval([torrent({ file: "dropped.torrent", applied: 0 })], 6);

    expect(plan.title).toBe("Remove this torrent from the folder?");
    expect(plan.files).toEqual(["dropped.torrent"]);
    expect(plan.lines).toEqual([
      "dropped.torrent is deleted from disk. If it is the only copy you have, it is gone.",
    ]);
    expect(plan.confirmLabel).toBe("Remove");
  });

  it("says applied tags stay only when something was applied", () => {
    // A reassurance about a risk the user does not have is noise, and noise in a confirm is what
    // teaches people to click through them.
    const untouched = planRemoval([torrent({ applied: 0 })], 6);
    expect(untouched.lines.some((line) => line.includes("stay on your videos"))).toBe(false);

    const applied = planRemoval([torrent({ videoFiles: 47, applied: 12 })], 6);
    expect(applied.lines[1]).toBe(
      "Tags already applied stay on your videos. 12 applied rows leave the batch page and come back if you add the file again.",
    );
  });

  it("tells a whole-folder removal apart from a filtered one", () => {
    const everything = planRemoval([torrent({ file: "a.torrent" }), torrent({ file: "b.torrent" })], 2);
    expect(everything.lines[0]).toContain("the whole folder, 2 of them");

    // The filtered case must say it takes every match rather than the page being shown — that is the
    // promise the button's own label already made.
    const some = planRemoval([torrent({ file: "a.torrent" }), torrent({ file: "b.torrent" })], 90);
    expect(some.lines[0]).toContain("every one this filter matches, not just the ones on screen");
  });

  it("promises the operator's own folders only where the fear exists", () => {
    // "Remove everything" is the phrase someone might read as everything the extension can see — and it
    // can see their source folders. A filtered removal never suggests that, so it does not say it.
    expect(planRemoval([torrent({ file: "a.torrent" }), torrent({ file: "b.torrent" })], 2).lines).toContain(
      "Your own torrent folders are not touched.",
    );
    expect(planRemoval([torrent({ file: "a.torrent" }), torrent({ file: "b.torrent" })], 90).lines).not.toContain(
      "Your own torrent folders are not touched.",
    );
  });

  it("does not call a lone torrent in a one-torrent folder a whole-folder removal", () => {
    // It is both, and the single-file wording is the more useful of the two — it names the file.
    const plan = planRemoval([torrent({ file: "only.torrent" })], 1);
    expect(plan.lines[0]).toContain("only.torrent is deleted");
    expect(plan.lines).not.toContain("Your own torrent folders are not touched.");
  });

  it("repeats the count on the button", () => {
    expect(planRemoval([torrent(), torrent(), torrent()], 90).confirmLabel).toBe("Remove 3");
  });
});

describe("canToggleCap", () => {
  it("offers to expand only when something is held back", () => {
    expect(canToggleCap(3182, true)).toBe(true);
    expect(canToggleCap(25, true)).toBe(false);
    expect(canToggleCap(6, true)).toBe(false);
  });

  it("always offers the way back once expanded", () => {
    expect(canToggleCap(3182, false)).toBe(true);
    expect(canToggleCap(6, false)).toBe(true);
  });
});

describe("listingLabel", () => {
  it("says how many it is about to show, so the wait has a size", () => {
    // The stat sweep that produces this costs 8 ms; reading and parsing the same folder was measured
    // at 1.06 s warm. The number is therefore available long before the list is.
    expect(listingLabel(3182)).toBe("Reading 3182 torrents…");
  });

  it("does not say torrents for one torrent", () => {
    expect(listingLabel(1)).toBe("Reading 1 torrent…");
  });

  it("says the plain thing until the count has arrived", () => {
    // Null until the sweep answers, and null for good if it failed. Waiting for a number to wait with
    // would leave the panel blank for the one case it cannot improve.
    expect(listingLabel(null)).toBe("Loading…");
  });

  it("says the plain thing for an empty folder rather than counting nothing", () => {
    expect(listingLabel(0)).toBe("Loading…");
  });
});

describe("afterRemoval", () => {
  // Built per call rather than shared: these tests hand the list to a function that must not mutate
  // it, and one shared array would let a mutation from an earlier test be the thing a later one
  // measures against. That is exactly what hid a splice-in-place mutant.
  const list = () => [torrent({ file: "a.torrent" }), torrent({ file: "b.torrent" }), torrent({ file: "c.torrent" })];

  it("drops what was removed instead of re-reading the folder", () => {
    // The whole point: re-reading re-parses every file to reflect one leaving, and nothing about the
    // rows that stay depends on the one that went — each count is that torrent's own.
    const after = afterRemoval(list(), ["b.torrent"], []);

    expect(after?.map((entry) => entry.file)).toEqual(["a.torrent", "c.torrent"]);
  });

  it("empties the list when everything in it was removed", () => {
    expect(afterRemoval(list(), ["a.torrent", "b.torrent", "c.torrent"], [])).toEqual([]);
  });

  it("asks for a re-read when anything was refused", () => {
    // A refusal means the list the user acted on disagreed with the folder, which is the one moment a
    // fresh read is owed — and refusals are prose rather than names, so there is nothing to subtract.
    expect(afterRemoval(list(), ["a.torrent", "b.torrent"], ["b.torrent: not in the folder"])).toBeNull();
  });

  it("asks for a re-read when there was no list to start with", () => {
    expect(afterRemoval(null, ["a.torrent"], [])).toBeNull();
  });

  it("does not mutate the list it was given", () => {
    const source = list();
    afterRemoval(source, ["a.torrent"], []);

    expect(source).toHaveLength(3);
  });
});

describe("listingFailureMessage", () => {
  it("names the failure rather than saying only that it failed", () => {
    expect(listingFailureMessage("timed out")).toBe("Couldn't read the folder: timed out");
  });
});

describe("folderSectionState", () => {
  it("says not-configured before anything else, even over a listing error", () => {
    // The listing fires whether or not the folder path has arrived yet, so a failure racing an
    // unresolved `folder` must not outrank the truer "not configured".
    expect(folderSectionState(null, null, "network down", null)).toEqual({ kind: "not-configured" });
  });

  it("reports a listing failure with its own message, not the raw reason", () => {
    const state = folderSectionState("/data/torrents", null, "network down", null);

    expect(state).toEqual({ kind: "error", message: "Couldn't read the folder: network down" });
  });

  it("stays on error even once a torrent count is known", () => {
    // A failed listing never got a count from the earlier successful listing — `expected` comes from
    // an independent stat sweep — but a stray one must not paper over the failure.
    const state = folderSectionState("/data/torrents", null, "network down", 3182);

    expect(state.kind).toBe("error");
  });

  it("reports loading, with the sweep's count, while nothing has failed and nothing has arrived", () => {
    expect(folderSectionState("/data/torrents", null, null, 3182)).toEqual({
      kind: "loading",
      label: "Reading 3182 torrents…",
    });
  });

  it("reports empty once the listing arrived with nothing in it", () => {
    expect(folderSectionState("/data/torrents", [], null, 0)).toEqual({ kind: "empty" });
  });

  it("reports list once the listing arrived with something in it", () => {
    expect(folderSectionState("/data/torrents", [torrent()], null, 1)).toEqual({ kind: "list" });
  });
});
