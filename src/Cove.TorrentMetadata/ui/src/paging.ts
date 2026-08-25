/**
 * Which slice of the filtered rows the batch page draws.
 *
 * The overview is one request that answers with every row — 3,199 torrents over 138,153 video files
 * on the measured library — and the page then filters, sweeps, walks and bulk-applies over that whole
 * set. So this is a **view** and never a fetch: the scope stays the filtered list, and paging decides
 * only how much of it is in the DOM at once. Everything that reads the scope — the header sweep, the
 * apply plan, the review walk — goes on reading `visible`, because narrowing any of those to the page
 * would make a filter mean one thing and a page turn mean another. It is the rule a filtered list
 * already imposes on every control beside it, one layer down.
 *
 * A module rather than a branch in `TorrentBatchPage`, for the reason `reloadStatus.ts` and
 * `folderState.ts` are: it imports no React and no `@cove/runtime/*`, so the arithmetic that decides
 * what is on screen is reachable from a test without a DOM.
 *
 * **The count and the slice must come from the same call.** A range label derived separately from the
 * rows it labels is the `writeFolder.ts` failure again — the label *is* the claim about what you are
 * looking at — so `takePage` returns the rows, the clamped page and the range together and callers
 * never recompute one of them.
 */

/** "All" — one page holding everything. Cove spells its own infinite page size the same way. */
export const ALL_ROWS = 0;

/**
 * The sizes offered, matching Cove's own list pages (`LIST_PER_PAGE_OPTIONS`) so the control reads as
 * part of the host rather than as this extension's invention.
 */
export const PER_PAGE_OPTIONS: readonly number[] = [20, 40, 60, 120, 250, 500, 1000];

/**
 * Where it starts. Lower than a media grid's would be, because a row here is two thumbnails and six
 * columns rather than one card, and higher than the host's 24, because this table is triaged in long
 * sittings and a page turn every 24 rows over four figures of them is its own kind of unusable.
 */
export const DEFAULT_PER_PAGE = 60;

/** How many pages a list of `total` rows fills. Always at least one, so an empty list has a page 1. */
export function pageCount(total: number, perPage: number): number {
  if (perPage === ALL_ROWS) return 1;
  return Math.max(1, Math.ceil(total / perPage));
}

/** The nearest page that exists. A page can outlive the list it was on — a filter narrows under it. */
export function clampPage(page: number, total: number, perPage: number): number {
  return Math.min(Math.max(1, Math.floor(page) || 1), pageCount(total, perPage));
}

/** The 1-based page holding a 0-based row index, so a walk that steps off the page can bring it along. */
export function pageOf(index: number, perPage: number): number {
  if (perPage === ALL_ROWS || index < 0) return 1;
  return Math.floor(index / perPage) + 1;
}

/** One page of rows, with everything a caller needs to say what it is looking at. */
export interface PageView<T> {
  rows: T[];
  /** The page actually shown, which is not necessarily the one asked for. */
  page: number;
  pages: number;
  /** 1-based position of the first and last row shown; both 0 when there are none. */
  from: number;
  to: number;
  total: number;
}

/** The slice, the page it turned out to be, and the range it covers — one call, one answer. */
export function takePage<T>(rows: readonly T[], page: number, perPage: number): PageView<T> {
  const total = rows.length;
  const pages = pageCount(total, perPage);
  const current = clampPage(page, total, perPage);

  if (perPage === ALL_ROWS) {
    return { rows: [...rows], page: 1, pages, from: total > 0 ? 1 : 0, to: total, total };
  }

  const start = (current - 1) * perPage;
  const slice = rows.slice(start, start + perPage);

  return {
    rows: slice,
    page: current,
    pages,
    from: slice.length > 0 ? start + 1 : 0,
    to: start + slice.length,
    total,
  };
}

/**
 * What the page is showing, or null when the whole list is on screen.
 *
 * Silent at one page, under the same rule the rescan line follows: a range that always says
 * "1–37 of 37" is a line nobody reads, and it would sit beside `describeRowFilter`, which has already
 * said everything there is to say about a list that fits.
 */
export function describePageRange(view: PageView<unknown>): string | null {
  if (view.pages <= 1) return null;
  return `Showing ${view.from.toLocaleString("en-US")}–${view.to.toLocaleString("en-US")} of ${view.total.toLocaleString("en-US")}`;
}

/** The label for one size in the picker. `ALL_ROWS` is spelled out rather than shown as 0. */
export function perPageLabel(perPage: number): string {
  return perPage === ALL_ROWS ? "All" : String(perPage);
}
