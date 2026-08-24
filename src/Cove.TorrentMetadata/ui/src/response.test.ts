import { describe, expect, it } from "vitest";
import { readApiResponse } from "./response";

const jsonResponse = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

const htmlResponse = (status = 200) =>
  // The SPA fallback: an unmapped /api/* path returns index.html with HTTP 200, not a 404.
  new Response("<!DOCTYPE html><html><body>Cove</body></html>", {
    status,
    headers: { "content-type": "text/html; charset=utf-8" },
  });

const plainTextErrorResponse = (status = 502) =>
  new Response("Bad Gateway", { status, headers: { "content-type": "text/plain" } });

const emptyResponse = (status: number) => new Response(null, { status });

describe("readApiResponse", () => {
  it("returns the payload on a real JSON success", async () => {
    await expect(readApiResponse(jsonResponse({ tags: ["a"] }), "Request failed (200).")).resolves.toEqual({
      tags: ["a"],
    });
  });

  it("throws the server's own message on a real JSON error", async () => {
    await expect(
      readApiResponse(jsonResponse({ error: "Video not found." }, 404), "Request failed (404)."),
    ).rejects.toThrow("Video not found.");
  });

  it("falls back to the generic message when a JSON error body carries no error field", async () => {
    await expect(readApiResponse(jsonResponse({}, 500), "Request failed (500).")).rejects.toThrow(
      "Request failed (500).",
    );
  });

  it("does not throw the raw JSON.parse error on the SPA fallback's HTML 200 — the whole point of the fix", async () => {
    const failure = readApiResponse(htmlResponse(200), "Request failed (200).");
    await expect(failure).rejects.toThrow("Request failed (200).");
    // Pinning the negative as well as the positive: this is the exact string the bug threw instead.
    await expect(failure.catch((error: Error) => error.message)).resolves.not.toMatch(/Unexpected token/);
  });

  it("treats an HTML body on a non-200 status the same way — ok alone never distinguishes it", async () => {
    await expect(readApiResponse(htmlResponse(404), "Request failed (404).")).rejects.toThrow(
      "Request failed (404).",
    );
  });

  it("falls back to the generic message on a non-JSON error body from something other than the SPA", async () => {
    await expect(readApiResponse(plainTextErrorResponse(502), "Request failed (502).")).rejects.toThrow(
      "Request failed (502).",
    );
  });

  it("falls back to the generic message when the body claims JSON but is not parseable", async () => {
    const malformed = new Response("{not json", {
      status: 200,
      headers: { "content-type": "application/json" },
    });
    await expect(readApiResponse(malformed, "Request failed (200).")).rejects.toThrow("Request failed (200).");
  });

  it("returns null for an empty successful body", async () => {
    await expect(readApiResponse(emptyResponse(200), "Request failed (200).")).resolves.toBeNull();
  });

  it("throws the generic message for an empty error body", async () => {
    await expect(readApiResponse(emptyResponse(500), "Request failed (500).")).rejects.toThrow(
      "Request failed (500).",
    );
  });
});
