import React from "@cove/runtime/react";
import { ReviewBody, type ReviewBodyHandle, type ReviewBodyProps } from "./ReviewBody";

const { useEffect, useRef } = React;

export type { ReviewPager } from "./ReviewBody";

/**
 * The review as a modal: a backdrop, a box, and the two ways out of it.
 *
 * This is the frame the **entity action** uses — opened from a video's own page, where there is no
 * list to sit beside and the review genuinely is an overlay on someone else's screen. The batch page
 * uses `ReviewPane` instead. Both render the same `ReviewBody`; only the frame differs.
 *
 * It keeps its name and its export because `main.tsx` mounts it into a detached root and nothing
 * about that entry point changed.
 *
 * **No state, by design.** Escape and the click-outside are dismissals, which is what a shell is for;
 * everything else — the selection, the apply, the cover, the counts — belongs to the review and stays
 * in the body. The dimming while the queue steps is the body's too, on `.tm-body` rather than on this
 * box, so this frame does not have to read the pager to draw itself.
 *
 * Neither dismissal calls `onClose` directly any more. Both go through `bodyRef.current.requestClose`
 * instead, which is `ReviewBody`'s own Close-button handler reached over the imperative handle — the
 * body is the only thing that knows whether an apply is in flight, so it is the only thing that can
 * decide whether a dismissal lands now or waits (this pair of doors is the same race
 * through a third and fourth). `bodyRef` is a plain ref, not `useState`: it holds a stable pointer to
 * a method on the child, not a value this component reacts to, so the shell still owns no state.
 */
export function MatchDialog(props: ReviewBodyProps) {
  const bodyRef = useRef<ReviewBodyHandle>(null);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") bodyRef.current?.requestClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, []);

  return (
    <div
      className="tm-backdrop"
      onClick={(event) => event.target === event.currentTarget && bodyRef.current?.requestClose()}
    >
      <div className="tm-modal" role="dialog" aria-modal="true">
        <ReviewBody {...props} ref={bodyRef} />
      </div>
    </div>
  );
}
