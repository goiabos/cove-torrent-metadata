import { describe, expect, it } from "vitest";
import {
  UPLOAD_CHUNK_BYTES,
  UPLOAD_CHUNK_FILES,
  chunkForUpload,
  mergeUploadResults,
  type UploadResult,
  isTorrentFile,
  NOT_A_TORRENT,
  NOT_TORRENTS_IN_DROP
} from "./upload";

const sized = (count: number, size: number) => Array.from({ length: count }, () => ({ size }));

describe("chunkForUpload", () => {
  it("sends one request when everything fits", () => {
    expect(chunkForUpload(sized(5, 1000)).length).toBe(1);
  });

  it("sends nothing for nothing", () => {
    expect(chunkForUpload([])).toEqual([]);
  });

  it("splits on the file count", () => {
    const chunks = chunkForUpload(sized(250, 10), 100, UPLOAD_CHUNK_BYTES);

    expect(chunks.map((chunk) => chunk.length)).toEqual([100, 100, 50]);
  });

  it("splits on the byte budget before the count is reached", () => {
    // Four files of 30 bytes against a 100-byte budget: three fit, the fourth starts a request.
    const chunks = chunkForUpload(sized(4, 30), 100, 100);

    expect(chunks.map((chunk) => chunk.length)).toEqual([3, 1]);
  });

  it("keeps a file larger than the whole budget rather than dropping it", () => {
    // Whether an oversized file is acceptable is the server's judgement — it has the per-file cap.
    // Discarding it here would report fewer files than were selected, with no reason for the gap.
    const chunks = chunkForUpload([{ size: 500 }, { size: 10 }], 100, 100);

    expect(chunks).toEqual([[{ size: 500 }], [{ size: 10 }]]);
  });

  it("loses no file and keeps them in order", () => {
    const items = Array.from({ length: 137 }, (_, index) => ({ size: 1000, index }));

    const flattened = chunkForUpload(items, 20, 8000).flat();

    expect(flattened).toEqual(items);
  });

  it("stays under the server's own file cap by default", () => {
    // `TorrentMetadataExtension.MaxTorrentUploadFiles` is 200 and refuses the overflow. The client
    // splitting at a lower number is what makes a big drop succeed rather than be half-refused.
    expect(UPLOAD_CHUNK_FILES).toBeLessThan(200);
  });

  it("leaves the host's default body limit room for multipart overhead", () => {
    // Kestrel's default is 30,000,000 bytes and every part carries a boundary and headers.
    expect(UPLOAD_CHUNK_BYTES).toBeLessThan(30_000_000);
  });
});

describe("mergeUploadResults", () => {
  const result = (over: Partial<UploadResult> = {}): UploadResult => ({
    saved: 0,
    rejected: [],
    added: [],
    torrents: 0,
    files: 0,
    ...over,
  });

  it("accumulates what was uploaded", () => {
    const merged = mergeUploadResults([
      result({ saved: 2, rejected: ["a: bad"], added: [{ torrentName: "one", fileName: "1.mp4", fanOut: 1 }] }),
      result({ saved: 3, rejected: ["b: bad"], added: [{ torrentName: "two", fileName: "2.mp4", fanOut: 2 }] }),
    ]);

    expect(merged.saved).toBe(5);
    expect(merged.rejected).toEqual(["a: bad", "b: bad"]);
    expect(merged.added.map((entry) => entry.torrentName)).toEqual(["one", "two"]);
  });

  it("takes the index totals from the last response rather than summing them", () => {
    // Each request ends with a reload, so `torrents` and `files` are the whole folder as it stood
    // afterwards. Summing would report it once per chunk — 3,218 torrents becoming tens of thousands.
    const merged = mergeUploadResults([
      result({ torrents: 100, files: 400 }),
      result({ torrents: 180, files: 720 }),
    ]);

    expect(merged.torrents).toBe(180);
    expect(merged.files).toBe(720);
  });

  it("answers emptily when nothing was sent", () => {
    expect(mergeUploadResults([])).toEqual(result());
  });
});

describe("isTorrentFile", () => {
  // One definition, because the batch page's drop area and the per-video drop zone both judge a drop
  // and each carried its own copy of this test.
  it("accepts a .torrent whatever case it is written in", () => {
    expect(isTorrentFile({ name: "release.torrent" })).toBe(true);
    expect(isTorrentFile({ name: "RELEASE.TORRENT" })).toBe(true);
    expect(isTorrentFile({ name: "Release.Torrent" })).toBe(true);
  });

  it("refuses anything else, including a name that merely contains the word", () => {
    expect(isTorrentFile({ name: "notes.txt" })).toBe(false);
    expect(isTorrentFile({ name: "torrent" })).toBe(false);
    expect(isTorrentFile({ name: "my.torrent.zip" })).toBe(false);
    expect(isTorrentFile({ name: "" })).toBe(false);
  });

  it("keeps a different sentence for each surface, since one drop is a folder and one is a file", () => {
    // The rule above is shared; the wording is not, and that is deliberate — a folder-sized drop and a
    // single file cannot be told the same thing.
    expect(NOT_TORRENTS_IN_DROP).not.toBe(NOT_A_TORRENT);
    expect(NOT_TORRENTS_IN_DROP).toContain("files");
  });
});
