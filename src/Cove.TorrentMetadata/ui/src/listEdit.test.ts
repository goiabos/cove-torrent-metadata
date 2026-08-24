import { describe, expect, it } from "vitest";
import { addedEntry, describeFolderAdd, describeHostAdd } from "./listEdit";

describe("addedEntry", () => {
  it("names the host the server stored, not the one that was typed", () => {
    // The point of asking the server: "https://img.example.com/covers/1.jpg" is stored as the bare
    // host, and the panel has to report what is now in the list rather than what was submitted.
    expect(addedEntry(["a.example"], ["a.example", "img.example.com"])).toBe("img.example.com");
  });

  it("returns null when the list did not grow", () => {
    expect(addedEntry(["a.example"], ["a.example"])).toBeNull();
  });

  it("treats a differently-cased repeat as no growth", () => {
    // `Clean` de-duplicates with OrdinalIgnoreCase, so the server collapses this and the list comes
    // back unchanged. Comparing case-sensitively here would report the existing entry as new.
    expect(addedEntry(["IMG.Example.com"], ["IMG.Example.com"])).toBeNull();
    expect(addedEntry(["IMG.Example.com"], ["img.example.com"])).toBeNull();
  });

  it("does not mistake a removal for an addition", () => {
    expect(addedEntry(["a.example", "b.example"], ["a.example"])).toBeNull();
  });

  it("finds the new host wherever it lands in the list", () => {
    // Not read off the end: `Clean` preserves submission order today, and a caller that assumed the
    // server appends would go wrong silently on the day it sorts.
    expect(addedEntry(["b.example"], ["a.example", "b.example"])).toBe("a.example");
  });

  it("handles the first host added to an empty list", () => {
    expect(addedEntry([], ["a.example"])).toBe("a.example");
  });
});

describe("describeHostAdd", () => {
  it("names the stored host when one was added", () => {
    expect(describeHostAdd([], ["img.example.com"])).toContain("img.example.com");
  });

  it("stays ambiguous when nothing changed", () => {
    // A duplicate and an entry that normalises to nothing are indistinguishable from here, and
    // claiming "already listed" on a typo tells the user they have done something they have not.
    const message = describeHostAdd(["a.example"], ["a.example"]);
    expect(message).toContain("already listed");
    expect(message).toContain("not a public host name");
  });
});

describe("describeFolderAdd", () => {
  it("names the stored path when one was added, and says a rescan is needed", () => {
    // Adding a folder does not rebuild the index — the server deliberately leaves that to the
    // operator — so a message that stopped at "added" would leave them waiting for rows that only
    // appear on the next rescan.
    const message = describeFolderAdd([], ["/srv/torrents"]);

    expect(message).toContain("/srv/torrents");
    expect(message).toContain("Rescan");
  });

  it("explains a folder refusal in folder terms, not host terms", () => {
    // A relative path and a filesystem root are both dropped by `SourceFolderSetting`. Telling
    // someone who typed "../torrents" that it "is not a host name" explains nothing at all.
    const message = describeFolderAdd(["/srv/torrents"], ["/srv/torrents"]);

    expect(message).toContain("already listed");
    expect(message).toContain("absolute path");
    expect(message).not.toContain("host");
  });
});
