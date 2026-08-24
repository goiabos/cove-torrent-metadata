/**
 * Fetching each cover once per page, and not holding the whole library in memory to do it.
 */

import { describe, expect, it, vi } from "vitest";
import { createCoverBlobStore } from "./coverBlobs";

const blob = (size: number) => ({ size });

const storeOf = (budgetBytes?: number) => {
  const revoked: string[] = [];
  let issued = 0;
  const store = createCoverBlobStore({
    budgetBytes,
    create: () => `blob:${++issued}`,
    revoke: (href) => revoked.push(href),
  });
  return { store, revoked };
};

describe("createCoverBlobStore", () => {
  it("hands the same object URL back for a cover it already holds", () => {
    const { store } = storeOf();
    const href = store.put("https://images.example/a.gif", blob(10));

    expect(store.get("https://images.example/a.gif")).toBe(href);
  });

  it("knows nothing about a cover it has not been given", () => {
    const { store } = storeOf();

    expect(store.get("https://images.example/missing.gif")).toBeUndefined();
  });

  it("keeps the URL already handed out when two frames race for one cover", () => {
    const { store, revoked } = storeOf();
    const first = store.put("https://images.example/a.gif", blob(10));
    const second = store.put("https://images.example/a.gif", blob(10));

    // Revoking the first would blank whatever is already rendering it.
    expect(second).toBe(first);
    expect(revoked).toEqual([]);
  });

  it("evicts the least recently wanted once it is over budget, and revokes what it drops", () => {
    const { store, revoked } = storeOf(100);
    const oldest = store.put("a", blob(60));
    store.put("b", blob(30));
    store.put("c", blob(30));

    expect(revoked).toEqual([oldest]);
    expect(store.get("a")).toBeUndefined();
    expect(store.get("b")).toBeDefined();
    expect(store.size()).toBe(60);
  });

  it("counts a read as recent use, so the cover being looked at is not the one evicted", () => {
    const { store } = storeOf(100);
    store.put("a", blob(40));
    store.put("b", blob(40));

    store.get("a");
    store.put("c", blob(40));

    expect(store.get("a")).toBeDefined();
    expect(store.get("b")).toBeUndefined();
  });

  it("keeps a cover larger than the whole budget rather than evicting what it just added", () => {
    const { store, revoked } = storeOf(10);
    const href = store.put("huge", blob(500));

    expect(store.get("huge")).toBe(href);
    expect(revoked).toEqual([]);
  });

  it("tells a frame still waiting for a cover the moment another frame fetches it", () => {
    const { store } = storeOf();
    const told = vi.fn();
    store.watch("https://images.example/a.gif", told);

    const href = store.put("https://images.example/a.gif", blob(10));

    expect(told).toHaveBeenCalledWith(href);
  });

  it("tells every frame waiting for that cover, and nobody waiting for another", () => {
    const { store } = storeOf();
    const row = vi.fn();
    const dialog = vi.fn();
    const unrelated = vi.fn();
    store.watch("a", row);
    store.watch("a", dialog);
    store.watch("b", unrelated);

    store.put("a", blob(10));

    expect(row).toHaveBeenCalledTimes(1);
    expect(dialog).toHaveBeenCalledTimes(1);
    expect(unrelated).not.toHaveBeenCalled();
  });

  it("says nothing to a frame that has unsubscribed", () => {
    const { store } = storeOf();
    const told = vi.fn();
    const stop = store.watch("a", told);

    stop();
    store.put("a", blob(10));

    expect(told).not.toHaveBeenCalled();
  });

  it("survives a listener that unsubscribes itself while being told", () => {
    const { store } = storeOf();
    const second = vi.fn();
    let stopFirst = () => undefined as void;
    stopFirst = store.watch("a", () => stopFirst());
    store.watch("a", second);

    expect(() => store.put("a", blob(10))).not.toThrow();
    expect(second).toHaveBeenCalled();
  });

  it("tells a frame once, not again when a later frame puts the same cover", () => {
    const { store } = storeOf();
    const told = vi.fn();
    store.watch("a", told);

    store.put("a", blob(10));
    store.put("a", blob(10));

    expect(told).toHaveBeenCalledTimes(1);
  });

  it("revokes exactly once per evicted cover", () => {
    const revoke = vi.fn();
    const store = createCoverBlobStore({ budgetBytes: 50, create: () => "blob:x", revoke });

    store.put("a", blob(40));
    store.put("b", blob(40));
    store.put("c", blob(40));

    expect(revoke).toHaveBeenCalledTimes(2);
  });
});
