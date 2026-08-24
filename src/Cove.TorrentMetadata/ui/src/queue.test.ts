/**
 * Walking the matched rows, as decisions rather than as component state.
 *
 * Fixtures are invented here, in code — never transcribed from a real torrent, under the same rule
 * as the rest of this directory.
 */

import { describe, expect, it } from "vitest";
import type { BatchRow } from "./api";
import {
  canStep,
  currentRow,
  describeQueuePosition,
  describeQueueRun,
  jumpToRow,
  keyStep,
  KEY_STEP_HINT,
  markApplied,
  openQueue,
  resyncQueue,
  rowKey,
  rowRef,
  STEP_KEYS,
  stepQueue,
  wasApplied,
} from "./queue";

const row = (over: Partial<BatchRow> = {}): BatchRow => ({
  torrentName: "release.torrent",
  fileName: "scene.mp4",
  torrentId: null,
  fanOut: 1,
  status: "matched",
  videoId: 1,
  videoTitle: null,
  videoHasImage: false,
  videoTagCount: 0,
  tagsToAdd: 0,
  tagsToCreate: 0,
  performersToAdd: 0,
  torrentCoverUrl: null,
  torrentCoverAllowed: false,
  ...over,
});

const rows = [
  row({ fileName: "one.mp4", videoId: 1 }),
  row({ fileName: "two.mp4", videoId: 2 }),
  row({ fileName: "three.mp4", videoId: 3, fanOut: 40 }),
];

describe("rowKey", () => {
  it("separates two torrents describing one video", () => {
    // 2.32% of corpus file sizes are shared and 20 of the real library's files match more than one
    // torrent, so a video id alone names every row the video appears in.
    const shared = [
      row({ torrentName: "a.torrent", fileName: "scene.mp4", videoId: 5 }),
      row({ torrentName: "b.torrent", fileName: "scene.mp4", videoId: 5 }),
    ];

    expect(rowKey(shared[0])).not.toBe(rowKey(shared[1]));
  });

  it("separates two files of one pack that share a name", () => {
    // The other direction, and the one the old torrentName/fileName key could not express: the server
    // strips the directory, so `Disc1/01.mp4` and `Disc2/01.mp4` both arrive as `01.mp4`. They are
    // different videos, and one tick used to apply both.
    const disc1 = row({ torrentName: "pack", fileName: "01.mp4", videoId: 11, torrentId: "42" });
    const disc2 = row({ torrentName: "pack", fileName: "01.mp4", videoId: 12, torrentId: "42" });

    expect(rowKey(disc1)).not.toBe(rowKey(disc2));
  });

  it("treats two copies of one tracker id as one row", () => {
    // A tracker keeps a torrent's id when its tags are edited, so a re-downloaded re-tagged release is
    // a second file with the same id. The server collapses them to one row, and a client that keyed
    // them apart would show a row the server will not accept an apply for.
    const first = row({ torrentName: "release", fileName: "a.mp4", videoId: 7, torrentId: "42" });
    const second = row({ torrentName: "release", fileName: "b.mp4", videoId: 7, torrentId: "42" });

    expect(rowKey(first)).toBe(rowKey(second));
  });

  it("does not confuse a torrent named like an id with one carrying that id", () => {
    // The prefixes are what keep the two spaces apart; without them these are the same key.
    const named = row({ torrentName: "12345", videoId: 3, torrentId: null });
    const identified = row({ torrentName: "something else", videoId: 3, torrentId: "12345" });

    expect(rowKey(named)).not.toBe(rowKey(identified));
  });

  it("falls back to the name when the torrent carries no id", () => {
    // A torrent whose comment has no recognisable URL still has to be addressable.
    const one = row({ torrentName: "a", videoId: 3, torrentId: null });
    const other = row({ torrentName: "b", videoId: 3, torrentId: null });

    expect(rowKey(one)).not.toBe(rowKey(other));
  });
});

describe("rowRef", () => {
  it("names a row by the same identity rowKey uses", () => {
    // The server folds these three fields with the same rule; sending anything else means ticking one
    // row and applying another.
    const subject = row({ torrentName: "release", videoId: 9, torrentId: "42" });

    expect(rowRef(subject)).toEqual({ videoId: 9, torrentId: "42", torrentName: "release" });
  });
});

describe("KEY_STEP_HINT", () => {
  const asEvent = (key: string) => ({ key, withModifier: false, typing: false });

  it("names keys that step the way it says they do", () => {
    // The hint used to be prose in the component reading "← → or J K", which pairs J with ← — while
    // keyStep maps j forward. Nothing could catch it, because a literal in a component is reachable by
    // no test. Derived from STEP_KEYS now, and this is what holds the two together.
    for (const key of STEP_KEYS.forward.keys) expect(keyStep(asEvent(key))).toBe(1);
    for (const key of STEP_KEYS.back.keys) expect(keyStep(asEvent(key))).toBe(-1);
  });

  it("says next before previous, in the order the labels are listed", () => {
    expect(KEY_STEP_HINT).toBe("→ or J next, ← or K previous");
    expect(KEY_STEP_HINT.indexOf("next")).toBeLessThan(KEY_STEP_HINT.indexOf("previous"));
  });
});

