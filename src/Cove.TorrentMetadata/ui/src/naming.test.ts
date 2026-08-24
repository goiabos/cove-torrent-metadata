import { describe, expect, it } from "vitest";
import { TAG_STYLES, tagStyleLabel } from "./naming";

describe("tagStyleLabel", () => {
  it("names every style the panel offers", () => {
    for (const style of TAG_STYLES) expect(tagStyleLabel(style.value)).toBe(style.label);
  });

  it("falls back to the raw value for a style it does not know", () => {
    // The style is whatever the server last stored, and the review dialog puts this straight into a
    // sentence. A bundle that is behind the host must read as an unfamiliar name, not as
    // "New tags are named ." — which reads as a broken dialog rather than a stale bundle.
    expect(tagStyleLabel("sentence-case")).toBe("sentence-case");
  });

  it("does not treat an empty style as a known one", () => {
    expect(tagStyleLabel("")).toBe("");
  });
});

describe("TAG_STYLES", () => {
  it("offers each wire value once", () => {
    const values = TAG_STYLES.map((style) => style.value);
    expect(new Set(values).size).toBe(values.length);
  });

  it("gives every style an example, since the label alone does not show the spelling", () => {
    for (const style of TAG_STYLES) expect(style.example).not.toBe("");
  });
});
