/**
 * Every decision the settings panel makes about the folder this extension writes.
 *
 * A module rather than logic inside the panel for the reason `reloadStatus.ts` and `overviewSummary.ts`
 * are: it imports no React and no `@cove/runtime/*`, so the rules below are reachable from a test with
 * no DOM and no stand-in for the host runtime. What is in here is the whole of what the panel decides —
 * which torrents a filter admits, how many the cap shows, what the count line then claims, what one
 * torrent's state is called, and what the bulk button says it will take.
 *
 * The last of those is the one worth stating plainly: **the bulk button's label is its specification.**
 * A "Remove all" beside a filter box is ambiguous — it could mean the twelve on screen or the three
 * thousand behind them — and rather than resolve that in a tooltip, the label names the set and counts
 * it. It follows that the label and the request must be built from the same list, which is why
 * `wipeLabel` and the panel's own submission both read `filterTorrents` and neither re-derives it.
 */

/** One torrent in the folder, as `WriteFolderService` reports it. */
export interface FolderTorrent {
  /** Path relative to the folder — the identity a remove names, and what the user dragged in. */
  file: string;
  /** The release name inside the file, or null when it will not parse. */
  name: string | null;
  /** The tracker's id. Without one an apply writes no link, so `applied` can only be zero. */
  torrentId: string | null;
  /** Video files the release carries. More than one is a pack. */
  videoFiles: number;
  /** Of those, how many the library holds a file of that exact size for. */
  inLibrary: number;
  /** Videos linked to this torrent. */
  applied: number;
}

/**
 * How many torrents are listed before the cap asks the user to filter or expand.
 *
 * A default rather than a rule: `Show all` lifts it. Small enough that the panel stays a panel on a
 * folder of a few thousand, large enough that a folder of six never sees the cap at all — which is the
 * common case, since bulk collections belong in source folders and this one holds what was dropped.
 */
export const FOLDER_PAGE = 25;

export type FolderTorrentKind =
  | "unreadable"
  | "no-video"
  | "absent"
  | "to-apply"
  | "partial"
  | "applied";

export interface FolderTorrentState {
  kind: FolderTorrentKind;
  /** What the pill reads. */
  label: string;
  /** Shown as its own pill, because it is why the state beside it may be a fraction. */
  isPack: boolean;
}

/**
 * What one torrent's row says about itself.
 *
 * `applied` outranks everything below it because it is the only part with a consequence: removing a
 * torrent nothing was applied from changes nothing but the folder, and removing one that *was* applied
 * takes its rows off the batch page until the file comes back. The row has to be able to say which of
 * those the user is about to do.
 *
 * A pack reports a fraction rather than the word. "Applied" on a 47-scene pack with 12 done is false,
 * and it is false in the specific way this whole design already refused once: a file-level flag cannot
 * express partial completion, which is why completion lives on `VideoRemoteId` per video and not on the
 * filesystem.
 */
export function folderTorrentState(torrent: FolderTorrent): FolderTorrentState {
  const isPack = torrent.videoFiles > 1;

  if (torrent.name === null)
    return { kind: "unreadable", label: "unreadable", isPack: false };

  // Parsed, but an image set, comic or audio-only release. Routine rather than broken — `HasVideo`
  // exists for exactly these — so it is not called a failure.
  if (torrent.videoFiles === 0)
    return { kind: "no-video", label: "no video", isPack: false };

  if (torrent.applied >= torrent.videoFiles)
    return { kind: "applied", label: "applied", isPack };

  if (torrent.applied > 0)
    return {
      kind: "partial",
      label: `${torrent.applied} / ${torrent.videoFiles} applied`,
      isPack,
    };

  if (torrent.inLibrary > 0)
    return { kind: "to-apply", label: "to apply", isPack };

  // The overwhelmingly common case, and not a fault: the folder describes a tracker rather than a
  // shelf. Worded as a statement about the library rather than about the torrent, because there is
  // nothing wrong with the torrent.
  return { kind: "absent", label: "not in your library", isPack };
}

/**
 * The torrents a filter admits — matched against the filename and the release name together.
 *
 * Both, because they are different strings and a user may remember either: the file is what they
 * dragged in, the name is what the batch page showed them. Matching only one would make the filter miss
 * the half of the list the user was thinking of.
 *
 * A file that will not parse has no name and stays findable by its filename alone, which is the only
 * handle it has.
 */
