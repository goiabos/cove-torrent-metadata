import { describe, expect, it } from "vitest";
import { folderChangeNotice, whereToPutTorrents, type FolderStateReport } from "./folderState";

const folder = (over: Partial<FolderStateReport["folders"][number]> = {}) => ({
  path: "/data/torrent-metadata",
  exists: true,
  checked: true,
  changed: false,
  writable: true,
  files: 1,
  ...over,
});

const report = (over: Partial<FolderStateReport> = {}): FolderStateReport => ({
  changed: false,
  folders: [folder()],
  removed: [],
  ...over,
});

describe("folderChangeNotice", () => {
  it("says nothing when the folders look the way the index left them", () => {
    // The batch page is looked at often and rescanned rarely, so this is the answer almost every
    // time. A line that is always present is a line nobody reads, which would cost the one visit
    // where it mattered.
    expect(folderChangeNotice(report())).toBeNull();
  });

  it("names the folder that changed and what to do about it", () => {
    const notice = folderChangeNotice(
      report({ changed: true, folders: [folder({ path: "/srv/torrents", changed: true })] }),
    );

    expect(notice).toBe("/srv/torrents has changed since the last scan — rescan to pick that up.");
  });

  it("never calls a change new", () => {
    // Stat data cannot tell an added file from a replaced or deleted one, and the index has no
    // concept of an update — identity is the file's hash, so a replaced torrent is a different
    // torrent. "3 new torrents" would be a claim the probe cannot support.
    const notice = folderChangeNotice(
      report({ changed: true, folders: [folder({ changed: true })] }),
    );

    expect(notice).not.toMatch(/new/i);
  });

  it("names two folders and counts three", () => {
    const two = folderChangeNotice(
      report({
        changed: true,
        folders: [folder({ path: "/a", changed: true }), folder({ path: "/b", changed: true })],
      }),
    );
    const three = folderChangeNotice(
      report({
        changed: true,
        folders: [
          folder({ path: "/a", changed: true }),
          folder({ path: "/b", changed: true }),
          folder({ path: "/c", changed: true }),
        ],
      }),
    );

    expect(two).toContain("/a and /b have changed");
    // A list of paths stops being readable well before it stops being accurate, and the operator can
    // configure any number of sources.
    expect(three).toContain("3 folders have changed");
    expect(three).not.toContain("/a");
  });

  it("falls back to the server's answer when it changed something this does not understand", () => {
    // The server's `changed` is the authority; the per-folder detail explains it. A reason it knows
    // about and this file does not is exactly when under-reporting would leave a stale index behind
    // a page insisting everything was fine — the same rule `reloadStatus` follows for `skipped.total`.
    const notice = folderChangeNotice(report({ changed: true }));

    expect(notice).toBe("The torrent folders have changed since the last scan — rescan to pick that up.");
  });

  it("says a dropped folder still has torrents in the index", () => {
    const notice = folderChangeNotice(report({ changed: true, removed: ["/old/sources"] }));

    // The only case where rescanning *removes* rows, so it cannot share the wording of the others —
    // a user told to "pick that up" would be looking for something to arrive.
    expect(notice).toBe(
      "/old/sources has been dropped from the settings, but their torrents are still indexed — rescan to clear them.",
    );
  });

  it("reports both a change and a dropped folder rather than only the first", () => {
    const notice = folderChangeNotice(
      report({ changed: true, folders: [folder({ path: "/srv", changed: true })], removed: ["/old"] }),
    );

    expect(notice).toContain("/srv has changed");
    expect(notice).toContain("/old has been dropped");
  });

  it("says a folder could not be checked, even when nothing else changed", () => {
    // Not a change — the server refuses to guess from a sweep that failed, and so does this. But the
    // silence above it would otherwise present "nothing changed" as covering a folder nobody read.
    const notice = folderChangeNotice(
      report({ folders: [folder(), folder({ path: "/mnt/nas", checked: false })] }),
    );

    expect(notice).toBe("Could not check /mnt/nas.");
  });

  it("does not treat a folder it could not check as a folder that changed", () => {
    const notice = folderChangeNotice(
      report({ folders: [folder({ path: "/mnt/nas", checked: false })] }),
    );

    // The exact sentence, not merely "does not say changed": with the could-not-check clause gone
    // this would read as null, which also does not say changed and would pass while telling the user
    // nothing at all.
    expect(notice).toBe("Could not check /mnt/nas.");
  });
});

describe("whereToPutTorrents", () => {
  it("names the folder rather than calling it the watched folder", () => {
    const message = whereToPutTorrents(report({ folders: [folder({ path: "/data/torrent-metadata" })] }));

    // The whole defect: the first screen of an empty install told the user to copy files into a
    // folder it would not name, and once sources became configurable there was no longer even a
    // single folder for "the watched folder" to mean.
    expect(message).toContain("/data/torrent-metadata");
    expect(message).not.toContain("the watched folder");
  });

  it("names the folder the extension writes to, never a source folder", () => {
    const message = whereToPutTorrents(
      report({
        folders: [
          folder({ path: "/mnt/client/watch", writable: false }),
          folder({ path: "/data/torrent-metadata", writable: true }),
        ],
      }),
    );

    // A source is read-only, may be on an unmounted drive, and is usually something else's — a
    // torrent client's watch directory is the intended case. Ours is the only always-safe answer, and
    // it is not first in the list here on purpose: position must not be what picks it.
    expect(message).toContain("/data/torrent-metadata");
    expect(message).not.toContain("/mnt/client/watch");
  });

  it("says the folder has to be created when it is not there yet", () => {
    const message = whereToPutTorrents(report({ folders: [folder({ exists: false })] }));

    // True exactly once, on a fresh install: the folder is created by the first upload. Naming a path
    // that does not exist, without saying so, fails the same user the same way.
    expect(message).toContain("create it first");
  });

  it("does not tell the user to create a folder that is already there", () => {
    expect(whereToPutTorrents(report())).not.toContain("create it");
  });

  it("still says something useful before the probe has answered", () => {
    // Null until the folder probe returns, and null for good if it failed. The empty state is the
    // first thing a new user sees, so it renders now and without a path rather than waiting.
    const message = whereToPutTorrents(null);

    expect(message).toContain("Drop .torrent files here");
    expect(message).not.toContain("undefined");
  });

  it("falls back the same way when no folder is flagged writable", () => {
    expect(whereToPutTorrents(report({ folders: [folder({ writable: false })] }))).toBe(
      whereToPutTorrents(null),
    );
  });
});
