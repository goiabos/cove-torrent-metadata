/**
 * The client's half of the cover pacing contract.
 *
 * The server's numbers are promises to a third party and are pinned on the C# side. What is pinned
 * here is that the browser asks the way the server can answer: one request at a time, the next only
 * once the last has finished, and never again for something it has been told it cannot have.
 */

import { describe, expect, it, vi } from "vitest";
import {
  MIN_RETRY_GAP_MS,
  RETRY_BUDGET_MS,
  createCoverLine,
  parseRetryAfter,
  planRetry,
  type CoverAnswer,
} from "./coverQueue";

describe("planRetry", () => {
  const plan = (over: Partial<Parameters<typeof planRetry>[0]> = {}) =>
    planRetry({ status: 429, elapsedMs: 0, ...over });

  it("retries a refusal, because a 429 is a place in a line rather than a failure", () => {
    expect(plan()).toEqual({ retry: true, reason: null });
  });

  it("stops once the cover has been trying longer than the budget", () => {
    const stopped = plan({ elapsedMs: RETRY_BUDGET_MS });

    expect(stopped.retry).toBe(false);
    expect(stopped.reason).toBe("gave up waiting for this cover");
  });

  it("does not retry a refused host — that stays refused until the allowlist changes", () => {
    const refused = plan({ status: 403 });

    expect(refused.retry).toBe(false);
    expect(refused.reason).toContain("not on your list");
  });

  it("does not retry a cover the server could not fetch — that answer is negative-cached", () => {
    expect(plan({ status: 502 }).retry).toBe(false);
    expect(plan({ status: 404 }).retry).toBe(false);
  });
});

describe("parseRetryAfter", () => {
  it("reads the seconds our own endpoint sends", () => {
    expect(parseRetryAfter("3")).toBe(3);
    expect(parseRetryAfter(" 12 ")).toBe(12);
  });

  it("answers null for a header that is absent or not a count of seconds", () => {
    expect(parseRetryAfter(null)).toBeNull();
    expect(parseRetryAfter("Wed, 21 Oct 2026 07:28:00 GMT")).toBeNull();
    expect(parseRetryAfter("")).toBeNull();
  });
});

describe("createCoverLine", () => {
  const live = () => false;
  const served: CoverAnswer = { status: 200, retryAfterSeconds: null };
  const refused = (seconds: number | null = null): CoverAnswer => ({ status: 429, retryAfterSeconds: seconds });

  /** A request that resolves when the test says so, so "still in flight" is expressible. */
  const pending = <T,>() => {
    let settle: (value: T) => void = () => undefined;
    const promise = new Promise<T>((resolve) => {
      settle = resolve;
    });
    return { promise, settle: (value: T) => settle(value) };
  };

  it("runs the first request straight away", async () => {
    const line = createCoverLine();

    await expect(line.send(live, async () => served)).resolves.toBe(served);
  });

  it("does not start the next request until the one before it has finished", async () => {
    const line = createCoverLine();
    const first = pending<CoverAnswer>();
    const started: string[] = [];

    const one = line.send(live, () => {
      started.push("one");
      return first.promise;
    });
    const two = line.send(live, async () => {
      started.push("two");
      return served;
    });

    await Promise.resolve();
    // The whole point: a cover that takes four seconds holds the slot for four seconds, and the
    // server refuses anything sent underneath it. A timer-based gap cannot express that.
    expect(started).toEqual(["one"]);

    first.settle(served);
    await one;
    await two;
    expect(started).toEqual(["one", "two"]);
  });

  it("waits out a refusal once, on behalf of everything behind it", async () => {
    vi.useFakeTimers();
    try {
      const line = createCoverLine(() => Date.now());

      await line.send(live, async () => refused(4));

      const started = vi.fn();
      const next = line.send(live, async () => {
        started();
        return served;
      });

      await vi.advanceTimersByTimeAsync(3999);
      expect(started).not.toHaveBeenCalled();

      await vi.advanceTimersByTimeAsync(1);
      await expect(next).resolves.toBe(served);
    } finally {
      vi.useRealTimers();
    }
  });

  it("keeps its own floor when the refusal named no delay", async () => {
    vi.useFakeTimers();
    try {
      const line = createCoverLine(() => Date.now());
      await line.send(live, async () => refused(null));

      const started = vi.fn();
      void line.send(live, async () => {
        started();
        return served;
      });

      await vi.advanceTimersByTimeAsync(MIN_RETRY_GAP_MS - 1);
      expect(started).not.toHaveBeenCalled();

      await vi.advanceTimersByTimeAsync(1);
      expect(started).toHaveBeenCalled();
    } finally {
      vi.useRealTimers();
    }
  });

  it("clears the wait once something is served again", async () => {
    vi.useFakeTimers();
    try {
      const line = createCoverLine(() => Date.now());
      await line.send(live, async () => refused(30));
      await vi.advanceTimersByTimeAsync(30_000);
      await line.send(live, async () => served);

      const started = vi.fn();
      void line.send(live, async () => {
        started();
        return served;
      });

      await Promise.resolve();
      await Promise.resolve();
      expect(started).toHaveBeenCalled();
    } finally {
      vi.useRealTimers();
    }
  });

  it("never sends for a caller that has gone away, and does not hold the line for it", async () => {
    const line = createCoverLine();
    const sent = vi.fn();

    await expect(line.send(() => true, async () => {
      sent();
      return served;
    })).resolves.toBeNull();
    expect(sent).not.toHaveBeenCalled();

    await expect(line.send(live, async () => served)).resolves.toBe(served);
  });

  it("lets an urgent request go ahead of everything still waiting", async () => {
    const line = createCoverLine();
    const first = pending<CoverAnswer>();
    const order: string[] = [];

    const running = line.send(live, () => {
      order.push("row-1");
      return first.promise;
    });
    const rowTwo = line.send(live, async () => {
      order.push("row-2");
      return served;
    });
    const dialog = line.send(live, async () => {
      order.push("dialog");
      return served;
    }, { urgent: true });

    first.settle(served);
    await Promise.all([running, dialog, rowTwo]);

    // The dialog's cover was asked for last and goes second: the reviewer is looking at it, and the
    // rows behind it are furniture.
    expect(order).toEqual(["row-1", "dialog", "row-2"]);
  });

  it("does not let an urgent request interrupt one already in flight", async () => {
    const line = createCoverLine();
    const first = pending<CoverAnswer>();
    const order: string[] = [];

    const running = line.send(live, () => {
      order.push("row");
      return first.promise;
    });
    void line.send(live, async () => {
      order.push("dialog");
      return served;
    }, { urgent: true });

    await Promise.resolve();
    // That request is holding the server's only slot for the host; cancelling it would waste the
    // fetch rather than free anything.
    expect(order).toEqual(["row"]);

    first.settle(served);
    await running;
  });

  it("keeps urgent requests in order among themselves", async () => {
    const line = createCoverLine();
    const first = pending<CoverAnswer>();
    const order: string[] = [];

    const running = line.send(live, () => first.promise);
    const strip = line.send(live, async () => {
      order.push("strip");
      return served;
    }, { urgent: true });
    const comparison = line.send(live, async () => {
      order.push("comparison");
      return served;
    }, { urgent: true });

    first.settle(served);
    await Promise.all([running, strip, comparison]);

    expect(order).toEqual(["strip", "comparison"]);
  });

  it("keeps the line moving when one request throws", async () => {
    const line = createCoverLine();

    await expect(
      line.send(live, async () => {
        throw new Error("the network went away");
      }),
    ).rejects.toThrow("network went away");

    await expect(line.send(live, async () => served)).resolves.toBe(served);
  });
});
