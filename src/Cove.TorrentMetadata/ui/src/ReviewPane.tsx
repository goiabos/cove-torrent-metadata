import React from "@cove/runtime/react";
import { ReviewBody, type ReviewBodyHandle, type ReviewBodyProps } from "./ReviewBody";

const { useRef } = React;

/**
 * The review as a pane beside the list, which is what the batch page uses.
 *
 * The same `ReviewBody` the modal draws; only the frame differs. What that frame buys is the thing a
 * backdrop cannot: the rows stay on screen, so the reviewer can see what is left, jump to a row, and
 * change what they are working on without ending the review — and the tag list gets a column of its
 * own to be tall in, which is what the tag filter needs.
 *
 * **Sticky rather than a fixed two-pane frame.** A frame wants a height, and the host's chrome is not
 * ours to measure — the page is a component Cove renders inside its own layout, and a guess that is
 * wrong by forty pixels either double-scrolls or clips the footer with Apply in it. So the list
 * scrolls with the page and this stays put beside it, bounded by the viewport it can actually see.
 *
 * Below the breakpoint there is no room for both, and the stylesheet turns this same element into a
 * sheet over the page — one DOM, two presentations, no width measured in JavaScript. It is **not**
 * `aria-modal` even then: it traps no focus, and saying otherwise would be the lie.
 *
 * Escape is scoped to the pane rather than the document, because this is not modal: it closes the
 * review when the reviewer is *in* it, and leaves the rest of the page's keys alone.
 *
 * Escape does not call `onClose` directly: it goes through `bodyRef.current.requestClose`, the same
 * imperative handle `MatchDialog` uses (see the comment there). This pane opens the identical race
 * `onClose` mid-apply would — the batch page's `closeReview` nulls its queue synchronously — so Escape
 * here has to defer to the same decision the modal's Escape and backdrop and the Close button inside
 * `ReviewBody` all defer to, rather than growing a second copy of "is an apply in flight?".
 */
export function ReviewPane(props: ReviewBodyProps) {
  const bodyRef = useRef<ReviewBodyHandle>(null);

  return (
    <section
      className="tm-pane"
      aria-label="Review"
      onKeyDown={(event) => {
        if (event.key !== "Escape") return;
        // The filter input clears itself on Escape and stops the event there, so this only ever ends
        // a review the reviewer has nothing left to back out of first.
        event.stopPropagation();
        bodyRef.current?.requestClose();
      }}
    >
      <ReviewBody {...props} ref={bodyRef} />
    </section>
  );
}
