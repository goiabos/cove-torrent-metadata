import React from "@cove/runtime/react";
import { coverUrl } from "./api";
import { coverBlobs } from "./coverBlobs";
import { coverLine, parseRetryAfter, planRetry } from "./coverQueue";

const { useEffect, useRef, useState } = React;

/**
 * Every torrent-side cover in this extension, and the only thing that fetches one.
 *
 * It goes through `GET …/cover?url=` like everything else — same origin, authenticated by the host's
 * cookie — so the allowlist, the User-Agent, the pacing and the caches all still apply. What
 * changed is *how* the browser asks: `fetch` instead of an `<img src>`, which buys two things an
 * `<img>` cannot give:
 *
 * - **A refusal can be read.** `Retry-After` says when a token will exist; the old client guessed,
 *   and guessed shorter, which is why its two retries both landed while the bucket was still empty.
 * - **"Queued" and "broken" become different states.** Both arrived as `onerror` before, so the frame
 *   could not tell a reviewer that a cover was still coming.
 *
 * It buys exactly one thing it might look like it buys and does not: quiet. The browser logs every
 * failed request whatever the code does with the response, so the console goes quiet only because
 * `coverLine` stops the page sending a request while the server's one slot for that host is busy.
 *
 * It fetches only once the frame is near the viewport, because `loading="lazy"` went away with the
 * `<img>` and a 700-row page would otherwise start 700 requests — and only if `coverBlobs` does not
 * already hold the cover, or acquire it while this frame is still queued. That second case is the
 * common one it looks like an edge of: the dialog jumps the line for a cover whose row is also
 * waiting for it, and the row should show it the moment it lands rather than when its own turn
 * arrives. The store owns the object URL; this component never revokes one, because the same cover is
 * routinely on screen twice.
 */

type CoverState =
  | { kind: "idle" }
  | { kind: "loading" }
  | { kind: "loaded"; href: string }
  | { kind: "failed"; reason: string };

/**
 * One observer for the page rather than one per frame.
 *
 * A batch page renders every matched row, which is hundreds of frames on a real folder; an observer
 * each is a cost paid for nothing when a single shared one answers the same question.
 */
let viewport: IntersectionObserver | null = null;
const watching = new Map<Element, () => void>();

function whenNearViewport(element: Element, enter: () => void): () => void {
  viewport ??= new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        const waiting = watching.get(entry.target);
        if (waiting === undefined) continue;

        // One shot: a frame that has started loading has no further use for the observer.
        watching.delete(entry.target);
        viewport?.unobserve(entry.target);
        waiting();
      }
    },
    // Enough margin that a cover is usually there by the time the row is scrolled to, without
    // fetching the whole table on first paint.
    { rootMargin: "300px" },
  );

  watching.set(element, enter);
  viewport.observe(element);

  return () => {
    watching.delete(element);
    viewport?.unobserve(element);
  };
}

/** What this frame starts as: already-held covers skip every other state. */
function held(url: string): CoverState {
  const href = coverBlobs.get(url);
  return href === undefined ? { kind: "idle" } : { kind: "loaded", href };
}