export function filterTorrents(
  torrents: readonly FolderTorrent[],
  query: string,
): FolderTorrent[] {
  const needle = query.trim().toLowerCase();
  if (needle === "") return [...torrents];

  return torrents.filter(
    (torrent) =>
      torrent.file.toLowerCase().includes(needle) ||
      (torrent.name !== null && torrent.name.toLowerCase().includes(needle)),
  );
}

/**
 * The line under the list.
 *
 * It never states a total it is not showing without saying so. "3,182 torrents" under a list of 25 is
 * the failure this exists to avoid — it reads as a complete list, and the user stops looking for the
 * one that is missing.
 *
 * Plain numbers rather than `toLocaleString`: the grouping character would follow the host's locale,
 * which makes the string untestable without pinning one and makes it disagree with the counts the rest
 * of this UI prints.
 */
export function folderCount(shown: number, matched: number, query: string): string {
  const typed = query.trim();

  if (matched === 0)
    return typed === "" ? "The folder is empty" : `No torrent matches “${typed}”`;

  if (shown < matched)
    return typed === ""
      ? `Showing ${shown} of ${matched}`
      : `Showing ${shown} of ${matched} matches`;

  if (typed === "")
    return `${matched} ${matched === 1 ? "torrent" : "torrents"}`;

  return `${matched} ${matched === 1 ? "match" : "matches"}`;
}

/**
 * What the bulk button says, or null when there is nothing for it to do.
 *
 * The count in the label is the count it takes — every match, not the page of them on screen. That is
 * only safe to do *because* the label states the number, which is the entire argument for building the
 * label from the filtered list rather than from the visible one.
 */
export function wipeLabel(matched: number, query: string): string | null {
  if (matched === 0) return null;

  const typed = query.trim();
  return typed === ""
    ? `Remove all ${matched}`
    : `Remove ${matched} matching “${typed}”`;
}

/** Everything the confirm says, so the wording is testable rather than buried in a component. */
export interface RemovalPlan {
  /** Names to send. Relative to the folder, exactly as the listing gave them. */
  files: string[];
  title: string;
  /** One paragraph each, in order. */
  lines: string[];
  /** The destructive button. It repeats the count, so the last thing clicked still states the size. */
  confirmLabel: string;
}

/**
 * What removing this selection will do, in the words the user gets to read first.
 *
 * Three things are said, and each earns its place from something that is actually true here rather
 * than from a house style for destructive dialogs:
 *
 * - **The file may be the only copy.** This is the single respect in which our folder differs from a
 *   source folder the operator manages: they dragged it in, and their torrent client may not have it.
 *   It is about the file, never about the metadata.
 * - **Applied tags stay.** Said only when something was applied, because otherwise it is a reassurance
 *   about a risk the user does not have. The link Cove stores and the baseline the extension stores are
 *   both keyed by (video, torrent) and neither is touched, so re-adding the file restores the row.
 * - **Your own folders are not touched** — whole-folder removals only. "Remove everything" is exactly
 *   the phrase someone might fear means everything the extension can *see*, and it can see their
 *   source folders.
 */
export function planRemoval(selected: readonly FolderTorrent[], inFolder: number): RemovalPlan {
  const files = selected.map((torrent) => torrent.file);
  const applied = selected.reduce((total, torrent) => total + torrent.applied, 0);
  const whole = selected.length > 1 && selected.length >= inFolder;
  const lines: string[] = [];

  if (selected.length === 1) {
    lines.push(
      `${files[0]} is deleted from disk. If it is the only copy you have, it is gone.`,
    );
  } else if (whole) {
    lines.push(
      `Every torrent the extension holds is deleted from disk — the whole folder, ${selected.length} of them. ` +
        "Any you have no other copy of are gone.",
    );
  } else {
    lines.push(
      `${selected.length} torrents are deleted from disk — every one this filter matches, not just the ones on screen. ` +
        "Any you have no other copy of are gone.",
    );
  }

  if (applied > 0) {
    lines.push(
      `Tags already applied stay on your videos. ${applied} applied ${applied === 1 ? "row" : "rows"} ` +
        "leave the batch page and come back if you add the file again.",
    );
  }

  if (whole) lines.push("Your own torrent folders are not touched.");

  return {
    files,
    title:
      selected.length === 1
        ? "Remove this torrent from the folder?"
        : `Remove ${selected.length} torrents from the folder?`,
    lines,
    confirmLabel: selected.length === 1 ? "Remove" : `Remove ${selected.length}`,
  };
}

