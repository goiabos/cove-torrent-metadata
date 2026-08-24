/**
 * Reading the entity an action was invoked on, out of the host's action payload.
 *
 * Its own module rather than a private function in `main.tsx`, because that file imports
 * `@cove/runtime/react` and `@cove/runtime/react-dom-client` — specifiers the host resolves through
 * an import map at load time, with no implementation anywhere in this repo. A test importing
 * `main.tsx` cannot resolve them, so anything living there is unreachable from the suite. Like
 * `review.ts`, this file imports no React and no `@cove/runtime/*`, which is the whole reason it can
 * be tested without standing in for the host runtime.
 */

/**
 * What an entity action hands a registered handler.
 *
 * Typed as `unknown[]` on purpose. Cove builds these arrays from `number` ids today, but the payload
 * crosses an import-map boundary from code we do not compile against and do not version with, so
 * what actually arrives is a runtime question, not a compile-time one.
 */
export interface ActionPayload {
  entityIds?: unknown[];
  selectedIds?: unknown[];
}

/**
 * The first usable entity id in a payload, or null when there is none.
 *
 * Cove sends the id as a collection — `entityIds` from a detail page, `selectedIds` from a
 * multi-select — never as a bare scalar. Both are read so one handler serves either surface, and an
 * unusable entry is skipped rather than ending the search, so an empty `entityIds` beside a
 * populated `selectedIds` still resolves.
 *
 * The check is a **positive integer**, not `Number.isFinite`. `Number("")`, `Number(null)` and
 * `Number([])` are all `0`, and zero is finite — so an empty entry used to pass the guard and become
 * video id 0. The caller's own "This action needs a video." never fired; the drop zone opened for a
 * video that cannot exist, and the eventual failure blamed the torrent folder for a malformed
 * payload.
 *
 * Non-numeric candidates are rejected before coercion rather than after, which the issue did not
 * ask for and is worth the extra line: `Number(true)` is `1` and `Number([7])` is `7`, so a
 * coercion-first guard turns junk into a *plausible* id. Operating silently on the wrong video is a
 * worse outcome than any error message.
 */
export function firstEntityId(payload: ActionPayload | undefined): number | null {
  for (const candidate of [...(payload?.entityIds ?? []), ...(payload?.selectedIds ?? [])]) {
    if (typeof candidate !== "number" && typeof candidate !== "string") continue;

    const id = Number(candidate);
    if (Number.isInteger(id) && id > 0) return id;
  }

  return null;
}
