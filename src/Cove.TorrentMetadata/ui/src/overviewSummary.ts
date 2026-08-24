/**
 * The one-line summary under the batch page's title.
 *
 * Almost every figure on this page is in *torrent video file* units — indexed files, rows to apply,
 * files not in your library. One is not: `videosMatchableByName` counts **videos**, because the thing
 * it describes is a property of the library rather than of the folder, and because a pack holding
 * 1,913 files named `01.mp4` would otherwise inflate it into noise.
 *
 * Mixing those units silently is a mistake this codebase has already made once — see
 * `TorrentBatchServiceTests`' note that reporting one as the other is how a count stops meaning
 * anything — so the clause names its unit out loud rather than joining the row of figures that share
 * the other one.
 *
 * A module rather than a template literal in the component for the reason `reloadStatus.ts` is one: it
 * imports no React and no `@cove/runtime/*`, so the wording and the omission rules are testable
 * without a DOM.
 */

/** Everything the line reports. Derived by the page from the overview; none of it is fetched here. */
export interface OverviewCounts {
  /** Torrents the indexed files came from. */
  torrents: number;
  /** Video files across every indexed torrent. */
  indexed: number;
  /** Matched rows not yet applied. */
  matched: number;
  /** Rows already applied. */
  applied: number;
  /** Applied rows whose torrent has since gained tags or performers. */
  updated: number;
  /** Indexed video files no library file matches. */
  noMatch: number;
  /**
   * Videos whose size match missed but whose *name* match would not — a file held under the same name
   * at a different size. Videos, not video files.
   */
  matchableByName: number;
}

/**
 * The summary, or "Loading…" before the first overview arrives.
 *
 * `updated` and `matchableByName` are omitted at zero. They describe situations rather than totals, and
 * a zero beside a situation invites the reader to work out what it would have meant.
 */
export function overviewSummary(counts: OverviewCounts | null): string {
  if (counts === null)
    return "Loading…";

  return [
    `${counts.torrents} torrents`,
    `${counts.indexed} video files`,
    `${counts.matched} to apply`,
    `${counts.applied} applied`,
    ...(counts.updated ? [`${counts.updated} with new tags`] : []),
    `${counts.noMatch} not in your library`,
    // Deliberately the last clause and deliberately wordy. It is the only one whose subject is a
    // video, and it is making a claim the reader can check — open one of those videos and the dialog
    // offers the torrent, reporting that it matched on the file name rather than the size.
    ...(counts.matchableByName
      ? [`${counts.matchableByName} of your videos match one by name`]
      : []),
  ].join(" · ");
}
