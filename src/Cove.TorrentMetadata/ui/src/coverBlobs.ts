/**
 * Covers this page has already fetched, kept as object URLs so it fetches each one once.
 *
 * Every cover in the extension is rendered by a `CoverImg`, and the same cover is routinely rendered
 * by more than one of them: a row's thumbnail and the review dialog's strip are the same image, and
 * so are the strip and the full comparison behind it. Without this each of those is a separate fetch
 * that has to queue in `coverLine` behind every other cover on the page — so opening a dialog on a
 * row whose thumbnail is already on screen showed a skeleton and waited.
 *
 * The browser's own HTTP cache holds the bytes (a served cover carries `Cache-Control: private,
 * max-age=86400`), so the re-fetch was cheap — but it was not *free*, because it still had to take a
 * turn in a line that exists to protect a third party. The one request the page can most obviously
 * avoid is the one for something it is already holding.
 *
 * **The store owns the object URL, so components never revoke.** A blob URL revoked by one component
 * unmounting would blank the same cover in another that is still showing it; ownership has to sit
 * where the sharing does.
 *
 * Bounded by bytes rather than by count, because covers here range from a few KB to multi-megabyte
 * animated GIFs and a count-based cap says nothing useful about either. The budget mirrors the
 * server's `CoverPreviewCache` for the same reason it was chosen there.
 *
 * The URL factory is injected so this stays testable without a DOM: it holds no React and no
 * `@cove/runtime/*`, like every other module the suite can reach.
 */

/** 64 MB, matching the server's preview cache. Ours to tune — it was never promised to anyone. */
export const COVER_BLOB_BUDGET_BYTES = 64 * 1024 * 1024;

export interface CoverBlobStore {
  /** The object URL for a cover already held, or undefined. Marks it as most recently wanted. */
  get(url: string): string | undefined;
  /** Holds a cover and returns its object URL, evicting the least recently wanted to stay in budget. */
  put(url: string, blob: { size: number }): string;
  /**
   * Called once if this cover arrives while the caller is still waiting for it. Returns an
   * unsubscribe.
   *
   * A frame checks the store when it mounts and, finding nothing, joins the line. Another frame can
   * then fetch the same cover first — which is exactly what the review dialog does when it jumps the
   * queue — and without this the first frame sits on a skeleton until its own turn comes round to
   * rediscover something the page is already holding.
   */
  watch(url: string, onHeld: (href: string) => void): () => void;
  /** Bytes currently held, for tests and for anyone wondering where the memory went. */
  size(): number;
}

export function createCoverBlobStore(options: {
  budgetBytes?: number;
  create: (blob: { size: number }) => string;
  revoke: (href: string) => void;
}): CoverBlobStore {
  const { budgetBytes = COVER_BLOB_BUDGET_BYTES, create, revoke } = options;
  // Insertion order is the eviction order, and `get` re-inserts, which makes a Map an LRU without a
  // second structure.
  const held = new Map<string, { href: string; bytes: number }>();
  const waiting = new Map<string, Set<(href: string) => void>>();
  let bytes = 0;

  return {
    get(url: string): string | undefined {
      const entry = held.get(url);
      if (entry === undefined) return undefined;

      held.delete(url);
      held.set(url, entry);
      return entry.href;
    },

    watch(url: string, onHeld: (href: string) => void): () => void {
      const listeners = waiting.get(url) ?? new Set();
      listeners.add(onHeld);
      waiting.set(url, listeners);

      return () => {
        listeners.delete(onHeld);
        if (listeners.size === 0) waiting.delete(url);
      };
    },

    put(url: string, blob: { size: number }): string {
      const existing = held.get(url);
      if (existing !== undefined) {
        // Two frames raced for the same cover. Keep the one already handed out — something may be
        // rendering it — and let the loser's blob go. Nobody is told: anything watching was told when
        // it first arrived, and anything mounting since found it with `get`.
        held.delete(url);
        held.set(url, existing);
        return existing.href;
      }

      const href = create(blob);
      held.set(url, { href, bytes: blob.size });
      bytes += blob.size;

      // Oldest first, and never the entry just added — a budget smaller than a single cover would
      // otherwise evict it before it could be rendered.
      for (const [key, entry] of held) {
        if (bytes <= budgetBytes || key === url) break;
        held.delete(key);
        bytes -= entry.bytes;
        revoke(entry.href);
      }

      const listeners = waiting.get(url);
      if (listeners !== undefined) {
        waiting.delete(url);
        // Copied before calling: a listener that unsubscribes itself in response — which is what a
        // frame taking the cover does — must not mutate the set being iterated.
        for (const listener of [...listeners]) listener(href);
      }

      return href;
    },

    size: () => bytes,
  };
}

/** The page's store. One per bundle, which is one per page. */
export const coverBlobs = createCoverBlobStore({
  create: (blob) => URL.createObjectURL(blob as Blob),
  revoke: (href) => URL.revokeObjectURL(href),
});
