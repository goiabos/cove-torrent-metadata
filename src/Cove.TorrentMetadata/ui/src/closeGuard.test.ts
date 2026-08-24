import { describe, expect, it } from "vitest";
import { CLOSE_PENDING_LABEL, resolveCloseRequest } from "./closeGuard";

describe("resolveCloseRequest", () => {
  it("closes at once when nothing is in flight", () => {
    expect(resolveCloseRequest(false)).toBe("close");
  });

  it("defers while an apply is in flight, whichever door asked", () => {
    expect(resolveCloseRequest(true)).toBe("defer");
  });
});

describe("CLOSE_PENDING_LABEL", () => {
  it("is what every deferred door's affordance reads while waiting", () => {
    expect(CLOSE_PENDING_LABEL).toBe("Closing…");
  });
});
