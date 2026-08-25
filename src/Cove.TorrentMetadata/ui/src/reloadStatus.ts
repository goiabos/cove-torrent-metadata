/**
 * What a rescan says it did.
 *
 * A reload walks every configured folder and, for most users, indexes almost everything it finds. The
 * interesting part is what it did *not* index, because none of that reaches the page by any other
 * route: a skipped file appears in no row and in no count, not even the batch page's unmatched
 * number, which is per indexed video file. That once made "four of your torrents are
 * unreadable" look exactly like "you do not have those files", and only the first is something the
 * user can act on.
 *
 * This is the one place that decides what the rescan line says. It is a module rather than a branch
 * inside `TorrentBatchPage` for the reason `queue.ts` and `coverQueue.ts` are: it imports no React and
 * no `@cove/runtime/*`, so it is testable without a DOM or a stand-in for the host runtime, and the
 * component keeps only the call.
 *
 * **Nothing is reported at zero.** A rescan of a clean folder should read as one clean sentence, not
 * as a row of zeroes inviting the user to worry about four things that did not happen. The corollary
 * is that a number appearing here is always a number worth reading.
 */

/** The reload endpoint's response. Mirrors the projection in `TorrentMetadataExtension.cs`. */
export interface ReloadReport {
  torrents: number;
  files: number;
  folder: string | null;
  /**
   * Every folder read, in order, so the page can name one that is missing or empty. `writable` marks
   * the extension's own folder — exactly one entry, and never one the operator configured.
   */
  folders: Array<{ path: string; exists: boolean; torrents: number; writable: boolean }>;
  /** True when the index cap was reached and later folders went unread. */
  truncated: boolean;
  /**
   * Directories the walk could not open, so nothing under them was seen — a permission, a share that
   * went away, or a symlink loop.
   *
   * Not part of `skipped`, and the separation is the point: those count *files* the walk saw and
   * passed over, while an unopened directory hides an unknown number of files behind it. Summing the
   * two would produce a figure in no unit at all.
   */
  unreadableDirectories: number;
  /**
   * Files the walk passed over, by reason. The cap is not one of these — it is `truncated`, because it
   * stops the walk rather than passing over a file.
   */
  skipped: {
    unreadable: number;
    malformed: number;
    withoutVideo: number;
    duplicates: number;
    total: number;
  };
}

/**
 * Reasons in the order they are named, each with the wording for a count of that kind.
 *
 * Ordered by how much they ask of the user rather than by how the server happens to declare them:
 * unreadable and malformed are defects in the folder, `withoutVideo` is routine, and a duplicate is
 * routine and usually invisible. Reading the actionable ones first is the whole point of separating
 * them.
 */
const REASONS: Array<{ key: keyof ReloadReport["skipped"]; describe: (count: number) => string }> = [
  { key: "unreadable", describe: (count) => `${count} unreadable` },
  { key: "malformed", describe: (count) => `${count} not readable as a torrent` },
  { key: "withoutVideo", describe: (count) => `${count} with no video` },
  { key: "duplicates", describe: (count) => `${count} already indexed` },
];

/** The skipped clause, or "" when the walk indexed everything it saw. */
export function skippedSummary(skipped: ReloadReport["skipped"]): string {
  const named = REASONS.filter((reason) => skipped[reason.key] > 0).map((reason) =>
    reason.describe(skipped[reason.key]),
  );

  // `total` is the server's, not a sum of the four above, so the two can disagree — a reason added
  // server-side and not here is exactly that case. Say so rather than under-reporting: the count the
  // user acts on stays true even when this list has not caught up.
  if (named.length === 0)
    return skipped.total > 0 ? `Skipped ${skipped.total}.` : "";

  const accounted = REASONS.reduce((sum, reason) => sum + skipped[reason.key], 0);
  const rest = skipped.total - accounted;

  return `Skipped ${[...named, ...(rest > 0 ? [`${rest} for other reasons`] : [])].join(", ")}.`;
}

/** The whole rescan status line: what was read, what was missing, what was skipped, and the cap. */
export function reloadStatus(report: ReloadReport): string {
  const read = report.folders.filter((folder) => folder.exists);
  // A folder that is not there is reported rather than thrown — a source can live on a drive that is
  // not mounted — so this is the only place it surfaces. Silence would read as a folder holding no
  // torrents, which is a different problem with a different fix.
  //
  // **Ours is not one of them.** The extension's own folder is created by the first upload, so on an
  // install where nothing has been dropped yet it is legitimately absent — and naming it here read as
  // a misconfiguration the user had to go and fix, in a sentence otherwise reserved for a source they
  // chose and we could not open. Where its absence *does* matter is the empty state, which has to send
  // someone to a folder to copy files into: `whereToPutTorrents` says "create it first" there, and
  // that is the one place it is worth saying.
  const missing = report.folders
    .filter((folder) => !folder.exists && !folder.writable)
    .map((folder) => folder.path);

  return [
    `Read ${read.length} folder(s): ${report.torrents} torrents, ${report.files} video files.`,
    missing.length ? `Not found: ${missing.join(", ")}.` : "",
    // Its own sentence rather than a clause of the skip list, because it is a different claim: the
    // skip counts say what was looked at and passed over, this says there is part of the folder we
    // could not look at. Naming a count of directories is all the walk can honestly offer — it never
    // opened them, so it does not know how many torrents are behind them.
    report.unreadableDirectories > 0
      ? `${report.unreadableDirectories} folder(s) could not be read — check permissions.`
      : "",
    skippedSummary(report.skipped),
    report.truncated ? "Stopped at the index cap — narrow a folder and rescan." : "",
  ]
    .filter(Boolean)
    .join(" ");
}