export function CoverImg({
  url,
  className = "",
  title,
  failedLabel,
  urgent,
}: {
  /** The cover URL out of the torrent. Never used as an `src` — it is proxied. */
  url: string;
  className?: string;
  title?: string;
  /** Shown in the failed frame when there is room for words. */
  failedLabel?: boolean;
  /**
   * A cover the reviewer is looking at now rather than one filling in behind them.
   *
   * The dialog's covers set it. The line is page-wide, so without it a review opened on a row whose
   * cover has not loaded yet waits behind every background thumbnail on the table — fair, and
   * indistinguishable from broken.
   */
  urgent?: boolean;
}) {
  const [state, setState] = useState<CoverState>(() => held(url));
  const frame = useRef<HTMLDivElement | null>(null);

  // Reset during render rather than in an effect, because the ref lives on the *skeleton* — once a
  // cover has loaded the observed element is an `<img>`, and a url arriving on a mounted frame would
  // find nothing to watch and never load. Adjusting here re-renders before the commit, so the effect
  // below always finds the frame it is about to observe.
  const [loadingFor, setLoadingFor] = useState(url);
  if (loadingFor !== url) {
    setLoadingFor(url);
    setState(held(url));
  }

  useEffect(() => {
    // Already held: nothing to observe, nothing to queue for, nothing to fetch.
    if (coverBlobs.get(url) !== undefined) return;

    const element = frame.current;
    if (element === null) return;

    let done = false;
    const abandoned = () => done;
    const controller = new AbortController();

    // Another frame may fetch this same cover first — the dialog's does exactly that when it jumps
    // the queue. Take it and stop: `done` makes the queued turn resolve without sending, so the line
    // is not spent rediscovering something the page is already holding.
    const unwatchStore = coverBlobs.watch(url, (href) => {
      if (done) return;
      done = true;
      controller.abort();
      setState({ kind: "loaded", href });
    });

    const load = async () => {
      setState({ kind: "loading" });
      let firstAnswerAt: number | null = null;

      for (;;) {
        if (done) return;

        // Through the line, always: the server allows one cover in flight per host and holds that
        // slot for the whole upstream fetch, so anything sent underneath a four-second GIF waits two
        // seconds and is refused. Asking one at a time is what makes a refusal rare rather than
        // something to manage.
        const answer = await coverLine.send(abandoned, async () => {
          // `credentials` explicitly, because a cover is authenticated by the host's own cookie —
          // the same way the `<img>` was, and the reason no token is threaded through here.
          const response = await fetch(coverUrl(url), {
            credentials: "same-origin",
            signal: controller.signal,
          });

          return {
            status: response.status,
            retryAfterSeconds: parseRetryAfter(response.headers.get("Retry-After")),
            response,
          };
        }, { urgent }).catch(() => null);

        // Null is either the frame going away before its turn, or a fetch that never answered —
        // aborted, or the server unreachable.
        if (answer === null) {
          if (!done) setState({ kind: "failed", reason: "cover unavailable" });
          return;
        }

        if (done) return;

        if (answer.response.ok) {
          const blob = await answer.response.blob();
          if (done) return;

          // Unwatched first, so our own arrival does not come back to us as someone else's.
          unwatchStore();
          setState({ kind: "loaded", href: coverBlobs.put(url, blob) });
          return;
        }

        // Timed from the first *answer*, not from the frame appearing: on a cold page a cover can
        // legitimately spend a minute waiting its turn, and that is the line working rather than
        // this cover failing.
        firstAnswerAt ??= Date.now();

        const plan = planRetry({ status: answer.status, elapsedMs: Date.now() - firstAnswerAt });
        if (!plan.retry) {
          setState({ kind: "failed", reason: plan.reason ?? "cover unavailable" });
          return;
        }

        // Straight back into the line. It holds the next turn until whatever `Retry-After` asked for
        // has passed, so there is nothing to sleep for here.
      }
    };

    const unwatch = whenNearViewport(element, () => void load());

    return () => {
      done = true;
      unwatchStore();
      unwatch();
      controller.abort();
    };
  }, [url, urgent]);

  if (state.kind === "loaded") {
    return <img className={className} src={state.href} alt="" title={title} />;
  }

  if (state.kind === "failed") {
    return (
      <div className={`${className} tm-cover-failed`} title={state.reason}>
        {failedLabel ? state.reason : null}
      </div>
    );
  }

  // Idle and loading look the same on purpose: from the reviewer's side "waiting its turn" and
  // "downloading" are one state — the cover is coming — and a frame that changed appearance between
  // them would be reporting our queue rather than their cover.
  return <div ref={frame} className={`${className} tm-skeleton`} title="fetching cover…" />;
}
