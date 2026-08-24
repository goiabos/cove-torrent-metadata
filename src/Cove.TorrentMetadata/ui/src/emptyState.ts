/**
 * What the batch page says when it has no rows to show.
 *
 * Four different situations produce an empty table and they are not interchangeable — a filter that
 * matched nothing, a folder with no torrents in it yet, a folder full of torrents describing files
 * this library does not have, and everything matched already applied. Telling them apart is the whole
 * value of the line: the third is the one that means "this is working, your library just is not in
 * this release", and reading the second in its place sends the user to check a folder that is fine.
 *
 * It is a module because the ladder lived inline in the component, where two of its four branches
 * were string literals no test could reach — the same rule that produced `folderState.ts` and
 * `writeFolder.ts`. No React and no `@cove/runtime/*`, so it needs no DOM and no stand-in for the
 * host runtime.
 *
 * **The order is the meaning.** The scope message goes first because a filter the user typed is the
 * nearest cause and the one they can undo; then "nothing indexed", which is about the folder rather
 * than the library; then "nothing matched", which is about the library rather than the folder. The
 * catch-all is last because it is the only one that is good news.
 */

import { emptyScopeMessage, type RowScope } from "./review";
import { whereToPutTorrents, type FolderStateReport } from "./folderState";

export interface EmptyStateInput {
  /** The page's current filters. */
  scope: RowScope;
  /** Rows left after "Hide applied", before the scope filters — so a filter's own emptiness is visible. */
  onPage: number;
  /** Video files across every indexed torrent. Zero means nothing has been read yet. */
  indexed: number;
  /** Torrents behind those files, for the sentence that reports both. */
  torrents: number;
  /** Rows the overview holds at all, ignoring every filter. Zero means nothing matched the library. */
  total: number;
  /** The folder probe's last answer, or null. Only consulted when nothing is indexed. */
  folderState: FolderStateReport | null;
}

export function emptyStateMessage(input: EmptyStateInput): string {
  // A filter is the nearest cause and the one the user can undo, so it wins over anything structural.
  // `onPage > 0` is what makes it *the filter's* emptiness rather than the page's: with nothing behind
  // it either, blaming the filter would be wrong.
  if (input.onPage > 0) {
    const scoped = emptyScopeMessage(input.scope);
    if (scoped !== null) return scoped;
  }

  // Nothing read yet. This is the only branch that is about the folder, and the only one that has to
  // name where a torrent goes — which `folderState` owns, because it must name the folder we write to
  // and never a source, and must say when it does not exist yet.
  if (input.indexed === 0) return whereToPutTorrents(input.folderState);

  // Torrents were read and none of them describes a file this library holds. Reported with both
  // numbers because that is what makes it legible as "working, but not about your library" rather
  // than as a failure — on a fresh library it is the overwhelmingly common answer.
  if (input.total === 0) {
    return `None of the ${input.indexed} video files across ${input.torrents} torrents are in your library.`;
  }

  // Everything matched is applied. Names the control that reveals it, because a page that says
  // "nothing to show" while holding rows it is hiding is the one empty state that reads as a bug.
  return "Nothing to show. Everything matched is already applied — untick “Hide applied” to see it.";
}
