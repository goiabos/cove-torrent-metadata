import { describe, expect, it } from "vitest";
import { firstEntityId } from "./payload";

describe("firstEntityId", () => {
  it("prefers the detail page's id to the multi-select one", () => {
    // The host sends the same id in both for a toolbar action, so this only shows in a payload the
    // host does not currently produce — but the order is what makes one handler serve either
    // surface, and it should fail loudly rather than quietly flip.
    expect(firstEntityId({ entityIds: [42], selectedIds: [7] })).toBe(42);
  });

  it("falls back to the multi-select field when the detail-page one is empty", () => {
    expect(firstEntityId({ entityIds: [], selectedIds: [9] })).toBe(9);
  });

  it("accepts an id that arrives as a string, since the payload is not ours to type", () => {
    expect(firstEntityId({ entityIds: ["9"] })).toBe(9);
  });

  it("rejects the empty values that coerce to zero", () => {
    // The bug: `Number` maps all three to 0, and 0 is finite, so each used to become video id 0.
    expect(firstEntityId({ entityIds: [""] })).toBeNull();
    expect(firstEntityId({ entityIds: [null] })).toBeNull();
    expect(firstEntityId({ entityIds: [[]] })).toBeNull();
  });

  it("rejects zero itself", () => {
    expect(firstEntityId({ entityIds: [0] })).toBeNull();
  });

  it("rejects ids no row can have", () => {
    expect(firstEntityId({ entityIds: [-1] })).toBeNull();
    expect(firstEntityId({ entityIds: [1.5] })).toBeNull();
    expect(firstEntityId({ entityIds: [Number.NaN] })).toBeNull();
    expect(firstEntityId({ entityIds: [Number.POSITIVE_INFINITY] })).toBeNull();
  });

  it("rejects values that would coerce into a plausible id rather than an obvious one", () => {
    // `Number(true)` is 1 and `Number([7])` is 7. Coercing first would open the dialog on a real
    // video the user never picked, which is worse than any error.
    expect(firstEntityId({ entityIds: [true] })).toBeNull();
    expect(firstEntityId({ entityIds: [[7]] })).toBeNull();
    expect(firstEntityId({ entityIds: [{ id: 7 }] })).toBeNull();
  });

  it("skips an unusable entry and keeps looking", () => {
    expect(firstEntityId({ entityIds: [null, 0, 12] })).toBe(12);
    expect(firstEntityId({ entityIds: [""], selectedIds: [12] })).toBe(12);
  });

  it("has no id when the payload is absent or carries nothing", () => {
    expect(firstEntityId(undefined)).toBeNull();
    expect(firstEntityId({})).toBeNull();
    expect(firstEntityId({ entityIds: [], selectedIds: [] })).toBeNull();
  });
});
