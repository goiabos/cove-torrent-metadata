/**
 * What the batch page says about the torrent folders: that they have moved on since the index was
 * built, and — on an empty install — where a torrent is supposed to go.
 *
 * The index is rebuilt on startup, on upload, and when the user presses Rescan — never on its own.
 * So a torrent copied in by hand is invisible, and nothing on the page said so: the row simply was
 * not there, which looks exactly like a torrent that does not describe anything in the library
 *. The server answers that with a stat-only sweep it compares against the one it took at the
 * last scan; this module turns that answer into a sentence, or into silence.
 *
 * A module rather than a branch in `TorrentBatchPage`, for the reason `reloadStatus.ts` is one: it
 * imports no React and no `@cove/runtime/*`, so every case is reachable from a test without a DOM,
 * and the component keeps only the call.
 *
 * **Three things it must not say.** Not "new" — stat data cannot tell an added file from a replaced
 * one from a deleted one, and the index has no concept of an update anyway, since identity is the
 * file's hash. Not "your index is out of date" — the probe knows the *folder* changed, not whether a
 * rescan would change anything the library can see, which differs under the index cap and under
 * every skip reason. And nothing at all when nothing changed: a line that is always there is a line
 * nobody reads.
 */

/** The folder-state endpoint's response. Mirrors the projection in `TorrentMetadataExtension.cs`. */
export interface FolderStateReport {
  /** The server's own answer, and the authority. The per-folder detail below explains it. */
  changed: boolean;
  folders: Array<{
    path: string;
    /** False for a folder that is configured but not there — a source on an unmounted drive. */
    exists: boolean;
    /** False when the sweep could not be completed. Then `changed` is not an answer, it is a guess. */
    checked: boolean;
    changed: boolean;
    /** True for the extension's own folder, the only one it may write to and the one to name. */
    writable: boolean;
    /**
     * How many `.torrent` files the sweep saw. Zero for a folder that is missing or unreadable — a
     * count of what was seen, not a claim about what is there.
     *
     * It is here because the sweep is cheap and the listing is not: the settings panel can say how
     * many torrents it is about to show before it has read one of them.
     */
    files: number;
  }>;
  /** Folders the last scan read that are no longer configured. Their torrents are still indexed. */
  removed: string[];
}

/** Up to two paths are named; beyond that the count is the more useful sentence. */
function nameThem(paths: readonly string[]): string {
  if (paths.length === 1) return `${paths[0]} has`;
  if (paths.length === 2) return `${paths[0]} and ${paths[1]} have`;
  return `${paths.length} folders have`;
}

/**
 * The notice, or null when there is nothing worth saying.
 *
 * Every sentence ends in what to do about it, because the control is already on the page and the
 * whole failure this fixes was a user not knowing a rescan was owed.
 */
export function folderChangeNotice(report: FolderStateReport): string | null {
  const changed = report.folders.filter((folder) => folder.changed).map((folder) => folder.path);
  // Reported even when nothing changed. A folder that cannot be swept is not evidence of a change —
  // the server refuses to guess, and so does this — but it does mean the answer above it is only
  // about the folders that could be read, and silence would present it as covering all of them.
  const unchecked = report.folders.filter((folder) => !folder.checked).map((folder) => folder.path);

  const sentences: string[] = [];

  if (changed.length > 0) {
    sentences.push(`${nameThem(changed)} changed since the last scan — rescan to pick that up.`);
  } else if (report.changed && report.removed.length === 0) {
    // The server said something changed and named no folder we can point at. Its answer wins: a
    // reason it knows about and this does not is exactly the case where under-reporting would leave
    // the user with a stale index and a page insisting everything was fine.
    sentences.push("The torrent folders have changed since the last scan — rescan to pick that up.");
  }

  if (report.removed.length > 0) {
    // Reads backwards from the others on purpose: the rescan that settles this one *removes* rows.
    sentences.push(
      `${nameThem(report.removed)} been dropped from the settings, but their torrents are still indexed — rescan to clear them.`,
    );
  }

  if (unchecked.length > 0) {
    sentences.push(`Could not check ${unchecked.join(", ")}.`);
  }

  return sentences.length > 0 ? sentences.join(" ") : null;
}

/**
 * Where to put a torrent, for the first screen of an empty install.
 *
 * The sentence this replaces said "copy them into the watched folder and rescan" — singular, and
 * naming nothing. It was thin when the path was fixed and undiscoverable; once the operator could
 * configure any number of source folders it instructed someone to copy a file into a folder it
 * would not name. This is the one place the extension has to be able to say where a file goes.
 *
 * **It names the folder the extension writes to, never a source.** A source folder is read-only, may
 * sit on a drive that is not mounted, and belongs to something else — a torrent client's watch
 * directory is the intended case. Ours is the answer that is always safe.
 *
 * `report` is null until the folder probe the page already makes has answered, or when it failed. The
 * message then falls back to naming no path rather than waiting: the empty state is the *first* thing
 * a new user sees, and a blank panel while a request settles reads as a broken page.
 */
export function whereToPutTorrents(report: FolderStateReport | null): string {
  const ours = report?.folders.find((folder) => folder.writable) ?? null;

  if (!ours) return "No torrents indexed. Drop .torrent files here, or copy them into the extension's torrent folder and press “Rescan folder”.";

  // Said only when it is true, and it is true exactly once: the folder is created by the first upload,
  // so on a fresh install the path being named does not exist yet. Telling someone to copy a file into
  // a folder that is not there, without saying so, is the same failure as not naming it at all.
  const create = ours.exists ? "" : " (create it first — it does not exist yet)";

  return `No torrents indexed. Drop .torrent files here, or copy them into ${ours.path}${create} and press “Rescan folder”.`;
}
