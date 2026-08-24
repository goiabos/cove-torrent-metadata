import { describe, expect, it } from "vitest";
import { emptyStateMessage, type EmptyStateInput } from "./emptyState";

/**
 * The four situations that produce an empty table.
 *
 * Two of these branches were string literals inside the component, so nothing could tell
 * that the page was about to send a user to check a folder that was fine.
 */
const input = (over: Partial<EmptyStateInput> = {}): EmptyStateInput => ({
  scope: { query: "", packsOnly: false, pack: null },
  onPage: 0,
  indexed: 0,
  torrents: 0,
  total: 0,
  folderState: null,
  ...over,
});

describe("emptyStateMessage", () => {
  it("blames the filter first, because it is the cause the reviewer can undo", () => {
    const message = emptyStateMessage(
      input({ scope: { query: "nothing matches this", packsOnly: false, pack: null }, onPage: 12, indexed: 40, total: 12 }),
    );

    expect(message).toContain("nothing matches this");
  });

  it("does not blame the filter when there is nothing behind it either", () => {
    // `onPage` is zero, so the emptiness is the page's rather than the filter's. Reporting the query
    // here would send the reviewer to clear a filter that is not what is hiding anything.
    const message = emptyStateMessage(
      input({ scope: { query: "anything", packsOnly: false, pack: null }, onPage: 0, indexed: 0 }),
    );

    expect(message).not.toContain("anything");
  });

  it("names where a torrent goes when nothing has been indexed", () => {
    // The only branch about the folder rather than the library, and the only one that must name the
    // folder this extension writes to.
    const message = emptyStateMessage(input({ indexed: 0, folderState: null }));

    expect(message).toContain("No torrents indexed");
    expect(message).toContain("Rescan folder");
  });

  it("carries the folder answer through rather than wording its own", () => {
    // `folderState.ts` owns this sentence, including the "create it first" it must add when the write
    // folder does not exist yet. A copy of that rule here would be a second definition of it.
    const message = emptyStateMessage(
      input({
        indexed: 0,
        folderState: { folders: [{ path: "/data/torrents", writable: true, exists: false, torrents: 0 }] } as never,
      }),
    );

    expect(message).toContain("/data/torrents");
    expect(message).toContain("does not exist yet");
  });

  it("reports both numbers when torrents were read and none is in the library", () => {
    // The common answer on a fresh library, and the one that has to read as "working, but not about
    // your library" rather than as a failure.
    const message = emptyStateMessage(input({ indexed: 40, torrents: 3, total: 0 }));

    expect(message).toContain("40 video files");
    expect(message).toContain("3 torrents");
  });

  it("points at the control that would reveal the rows it is hiding", () => {
    // The one empty state that is good news. Without naming "Hide applied" it reads as a page that
    // has lost the rows it is holding.
    const message = emptyStateMessage(input({ indexed: 40, torrents: 3, total: 7 }));

    expect(message).toContain("Hide applied");
  });

  it("prefers the folder answer over the match answer when neither has anything", () => {
    // Both `indexed` and `total` are zero. "None of the 0 video files…" is true and useless; the
    // folder sentence is the one that tells the user what to do next.
    const message = emptyStateMessage(input({ indexed: 0, torrents: 0, total: 0 }));

    expect(message).not.toContain("None of the 0");
  });
});
