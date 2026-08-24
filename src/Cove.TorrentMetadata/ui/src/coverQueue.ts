/**
 * How the browser asks for covers, so that it asks the way the server can answer.
 *
 * The proxy allows **one cover request in flight per image host** (`CoverRateLimiter`
 * `MaxConcurrentPerHost = 1`), holds that slot for the whole upstream fetch, and refuses with a 429
 * after two seconds of waiting for it. Covers here are frequently multi-megabyte animated GIFs, so a
 * single fetch can occupy that slot for several seconds.
 *
 * That is the fact everything below follows from, and the one two earlier attempts missed:
 *
 * - Firing every cover at once means one is served and the rest wait 2s and are refused.
 * - Pacing them on a **timer** is no better once a fetch outlives the interval — a three-second GIF
 *   with a one-second cadence still has two requests piled up behind it, both refused.
 * - `fetch` instead of `<img>` makes a refusal *readable*, which is worth having, but it does not
 *   make it quiet: the browser logs every failed request whatever the code does with it.
 *
 * So the client mirrors the server's own rule: **one cover request at a time, and the next one starts
 * only when the previous has finished.** A refusal then stops being something to manage and starts
 * being something that mostly does not happen — the slot is free by construction whenever we ask.
 *
 * A serial line is not slow where it matters. `CoverProxyService` answers a cover it already holds —
 * blob cache, negative cache, preview cache — before the limiter is ever consulted, so a revisited
 * page runs the line at a few milliseconds a row. A cold page is bounded by the image host, which is
 * the thing being paced, and no arrangement of requests makes it faster.
 *
 * Nothing here imports React, `@cove/runtime/*`, or `./api`. It decides and it sequences; the
 * component fetches.
 */

/** How long a cover keeps being retried after its first answer before the frame gives up. */
export const RETRY_BUDGET_MS = 90_000;

/** The floor on a wait the server asked for without naming a number. */
export const MIN_RETRY_GAP_MS = 1_050;

/** The part of a cover response this module reasons about. */
export interface CoverAnswer {
  status: number;
  /** `Retry-After` in seconds, when the response carried one. */
  retryAfterSeconds: number | null;
}

export interface RetryPlan {
  retry: boolean;
  /** Why it stopped, for the failed frame. Null while it is still worth trying. */
  reason: string | null;
}

/**
 * Whether a cover that did not arrive is worth asking for again.
 *
 * Only a refusal is. Everything else is the server having decided something it will keep deciding: a
 * host that is not on the allowlist stays off it until the user says otherwise, and a cover that
 * could not be fetched is negative-cached, so asking again buys a round trip to hear the same thing.
 *
 * *When* to ask again is not decided here — the line holds the next turn until the server's own
 * `Retry-After` has passed. This decides only whether, and when to stop.
 */
export function planRetry(input: {
  status: number;
  /** How long since this cover's first answer, in milliseconds. */
  elapsedMs: number;
  budgetMs?: number;
}): RetryPlan {
  const { status, elapsedMs, budgetMs = RETRY_BUDGET_MS } = input;

  if (status === 403) {
    return { retry: false, reason: "not fetched — this host is not on your list" };
  }

  if (status !== 429) {
    return { retry: false, reason: "cover unavailable" };
  }

  if (elapsedMs >= budgetMs) {
    return { retry: false, reason: "gave up waiting for this cover" };
  }

  return { retry: true, reason: null };
}

/**
 * Reads `Retry-After` as the seconds our own endpoint always sends, or null for anything else.
 *
 * The blank check is not defensive tidiness: `Number("")` is `0`, not `NaN`, so an empty header would
 * otherwise read as "come back immediately" — the one answer the limiter never gives.
 */
export function parseRetryAfter(header: string | null): number | null {
  if (header === null) return null;

  const text = header.trim();
  if (text === "") return null;

  const seconds = Number(text);
  return Number.isFinite(seconds) && seconds >= 0 ? seconds : null;
}

export interface CoverLine {
  /**
   * Runs `request` when the line reaches it, holding the line until it settles.
   *
   * Resolves to the answer, or to null when the caller was abandoned before its turn came — an
   * abandoned caller costs the line nothing, so filtering a table of rows away drains it rather than
   * leaving the next real request behind hundreds of dead turns.
   *
   * `urgent` puts it ahead of everything still waiting. It is for a cover the user is looking at
   * *now* — the review dialog's — because a page-wide line is otherwise fair in the one way that
   * reads as broken: open a dialog on a row whose cover has not loaded yet and it waits behind every
   * background thumbnail on the table. It cannot interrupt a request already in flight, and should
   * not: that one is holding the server's only slot for the host, and cancelling it would waste the
   * fetch rather than speed anything up.
   */
  send<T extends CoverAnswer>(
    abandoned: () => boolean,
    request: () => Promise<T>,
    options?: { urgent?: boolean },
  ): Promise<T | null>;
}

const defaultSleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/**
 * One cover request at a time, page-wide, with the foreground allowed to go first.
 *
 * Serial because the server is: a second request while one is in flight does not go faster, it waits
 * two seconds and is then refused. The line holds for the *duration* of each request rather than for
 * a fixed interval, because the duration is the thing that varies — and assuming otherwise is what
 * kept the console full through two attempts at this.
 *
 * Two lanes rather than one, because "serial" and "first come first served" turned out to be
 * separable: what the server needs is that only one request is out at a time, and it has no opinion
 * about which. So a waiting queue rather than a promise chain, and the urgent lane is drained first.
 *
 * The only deliberate delay is one the server asked for. A `Retry-After` sets the earliest moment the
 * next request may start, so the page waits it out once rather than each cover discovering it alone.
 */
export function createCoverLine(now: () => number = Date.now, sleep = defaultSleep): CoverLine {
  const urgent: Array<() => Promise<void>> = [];
  const waiting: Array<() => Promise<void>> = [];
  let inFlight = false;
  let notBefore = 0;

  const pump = async (): Promise<void> => {
    if (inFlight) return;

    const next = urgent.shift() ?? waiting.shift();
    if (next === undefined) return;

    inFlight = true;
    try {
      await next();
    } finally {
      inFlight = false;
    }

    void pump();
  };

  return {
    send<T extends CoverAnswer>(
      abandoned: () => boolean,
      request: () => Promise<T>,
      options: { urgent?: boolean } = {},
    ): Promise<T | null> {
      return new Promise<T | null>((resolve, reject) => {
        const turn = async () => {
          if (abandoned()) {
            resolve(null);
            return;
          }

          const wait = notBefore - now();
          if (wait > 0) await sleep(wait);
          if (abandoned()) {
            resolve(null);
            return;
          }

          try {
            const answer = await request();

            // Told once, obeyed by everyone behind us. Each cover rediscovering the same refusal is
            // how one saturated host turned into a page of them.
            notBefore =
              answer.status === 429
                ? now() + Math.max((answer.retryAfterSeconds ?? 0) * 1000, MIN_RETRY_GAP_MS)
                : 0;

            resolve(answer);
          } catch (failure) {
            // Rejected to this caller only. The line itself must survive it, or one thrown request
            // stalls every cover behind it for the life of the page.
            reject(failure);
          }
        };

        (options.urgent === true ? urgent : waiting).push(turn);
        void pump();
      });
    },
  };
}

/** The page's line. Shared deliberately: it stands in for one image host's single slot. */
export const coverLine = createCoverLine();