describe("openQueue", () => {
  it("starts at the row the reviewer clicked, not at the top", () => {
    const queue = openQueue(rows, rows[1]);

    expect(queue.index).toBe(1);
    expect(currentRow(queue)?.fileName).toBe("two.mp4");
  });

  it("keeps the list it was given, packs included", () => {
    expect(openQueue(rows, rows[0]).rows).toHaveLength(3);
  });

  it("matches by key, so an equal row from a re-render still finds its place", () => {
    expect(openQueue(rows, row({ fileName: "three.mp4", videoId: 3, fanOut: 40 })).index).toBe(2);
  });

  it("becomes a queue of one when the row is not in the list", () => {
    // Absent by *video*, since that is half of what identifies a row. This fixture used to vary only
    // the file name, which stopped meaning "a different row" when the identity became the video plus
    // the torrent — it named the same row as `rows[0]` and the walk correctly found it.
    const queue = openQueue(rows, row({ fileName: "elsewhere.mp4", videoId: 99 }));

    expect(queue.rows).toHaveLength(1);
    expect(queue.index).toBe(0);
    expect(currentRow(queue)?.fileName).toBe("elsewhere.mp4");
  });

  it("starts with nothing applied", () => {
    expect(openQueue(rows, rows[0]).applied.size).toBe(0);
  });
});

describe("stepQueue", () => {
  it("advances one row at a time", () => {
    expect(currentRow(stepQueue(openQueue(rows, rows[0]), 1))?.fileName).toBe("two.mp4");
  });

  it("goes back", () => {
    expect(currentRow(stepQueue(openQueue(rows, rows[2]), -1))?.fileName).toBe("two.mp4");
  });

  it("stops at the end rather than wrapping", () => {
    const last = openQueue(rows, rows[2]);

    expect(stepQueue(last, 1)).toBe(last);
    expect(canStep(last, 1)).toBe(false);
  });

  it("stops at the start rather than wrapping", () => {
    const first = openQueue(rows, rows[0]);

    expect(stepQueue(first, -1)).toBe(first);
    expect(canStep(first, -1)).toBe(false);
  });

  it("cannot move a queue of one in either direction", () => {
    const alone = openQueue([rows[0]], rows[0]);

    expect(canStep(alone, -1)).toBe(false);
    expect(canStep(alone, 1)).toBe(false);
  });

  it("carries the applied set across a step", () => {
    const walked = stepQueue(markApplied(openQueue(rows, rows[0])), 1);

    expect(walked.applied.has(rowKey(rows[0]))).toBe(true);
  });

  it("does not mutate the queue it was given", () => {
    const queue = openQueue(rows, rows[0]);
    stepQueue(queue, 1);

    expect(queue.index).toBe(0);
  });
});

describe("markApplied", () => {
  it("records the row being reviewed", () => {
    expect(markApplied(openQueue(rows, rows[1])).applied.has(rowKey(rows[1]))).toBe(true);
  });

  it("records the row, never a status — that is the server's to decide", () => {
    const applied = markApplied(openQueue(rows, rows[1]));

    expect(currentRow(applied)?.status).toBe("matched");
    expect(applied.rows).toEqual(rows);
  });

  it("counts one row once, however many times it is applied", () => {
    const twice = markApplied(markApplied(openQueue(rows, rows[0])));

    expect(twice.applied.size).toBe(1);
  });

  it("accumulates across a walk", () => {
    const walked = markApplied(stepQueue(markApplied(openQueue(rows, rows[0])), 1));

    expect(walked.applied.size).toBe(2);
  });
});

describe("describeQueuePosition", () => {
  it("counts from one, because a person reads it", () => {
    expect(describeQueuePosition(openQueue(rows, rows[0]))).toBe("1 of 3");
    expect(describeQueuePosition(openQueue(rows, rows[2]))).toBe("3 of 3");
  });
});

describe("describeQueueRun", () => {
  it("says nothing about a walk that changed nothing", () => {
    expect(describeQueueRun(openQueue(rows, rows[0]))).toBeNull();
  });

  it("reports rows applied against the walk, not tags", () => {
    const walked = markApplied(stepQueue(markApplied(openQueue(rows, rows[0])), 1));

    expect(describeQueueRun(walked)).toBe("Applied 2 of the 3 rows in this walk.");
  });
});

