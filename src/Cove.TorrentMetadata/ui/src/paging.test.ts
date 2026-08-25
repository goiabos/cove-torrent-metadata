import { describe, expect, it } from "vitest";
import {
  ALL_ROWS,
  clampPage,
  describePageRange,
  pageCount,
  pageOf,
  perPageLabel,
  takePage,
} from "./paging";

const rows = (count: number) => Array.from({ length: count }, (_, index) => index + 1);

describe("pageCount", () => {
  it("is one page for an empty list, so there is always a page 1", () => {
    expect(pageCount(0, 60)).toBe(1);
  });

  it("counts a partial last page", () => {
    expect(pageCount(121, 60)).toBe(3);
  });

  it("is one page at All, however long the list is", () => {
    expect(pageCount(3199, ALL_ROWS)).toBe(1);
  });
});

describe("clampPage", () => {
  it("brings a page back when the list narrowed under it", () => {
    // Typing into the row filter is exactly this: page 9 of a 715-row list, then two matches left.
    expect(clampPage(9, 2, 60)).toBe(1);
  });

  it("refuses a page below the first", () => {
    expect(clampPage(0, 200, 60)).toBe(1);
    expect(clampPage(-4, 200, 60)).toBe(1);
  });

  it("leaves a page that exists alone", () => {
    expect(clampPage(3, 200, 60)).toBe(3);
  });
});

describe("pageOf", () => {
  it("finds the page holding a row, so a walk can bring the page with it", () => {
    // The review walk steps over the whole filtered list, not over the page — stepping past row 60
    // has to turn the page rather than step onto a row nothing is showing.
    expect(pageOf(59, 60)).toBe(1);
    expect(pageOf(60, 60)).toBe(2);
    expect(pageOf(719, 60)).toBe(12);
  });

  it("is page 1 at All", () => {
    expect(pageOf(900, ALL_ROWS)).toBe(1);
  });
});

describe("takePage", () => {
  it("slices the page asked for and says which rows those are", () => {
    const view = takePage(rows(715), 3, 60);

    expect(view.rows[0]).toBe(121);
    expect(view.rows).toHaveLength(60);
    expect(view.page).toBe(3);
    expect(view.pages).toBe(12);
    expect(view.from).toBe(121);
    expect(view.to).toBe(180);
    expect(view.total).toBe(715);
  });

  it("shortens the last page rather than padding it", () => {
    const view = takePage(rows(715), 12, 60);

    expect(view.rows).toHaveLength(55);
    expect(view.from).toBe(661);
    expect(view.to).toBe(715);
  });

  it("returns the page it actually showed, not the one it was asked for", () => {
    // The caller renders `view.rows` and labels them from the same call, so a clamp that was not
    // reported back would draw page 1 under a "page 9" label — the label and the list disagreeing,
    // which is the failure the module exists to prevent.
    const view = takePage(rows(2), 9, 60);

    expect(view.page).toBe(1);
    expect(view.rows).toEqual([1, 2]);
    expect(view.to).toBe(2);
  });

  it("hands back everything at All", () => {
    const view = takePage(rows(3199), 1, ALL_ROWS);

    expect(view.rows).toHaveLength(3199);
    expect(view.pages).toBe(1);
    expect(view.from).toBe(1);
    expect(view.to).toBe(3199);
  });

  it("reports an empty list as no rows at all rather than as row 1", () => {
    const view = takePage([], 1, 60);

    expect(view.rows).toEqual([]);
    expect(view.from).toBe(0);
    expect(view.to).toBe(0);
    expect(view.total).toBe(0);
  });

  it("copies rather than aliasing the caller's list at All", () => {
    const source = rows(3);
    const view = takePage(source, 1, ALL_ROWS);
    view.rows.push(99);

    expect(source).toHaveLength(3);
  });
});

describe("describePageRange", () => {
  it("says nothing when the whole list is on screen", () => {
    // `describeRowFilter` has already said what there is to say about a list that fits, and a range
    // that is always there is a line nobody reads.
    expect(describePageRange(takePage(rows(37), 1, 60))).toBeNull();
    expect(describePageRange(takePage(rows(3199), 1, ALL_ROWS))).toBeNull();
  });

  it("names the rows on screen out of the whole filtered list", () => {
    expect(describePageRange(takePage(rows(3199), 2, 60))).toBe("Showing 61–120 of 3,199");
  });
});

describe("perPageLabel", () => {
  it("spells All out rather than showing a zero", () => {
    expect(perPageLabel(ALL_ROWS)).toBe("All");
    expect(perPageLabel(60)).toBe("60");
  });
});