/**
 * Whether a `Show all` control has anything to offer.
 *
 * Offered while capped and holding something back, and while expanded so the way back exists. A control
 * beside a list already showing everything would claim there is more.
 */
export function canToggleCap(matched: number, capped: boolean, cap = FOLDER_PAGE): boolean {
  return capped ? matched > cap : true;
}

/**
 * What the panel says while the listing is on its way.
 *
 * `Loading…` on its own is a word that could mean anything, and on a folder of a few thousand it sits
 * there for a second or more: reading and parsing that folder was measured at 1.06 s warm and 2.34 s
 * cold, against 8 ms for the stat sweep that counts it. The count therefore arrives long before
 * the list does, and saying it turns an unqualified wait into one whose size the user can see.
 *
 * `files` is null until the sweep has answered, or for good if it failed — the panel says the plain
 * thing then rather than waiting for a number to wait with.
 */
export function listingLabel(files: number | null): string {
  // Zero is the plain wording too: "Reading 0 torrents…" is a sentence about nothing, and the empty
  // state that follows says it properly.
  if (files === null || files === 0) return "Loading…";

  return `Reading ${files} torrent${files === 1 ? "" : "s"}…`;
}

/** What the failed listing itself says, distinct from the reason a removal was refused. */
export function listingFailureMessage(reason: string): string {
  return `Couldn't read the folder: ${reason}`;
}

/**
 * What the section above the list is showing, as one value rather than a chain of ternaries the
 * component has to keep in step.
 *
 * `"error"` exists because a failed listing used to have no state of its own — the wait label
 * (`listingLabel`) stayed up forever with nothing to retry, and the message went to the panel's
 * shared notice at the bottom, arriving detached from the section it was actually about. A
 * listing failure is local to this section and stays that way: it does not borrow the panel's notice
 * line, which is for what the user's own click just did, not for a background read that failed on its
 * own.
 *
 * Order matters and mirrors what the component checked inline before: not-configured outranks a
 * listing failure, because the folder listing fires independently of whether the folder path has
 * arrived yet, and "not configured" is the truer thing to say when both are true at once.
 */
export type FolderSectionState =
  | { kind: "not-configured" }
  | { kind: "error"; message: string }
  | { kind: "loading"; label: string }
  | { kind: "empty" }
  | { kind: "list" };

export function folderSectionState(
  folder: string | null,
  torrents: readonly FolderTorrent[] | null,
  listingError: string | null,
  expected: number | null,
): FolderSectionState {
  if (folder === null) return { kind: "not-configured" };
  if (listingError !== null) return { kind: "error", message: listingFailureMessage(listingError) };
  if (torrents === null) return { kind: "loading", label: listingLabel(expected) };
  if (torrents.length === 0) return { kind: "empty" };
  return { kind: "list" };
}

/**
 * The list after a removal — or null, meaning the folder has to be read again.
 *
 * Re-reading after every removal is what this replaces, and it is not cheap: the panel re-parsed the
 * whole folder to reflect one file leaving it, with every control disabled meanwhile, on the action a
 * user repeats most. Nothing about the remaining rows changes when a file is deleted — the
 * counts on each are that torrent's own — so dropping them locally shows the same thing the re-read
 * would have.
 *
 * **A refusal is where that stops being true.** It means the list the user acted on disagreed with the
 * folder, which is the one moment a fresh read is actually owed, and the refusals are prose rather than
 * names so there is no honest way to subtract them. So any refusal re-reads, whatever else succeeded.
 */
export function afterRemoval(
  torrents: readonly FolderTorrent[] | null,
  requested: readonly string[],
  refused: readonly string[],
): FolderTorrent[] | null {
  if (refused.length > 0 || torrents === null) return null;

  const gone = new Set(requested);
  return torrents.filter((torrent) => !gone.has(torrent.file));
}
