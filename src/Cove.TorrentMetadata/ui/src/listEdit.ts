/**
 * Reading the result of a list-setting edit.
 *
 * Two settings are edited the same way — cover hosts and source folders — and in both the **server**
 * owns what an entry means. `CoverHostSetting.Normalise` reduces a host to a bare hostname;
 * `SourceFolderSetting.Normalise` reduces a folder to an absolute path and refuses a relative one or
 * a filesystem root. Both de-duplicate, both run on every write. So the panel sends what the user
 * typed and renders what comes back rather than normalising here: a second copy of either rule would
 * drift from the one that actually decides whether a fetch is allowed or a folder is read.
 *
 * The cost of that choice is that a rejected entry and an accepted one look identical from the
 * client — both return 200 with a list. Comparing the list before and after is how the panel tells
 * them apart, which is the whole remit of this module. It imports no React and no
 * `@cove/runtime/*`, so it is reachable from a test.
 */

/**
 * Case-insensitively, because a host list de-duplicates that way and stores the spelling as typed.
 *
 * Folders are compared the same way here even though the server compares them by platform rules — on
 * Linux two paths differing only in case really are two folders. That only makes this *less* likely
 * to claim something was added, and the message for "no change" already covers both readings; the
 * alternative is teaching the browser the server's filesystem, which it cannot know.
 */
function has(entries: readonly string[], entry: string): boolean {
  return entries.some((existing) => existing.toLowerCase() === entry.toLowerCase());
}

/**
 * The entry that appeared, or null if the list did not grow.
 *
 * Written as a diff rather than as `after[after.length - 1]` so it does not quietly depend on the
 * server appending: `Clean` preserves submission order today, and a caller reading the last element
 * would go wrong silently on the day it sorts.
 */
export function addedEntry(before: readonly string[], after: readonly string[]): string | null {
  return after.find((entry) => !has(before, entry)) ?? null;
}

/**
 * What to tell the user after an add.
 *
 * The unchanged case is deliberately ambiguous. A submission that collapses onto an existing entry
 * and one that normalises to nothing are indistinguishable without re-implementing the normaliser,
 * and guessing wrong is worse than naming both: "already listed" on a typo reads as the panel
 * insisting the user has already done something they have not.
 *
 * "Public" is doing real work in that sentence. `CoverHostSetting` now also drops an entry that
 * names this server's own network — an address literal, `localhost`, or an undotted intranet name
 * — and those are the entries someone is most likely to type deliberately and then wonder
 * about. Saying only "is not a host name" would read as the panel failing to parse `127.0.0.1`.
 */
export function describeHostAdd(before: readonly string[], after: readonly string[]): string {
  const added = addedEntry(before, after);
  return added === null
    ? "No change — that is already listed, or is not a public host name."
    : `Now fetching covers from ${added}.`;
}

/**
 * The same, for a source folder.
 *
 * Its own message because the ways a folder is refused are not the ways a host is: a relative path
 * and a filesystem root are both dropped by `SourceFolderSetting`, and "not a host name" would tell
 * someone who typed `../torrents` nothing about why it vanished.
 */
export function describeFolderAdd(before: readonly string[], after: readonly string[]): string {
  const added = addedEntry(before, after);
  return added === null
    ? "No change — that folder is already listed, or is not an absolute path."
    : `Now reading torrents from ${added}. Rescan to pick them up.`;
}