describe("jumpToRow", () => {
  it("moves to a row clicked in the list beside the review", () => {
    const queue = jumpToRow(openQueue(rows, rows[0]), rows[2]);

    expect(queue.index).toBe(2);
    expect(currentRow(queue)).toBe(rows[2]);
  });

  it("keeps the record of what this walk has applied", () => {
    // The whole reason this is not `openQueue`: that set is what the refresh on close and the run
    // summary are built from, and a click must not quietly discard it.
    const walked = markApplied(openQueue(rows, rows[0]));

    expect([...jumpToRow(walked, rows[2]).applied]).toEqual([rowKey(rows[0])]);
  });

  it("leaves the queue's own rows alone", () => {
    expect(jumpToRow(openQueue(rows, rows[0]), rows[1]).rows).toBe(rows);
  });
});

describe("resyncQueue", () => {
  it("follows the row under review into the refiltered list", () => {
    const queue = openQueue(rows, rows[2]);
    const narrowed = [rows[1], rows[2]];

    const resynced = resyncQueue(queue, narrowed);

    expect(currentRow(resynced)).toBe(rows[2]);
    expect(describeQueuePosition(resynced)).toBe("2 of 2");
  });

  it("keeps the applied record, because a filter does not undo an act", () => {
    const walked = markApplied(openQueue(rows, rows[0]));

    expect([...resyncQueue(walked, [rows[0], rows[1]]).applied]).toEqual([rowKey(rows[0])]);
  });

  it("becomes a walk of one when the filter hides the row being reviewed", () => {
    // Not a degenerate case: with the review beside the list rather than over it, a filter can hide
    // the row the reviewer is still reading. `1 of 1` is honest; an index into a list that no longer
    // holds the row is not, and closing the review under them is worse than either.
    const queue = openQueue(rows, rows[2]);

    const resynced = resyncQueue(queue, [rows[0], rows[1]]);

    expect(currentRow(resynced)).toBe(rows[2]);
    expect(describeQueuePosition(resynced)).toBe("1 of 1");
    expect(canStep(resynced, 1)).toBe(false);
    expect(canStep(resynced, -1)).toBe(false);
  });

  it("survives the list emptying entirely", () => {
    const resynced = resyncQueue(openQueue(rows, rows[1]), []);

    expect(currentRow(resynced)).toBe(rows[1]);
    expect(describeQueuePosition(resynced)).toBe("1 of 1");
  });

  it("re-reads the position when the row stayed and the list around it changed", () => {
    const queue = openQueue(rows, rows[0]);

    expect(describeQueuePosition(resyncQueue(queue, [rows[2], rows[1], rows[0]]))).toBe("3 of 3");
  });
});

describe("wasApplied", () => {
  it("marks the row this walk applied, and only that row", () => {
    const walked = markApplied(openQueue(rows, rows[1]));

    expect(wasApplied(walked, rows[1])).toBe(true);
    expect(wasApplied(walked, rows[0])).toBe(false);
  });

  it("marks a row rather than a video, since a video can appear in two of them", () => {
    const shared = [
      row({ torrentName: "a.torrent", fileName: "scene.mp4", videoId: 5 }),
      row({ torrentName: "b.torrent", fileName: "scene.mp4", videoId: 5 }),
    ];
    const walked = markApplied(openQueue(shared, shared[0]));

    expect(wasApplied(walked, shared[0])).toBe(true);
    expect(wasApplied(walked, shared[1])).toBe(false);
  });

  it("is false across a walk that applied nothing", () => {
    expect(wasApplied(openQueue(rows, rows[0]), rows[0])).toBe(false);
  });
});

describe("keyStep", () => {
  const press = (key: string, over: Partial<{ withModifier: boolean; typing: boolean }> = {}) =>
    keyStep({ key, withModifier: false, typing: false, ...over });

  it("moves forward on the arrow and on j", () => {
    expect(press("ArrowRight")).toBe(1);
    expect(press("j")).toBe(1);
    expect(press("J")).toBe(1);
  });

  it("moves back on the arrow and on k", () => {
    expect(press("ArrowLeft")).toBe(-1);
    expect(press("k")).toBe(-1);
    expect(press("K")).toBe(-1);
  });

  it("does nothing while the reviewer is typing", () => {
    // Both filters are text boxes a step away from the list they filter. A `j` that jumped to the next
    // row mid-word is the whole feature's reputation.
    expect(press("j", { typing: true })).toBeNull();
    expect(press("ArrowRight", { typing: true })).toBeNull();
  });

  it("leaves a modified key to the browser and to the host", () => {
    expect(press("ArrowRight", { withModifier: true })).toBeNull();
  });

  it("binds nothing else — apply is not a key press", () => {
    // Stepping is reversible; applying writes to the library.
    for (const key of ["a", "A", "Enter", " ", "Escape", "ArrowDown", "n", "p"])
      expect(press(key)).toBeNull();
  });
});
