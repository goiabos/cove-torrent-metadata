/**
 * Every decision the browser makes about a proposal, as pure functions.
 *
 * These used to be inlined in `MatchDialog` and `TorrentBatchPage`, which made them untestable
 * without a DOM — and they are not presentational. `buildApplyRequest` in particular decides what
 * the server is allowed to change, so a mistake here rewrites values the reviewer never ticked,
 * which is the exact inverse of what this extension promises.
 *
 * Nothing in this file imports React or `@cove/runtime/*`: the host owns those singletons and the
 * bundle marks them external, so a module that stays free of them needs no test-only stand-in for
 * the host runtime. The `./api` import is types only and erases at build time.
 */

import { rowKey } from "./queue";
import type {
  ApplyRequest,
  BatchRow,
  ProposedRelation,
  ProposedStudio,
  TorrentApplyResult,
  TorrentMatchProposal,
} from "./api";

export type FieldKey = "title" | "date" | "studioName" | "url";

export interface FieldSpec {
  key: FieldKey;
  label: string;
  proposed: string;
  current: string | null;
  /** True when accepting this would replace an existing value rather than fill an empty one. */
  replaces: boolean;
}

/** What the reviewer has ticked. Absent from these sets means absent from the request. */
export interface Selection {
  fields: ReadonlySet<FieldKey>;
  tags: ReadonlySet<string>;
  /** Performer ids — what the apply takes, and the only thing that identifies one. */
  performers: ReadonlySet<number>;
  /**
   * The studio picked from a choice, or null for "none".
   *
   * Separate from `fields` because it is not a tick: `fields` answers *whether* to accept a value the
   * server proposed, and this answers *which* value, from a set where the server deliberately proposed
   * none. Folding it into `fields` would need a proposed studio to point at, and the whole state is
   * that there isn't one.
   */
  studio: string | null;
}

/**
 * What the window should do about the studio, as one closed set of answers.
 *
 * Four states rather than a nullable name, because the reviewer can tell three of them apart and the
 * server can tell all four: a torrent that named no studio, a torrent whose studios the library does
 * not hold, one that resolves, and one where the library holds two and only the person looking at the
 * video knows which. The last two were once both "no studio" and read identically to the first.
 *
 * `many` carries a count and no names on purpose — naming five studios the window will not offer is
 * noise, and a shortlist of two drawn from five would have to be ordered, which is the defect the studio rule
 * exists to kill.
 */
export type StudioProposal =
  | { kind: "none" }
  | { kind: "one"; name: string }
  | { kind: "choose"; options: readonly ProposedStudio[] }
  | { kind: "many"; count: number };

export function studioProposal(proposal: TorrentMatchProposal): StudioProposal {
  if (proposal.studioName) return { kind: "one", name: proposal.studioName };
  if (proposal.studioChoices.length === 2) return { kind: "choose", options: proposal.studioChoices };
  // Two or more matched but none is offerable — three or more studios, or one studio the library holds
  // twice. Both are a count and nothing else.
  if (proposal.studioMatchCount >= 2) return { kind: "many", count: proposal.studioMatchCount };
  return { kind: "none" };
}

/** The scalar fields worth showing, and whether accepting one would overwrite something. */
export function buildFields(proposal: TorrentMatchProposal): FieldSpec[] {
  const scalars: Array<[FieldKey, string, string | null, string | null]> = [
    ["title", "Title", proposal.title, proposal.currentTitle],
    ["date", "Date", proposal.date, proposal.currentDate],
    ["studioName", "Studio", proposal.studioName, proposal.currentStudioName],
  ];

  const fields: FieldSpec[] = [];
  for (const [key, label, proposed, current] of scalars) {
    if (!proposed) continue;
    // Identical values present no decision, so they are left out entirely rather than shown as a no-op.
    if (current && current.trim() === proposed.trim()) continue;
    fields.push({ key, label, proposed, current, replaces: Boolean(current && current.trim()) });
  }

  if (proposal.url && !proposal.currentUrls.includes(proposal.url)) {
    // The URL is additive: it joins whatever the video already has rather than replacing it.
    fields.push({ key: "url", label: "URL", proposed: proposal.url, current: null, replaces: false });
  }

  return fields;
}

/**
 * The ones worth ticking, keyed by whatever identifies them.
 *
 * Tags are keyed by name and performers by id, which is not a stylistic split: a performer's name
 * stopped being something Cove can resolve, so the dialog holds the id the apply will send.
 */
const importable = <T extends { alreadyApplied: boolean }, K>(
  items: readonly T[],
  key: (item: T) => K,
): ReadonlySet<K> => new Set(items.filter((item) => !item.alreadyApplied).map(key));

/**
 * What is ticked when the dialog opens.
 *
 * Pack metadata is a union across every scene in the torrent, so most of it is wrong for any single
 * video: a pack starts with nothing selected, making inclusion a deliberate act. This is the only
 * place that rule exists — the server reports `fanOut` and takes no view on it.
 *
 * For a single scene the safe half is pre-ticked: fields that would fill a gap, and relations the
 * video does not already carry. A field that would *replace* an existing value never starts on.
 */
export function defaultSelection(proposal: TorrentMatchProposal, fields: readonly FieldSpec[]): Selection {
  if (proposal.fanOut > 1) return emptySelection();

  return {
    fields: new Set(fields.filter((field) => !field.replaces).map((field) => field.key)),
    tags: importable(proposal.tags, (tag) => tag.name),
    performers: importable(proposal.performers, (performer) => performer.id),
    // A choice never starts made, on a single scene or a pack. The other fields pre-tick because the
    // server has proposed a value and ticking it accepts one; here the server has deliberately proposed
    // nothing, and picking one of two curated studios on the reviewer's behalf is the guess the studio rule removed.
    // "None" is not a placeholder for a decision — it is the decision the extension already reached.
    studio: null,
  };
}

/**
 * Select-all and select-none for the tag list, scoped to the rows actually on screen.
 *
 * It used to sweep the scalar fields too, which meant one click undid the care in
 * `defaultSelection`: fields that would *replace* a curated value start off deliberately, and
 * ticking them is what sets `overwrite` on the request. "Select all" then "Apply selected" was two
 * clicks from replacing a title, date and studio the reviewer never revisited.
 *
 * A tag list runs 30–80 entries and is the only part of the dialog long enough to need a sweep. A
 * field is four checkboxes with the current and proposed values side by side — a considered
 * decision, not a list. Scoping the button to tags is what makes it unable to set `overwrite` at
 * all, rather than guarding the consequence after the fact.
 *
 * **`shown` is the scope, and that is the whole of what makes a filter safe.** Sweeping every
 * importable tag from a button beside a filtered list would tick what the reviewer cannot see;
 * clearing every tick would untick decisions they had already made about tags now hidden. Both are
 * an older mistake in a new place, which is why `describeTagFilter` puts the scope in the button's own label
 * rather than leaving it true only in the code. Unfiltered, `shown` is the whole importable
 * list and this is exactly the sweep it has always been.
 *
 * Tags already on the video stay out: their checkboxes are disabled, so ticking them would show a
 * tick the reviewer cannot clear.
 */
export function sweepTags(
  current: ReadonlySet<string>,
  shown: readonly ProposedRelation[],
  on: boolean,
): ReadonlySet<string> {
  const names = importable(shown, (tag) => tag.name);
  return on
    ? new Set([...current, ...names])
    : new Set([...current].filter((name) => !names.has(name)));
}

/**
 * The tag list split into what can be decided and what cannot.
 *
 * A tag already on the video renders with its checkbox disabled — it cannot be ticked, unticked or
 * acted on in any way — yet it costs the same row as a live decision, and at the corpus median that
 * is most of the list on a second visit: a row reading `updated` is one already applied that has
 * since gained tags, so by construction its list is mostly inert.
 *
 * **Only the inert half is ever collapsed.** Everything in `toImport` stays on screen however long
 * the list runs — importing tags is what this extension is for, and a list that hides part of itself
 * behind a "more" link is the one thing this window must not do. If that means the modal scrolls,
 * the modal scrolls.
 */
export function partitionTags(proposal: TorrentMatchProposal): {
  toImport: ProposedRelation[];
  onVideo: ProposedRelation[];
} {
  return {
    toImport: proposal.tags.filter((tag) => !tag.alreadyApplied),
    onVideo: proposal.tags.filter((tag) => tag.alreadyApplied),
  };
}

/**
 * Two strings are the same query when they differ only in how their words are joined.
 *
 * Tag names arrive normalised into one style while the torrent's own spelling is whatever the
 * uploader typed, so `two-cam`, `two_cam`, `two.cam` and `two cam` are one tag being searched for
 * four ways. Cove's own tag search does not do this and a dot, hyphen or underscore hides the tag
 * there; a filter that repeated it would be unusable on exactly the lists it exists for.
 */
const searchable = (value: string): string => value.toLowerCase().replace(/[\s._-]+/g, " ").trim();

/**
 * The importable tags a typed query leaves on screen.
 *
 * Matched against the tag's own name *and* the torrent's spelling of it, because
 * `surprisingSource` means the string the reviewer read in the release may not be the string on the
 * checkbox — and searching for what you just read is the first thing anyone does.
 *
 * An empty query is not a filter: every tag comes back, and every line built from this reverts to
 * the unfiltered wording.
 */
export function filterTags(tags: readonly ProposedRelation[], query: string): ProposedRelation[] {
  const wanted = searchable(query);
  if (wanted === "") return [...tags];

  return tags.filter(
    (tag) => searchable(tag.name).includes(wanted) || (tag.source !== null && searchable(tag.source).includes(wanted)),
  );
}

/**
 * The number of importable tags at which a filter earns its place.
 *
 * The corpus median is 37 and p95 is 98 — a search box over those is chrome on a list that already
 * fits. 50 torrents carry over 200 content tags, p99 is 232 and the ceiling measured is 1,122, which
 * is the list this exists for. `docs/DESIGN-DECISIONS.md` fixed the threshold before the
 * feature: *"a filter bar is not the answer here — it is the answer at 200 rows"*.
 */
export const TAG_FILTER_MIN = 200;

/** Whether this review's tag list is long enough to be worth searching rather than reading. */
export function showsTagFilter(toImport: number): boolean {
  return toImport >= TAG_FILTER_MIN;
}

/** Everything the filtered tag header says, so a component holds none of it as a literal. */
export interface TagFilterLines {
  /** "14 of 1,122 shown", or null when nothing is being filtered. */
  count: string | null;
  /** The sweep labels, which name their own scope — they *are* the specification of what they do. */
  selectAll: string;
  selectNone: string;
  /**
   * The ticks the filter is hiding, or null when it hides none.
   *
   * A selection survives the filter that hid it, because it is a set of names and the filter only
   * decides what renders — which is right, and is also the one way this feature could quietly apply
   * something the reviewer cannot see. So it is counted out loud.
   */
  hidden: string | null;
}

export function describeTagFilter(input: {
  query: string;
  shown: readonly ProposedRelation[];
  /** Every importable tag, filter or no filter. */
  total: number;
  selection: ReadonlySet<string>;
}): TagFilterLines {
  const unfiltered: TagFilterLines = { count: null, selectAll: "All tags", selectNone: "No tags", hidden: null };
  if (searchable(input.query) === "") return unfiltered;

  const shownNames = new Set(input.shown.map((tag) => tag.name));
  const hiddenTicks = [...input.selection].filter((name) => !shownNames.has(name)).length;
  const hidden =
    hiddenTicks === 0
      ? null
      : `${count(hiddenTicks, "selected tag")} ${hiddenTicks === 1 ? "is" : "are"} not shown by this filter.`
        + ` Apply still takes all ${input.selection.size}.`;

  // Nothing matched: the sweeps have no scope to name, and a button reading "All 0 shown" is worse
  // than one reading what it would do if anything did.
  if (input.shown.length === 0)
    return { count: `Nothing matches “${input.query.trim()}”`, selectAll: "All shown", selectNone: "None shown", hidden };

  return {
    count: `${input.shown.length.toLocaleString("en-US")} of ${input.total.toLocaleString("en-US")} shown`,
    selectAll: `All ${input.shown.length} shown`,
    selectNone: `None of the ${input.shown.length} shown`,
    hidden,
  };
}

/** What the tag section header states, so the numbers have one definition rather than a component's. */
export interface TagCounts {
  /** Every content tag the torrent offers this video, after de-duplication. */
  total: number;
  /** Of those, already carried by the video — the inert ones. */
  onVideo: number;
  /** The decidable remainder. */
  toImport: number;
  /** Of the remainder, tags that would reuse a row the library already holds. */
  existing: number;
  /** Of the remainder, tags that would be created. A subset of `toImport`, not a bucket beside it. */
  created: number;
}

/**
 * Counted here rather than in the dialog, because a past defect was exactly this arithmetic going wrong where
 * no test could see it: a headline number that answered a different question than it appeared to.
 */
export function countTags(proposal: TorrentMatchProposal): TagCounts {
  const { toImport, onVideo } = partitionTags(proposal);
  const existing = toImport.filter((tag) => tag.matchesExisting).length;

  return {
    total: proposal.tags.length,
    onVideo: onVideo.length,
    toImport: toImport.length,
    existing,
    created: toImport.length - existing,
  };
}

/**
 * Whether the cover comparison opens expanded rather than behind its button.
 *
 * Collapsed is right when the reviewer is deciding whether to *replace* artwork they already have —
 * the two thumbnails beside the checkbox are enough to notice the torrent's cover is a different
 * scene, and the full comparison is one click away. It is wrong when the video has no artwork
 * at all: there is nothing to weigh against, the answer is almost always yes, and the thing worth
 * seeing is the cover on offer.
 *
 * There is no point opening on a cover that will not be fetched, so a host the operator has not
 * allowed keeps it shut — the notice above it is what needs reading in that case, not an empty frame.
 *
 * `videoHasImage` comes from the server. The dialog used to learn it by rendering an image and
 * waiting for the 404, which meant a request per open, a console error, and a decision that could not
 * be made until after the first paint.
 */
export function coverStartsOpen(proposal: TorrentMatchProposal): boolean {
  return proposal.coverUrl !== null && proposal.coverHostAllowed && !proposal.videoHasImage;
}

/**
 * The torrent's own spelling of a tag, when it is worth showing beside the name.
 *
 * For a tag that resolves to an existing row the library's spelling wins and the source is noise. For
 * a new one it is the string that will be created and seeded as an alias — but almost always it is
 * just the display name with dots for spaces, and printing `two.cam` next to "Two Cam" teaches
 * nothing. So it surfaces only where normalising did something a reviewer would not have predicted,
 * which is the case worth catching before it becomes a tag.
 */
export function surprisingSource(tag: ProposedRelation): string | null {
  if (tag.source === null || tag.alreadyApplied || tag.matchesExisting) return null;

  const plain = tag.source.replace(/\./g, " ").trim().toLowerCase();
  return plain === tag.name.trim().toLowerCase() ? null : tag.source;
}

/** Nothing ticked. The starting point a pack gets, and what the tests build a bare request from. */
export const emptySelection = (): Selection => ({ fields: new Set(), tags: new Set(), performers: new Set(), studio: null });

/**
 * The apply request for a reviewed proposal.
 *
 * `overwrite` is a single flag the server then applies to every field in the request, so the
 * guarantee that an unticked field is untouched rests entirely on that field being sent as null —
 * both halves live here deliberately, because they are one invariant and were previously four lines
 * apart in a component.
 *
 * The cover is its own intent signal and is deliberately independent of `overwrite`: ticking it
 * replaces existing artwork even when no scalar field is being replaced. Gating it behind the
 * scalar flag once made the dialog say "will replace" and then do nothing, which is why the C# side
 * pins the same rule in `CoverImportTests`.
 */
export function buildApplyRequest(input: {
  proposal: TorrentMatchProposal;
  fields: readonly FieldSpec[];
  selection: Selection;
  importCover: boolean;
}): ApplyRequest {
  const { proposal, fields, selection, importCover } = input;

  const field = (key: FieldKey) =>
    selection.fields.has(key) ? fields.find((entry) => entry.key === key)?.proposed ?? null : null;

  return {
    videoId: proposal.videoId,
    coverUrl: importCover ? proposal.coverUrl : null,
    tags: [...selection.tags],
    performers: [...selection.performers],
    tagSources: Object.fromEntries(
      proposal.tags
        .filter((tag) => selection.tags.has(tag.name) && tag.source)
        .map((tag) => [tag.name, tag.source as string]),
    ),
    title: field("title"),
    date: field("date"),
    // Whichever of the two states produced a value. They cannot both be live: the server sends a
    // proposed studio or a choice, never both, so this is an alternation rather than a precedence.
    studioName: field("studioName") ?? selection.studio,
    url: field("url"),
    torrentId: proposal.torrentId,
    // Passed through untouched. The reviewer's ticks decide what is written; this decides what the
    // row says next time, and it is the server's own number coming back.
    torrentTagCount: proposal.torrentTagCount,
    // Only true when a ticked field would replace an existing value. Unticked fields are sent as
    // null above, so this cannot reach anything the reviewer did not choose.
    overwrite: fields.some((entry) => entry.replaces && selection.fields.has(entry.key)),
  };
}

/**
 * What an apply actually did, in one line.
 *
 * `TorrentApplyResult` carries ten fields so the UI can report what happened rather than claim
 * success blindly, and this is the only place that detail exists — the batch endpoint returns
 * totals, not per-video changes. It was computed inside the dialog and then discarded when the
 * caller unmounted it; it lives here now because both callers report it, in different places.
 *
 * "Nothing to apply." is the case worth having: a reviewer who ticks boxes, applies, and gets a
 * silent close cannot tell success from a no-op.
 *
 * The cover reason is appended rather than folded into the list because the cover is the one field
 * that can be asked for and silently not happen — a refused host would otherwise leave "Applied 12
 * tags." as the whole report.
 */
export function describeApplyResult(result: TorrentApplyResult): string {
  const parts = [
    result.tagsAdded && `${result.tagsAdded} tags`,
    result.tagsCreated && `${result.tagsCreated} created`,
    result.aliasesSeeded && `${result.aliasesSeeded} aliases`,
    result.performersAdded && `${result.performersAdded} performers`,
    result.titleChanged && "title",
    result.dateChanged && "date",
    result.studioChanged && "studio",
    result.urlAdded && "url",
    result.coverChanged && "cover",
  ].filter(Boolean);

  const applied = parts.length ? `Applied ${parts.join(", ")}.` : "Nothing to apply.";
  return result.coverSkipped ? `${applied} ${result.coverSkipped}` : applied;
}

/**
 * The rows a bulk apply would touch.
 *
 * The pack rule again, in its second implementation: one taglist describing a whole release
 * over-tags every scene in it, so packs are excluded unless the user asks for them. Already-applied
 * rows are skipped because re-applying them proposes nothing new.
 */
/**
 * What a tickable row's badge says.
 *
 * Three states, and the order matters: a relation already on the video is inert whatever else is
 * true of it, so it is answered first. This lived in `RelationRow` as a nested ternary, where the
 * only way to check it was to render a dialog — which this repo deliberately cannot do.
 */
export function relationBadge(relation: { alreadyApplied: boolean; matchesExisting: boolean }): string {
  if (relation.alreadyApplied) return "on video";
  return relation.matchesExisting ? "existing" : "new";
}

/**
 * What the review window is titled.
 *
 * The video's own name, never the torrent's proposed one — that title is one of the checkboxes
 * below, so naming the window after it made the heading a claim under review, and a reviewer who
 * declined it had spent the whole review reading a name that would not survive the apply. The
 * fallback names the video by id rather than leaving the heading empty on a library row with no
 * title.
 */
export function videoDisplayName(proposal: TorrentMatchProposal): string {
  return proposal.currentTitle ?? `Video ${proposal.videoId}`;
}

/**
 * The matched file, when naming it tells the reviewer something the heading does not.
 *
 * A pack's rows all carry one torrent name and one fan-out warning, so the file is the only thing
 * separating the feature from its trailer — and the window that withholds it is the one where the
 * tags get written. The list already shows it under the torrent name; a review beside that list
 * should say what the list says.
 *
 * Suppressed where it would only repeat itself: a single-file torrent is usually named after its own
 * file, and `CJOD-509 | CJOD-509.mp4 | matched on file size` spends a line saying nothing. The
 * comparison drops the extension and ignores case, because that is the shape the repetition takes —
 * the torrent is named for the file, not for the file plus `.mp4`.
 *
 * Naming it is not heading with it: the heading stays the video's own name, because a heading taken
 * from the torrent is a claim one of the checkboxes below is asking about. The file is not a
 * claim — it is which video this window is editing.
 */
export function matchedFileLabel(proposal: TorrentMatchProposal): string | null {
  const file = proposal.fileName?.trim();
  if (!file) return null;

  const torrent = proposal.torrentName?.trim() ?? "";
  const withoutExtension = file.replace(/\.[^./\\]+$/, "");
  const same = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: "accent" }) === 0;

  return same(torrent, file) || same(torrent, withoutExtension) ? null : file;
}

/**
 * What the primary action offers.
 *
 * After an apply the list holds only what was declined or has arrived since, so the button offers
 * that rather than repeating an act already carried out.
 */
export function applyButtonLabel(hasApplied: boolean): string {
  return hasApplied ? "Apply again" : "Apply selected";
}

/**
 * The host a cover would be fetched from, for naming it in the allowlist notice.
 *
 * Null for a URL the browser cannot parse, which is a real case: the URL comes out of the torrent,
 * and nothing has promised it is one.
 */
export function coverHost(url: string | null): string | null {
  if (!url) return null;
  try {
    return new URL(url).hostname;
  } catch {
    return null;
  }
}

export function eligibleRows(rows: readonly BatchRow[], includePacks: boolean): BatchRow[] {
  return rows.filter((row) => row.status === "matched" && (includePacks || row.fanOut === 1));
}

/**
 * The rows a typed query leaves on the batch page.
 *
 * Matched against everything the row puts on screen — the video's title, the torrent's name and the
 * file's — under the same joining rule as the tag filter, so a search for "harbour lights" finds
 * `harbour.lights.04.1080p`. At 715 rows the three checkboxes above the table are enough; at
 * four figures the reviewer is looking for *one release*, and the walk is built from what is visible,
 * so narrowing the list is also how a walk gets aimed.
 */
export function filterRows(rows: readonly BatchRow[], query: string): BatchRow[] {
  const wanted = searchable(query);
  if (wanted === "") return [...rows];

  return rows.filter((row) =>
    [row.videoTitle, row.torrentName, row.fileName].some(
      (value) => value !== null && searchable(value).includes(wanted),
    ),
  );
}

/**
 * Select-all and select-none for the row list, scoped to the rows on screen.
 *
 * The same shape as `sweepTags` and for the same reason: a sweep beside a filtered list must reach
 * neither the rows behind the filter nor the ticks already made about them. Only *matched* rows are
 * swept — an applied row has nothing to apply, so ticking it would show a tick that does nothing.
 * Packs are **not** excluded here: ticking one is how a reviewer consents to it, which is the whole
 * point of a selection.
 */
export function sweepRows(
  current: ReadonlySet<string>,
  shown: readonly BatchRow[],
  on: boolean,
): ReadonlySet<string> {
  const keys = new Set(shown.filter((row) => row.status === "matched").map(rowKey));
  return on
    ? new Set([...current, ...keys])
    : new Set([...current].filter((key) => !keys.has(key)));
}

/** What the header checkbox would do, named so its scope is legible without reading the code. */
export function describeRowSweep(shown: number, checked: boolean): string {
  return checked ? `Clear the ${shown} rows shown` : `Select the ${shown} rows shown`;
}

/**
 * What pressing *Apply* would cover, and what the button therefore says.
 *
 * One function because the label, the confirm and the request must be built from the same list — the
 * label *is* the specification of what the run does, and the two drifting apart is the failure
 * `writeFolder.ts` exists to prevent. It is also why the rows come back rather than a count: the
 * caller sends these, it does not re-derive them.
 *
 * **A selection wins over the page's own scope.** Ticked rows apply whether the current filter shows
 * them or not, because the tick is the reviewer's statement and a filter is only a view — the same
 * rule the tag filter follows, and the same obligation: the ticks a filter is hiding are counted out
 * loud rather than left to be discovered afterwards.
 *
 * With nothing ticked this is the sweep it has always been: every matched row on screen, packs only
 * where the flag allows them.
 */
export interface ApplyPlan {
  /** The rows a run would cover, in table order. */
  rows: BatchRow[];
  /** True when the reviewer ticked rows rather than letting the page's scope choose. */
  chosen: boolean;
  /** The primary button's label. */
  label: string;
  /** Ticked rows the current scope is not showing, or null when it is showing them all. */
  hidden: string | null;
}

export function planApply(input: {
  /** Every row the overview holds, so a ticked row survives a filter that hides it. */
  all: readonly BatchRow[];
  /** What the page's scope leaves on screen. */
  visible: readonly BatchRow[];
  selected: ReadonlySet<string>;
  includePacks: boolean;
}): ApplyPlan {
  if (input.selected.size === 0) {
    const rows = eligibleRows(input.visible, input.includePacks);
    return { rows, chosen: false, label: `Apply to ${rows.length}`, hidden: null };
  }

  const rows = input.all.filter((row) => row.status === "matched" && input.selected.has(rowKey(row)));
  const shown = new Set(input.visible.map(rowKey));
  const offscreen = rows.filter((row) => !shown.has(rowKey(row))).length;

  return {
    rows,
    chosen: true,
    label: `Apply to ${rows.length} selected`,
    hidden:
      offscreen === 0
        ? null
        : `${count(offscreen, "selected row")} ${offscreen === 1 ? "is" : "are"} not shown here.`
          + ` Apply still takes all ${rows.length}.`,
  };
}

/**
 * What the batch page is looking at, as one value rather than three booleans threaded separately.
 *
 * The walk is built from what is visible, so this scopes the walk as well as the list — narrowing it
 * is how a sitting gets aimed at one thing, and `resyncQueue` is what keeps a walk already in progress
 * honest when it changes underneath.
 */
export interface RowScope {
  /** Typed, matched on the video's title, the torrent's name and the file's. */
  query: string;
  /** Only rows whose torrent describes more than one video file. */
  packsOnly: boolean;
  /** One torrent's rows, by exact name — the "work this pack" sitting. */
  pack: string | null;
}

export const wholeList: RowScope = { query: "", packsOnly: false, pack: null };

/**
 * The rows a scope leaves on the page.
 *
 * The pack focus is an **exact** name match rather than a query, which is the difference between
 * working one release and working every release whose name contains it: a torrent named
 * `winter.set` would otherwise drag in `winter.set.2`, and the progress line beside it would be
 * counting a set the reviewer did not choose.
 */
export function scopeRows(rows: readonly BatchRow[], scope: RowScope): BatchRow[] {
  const scoped = rows.filter(
    (row) => (scope.pack === null || row.torrentName === scope.pack) && (!scope.packsOnly || row.fanOut > 1),
  );
  return filterRows(scoped, scope.query);
}

/**
 * Why a scoped list is empty, or null when the list is whole and the page's own counts explain it.
 *
 * A scope that hides everything looks exactly like a library with nothing to do, and the two want
 * opposite reactions from the reviewer. Which of the three did it is worth saying, because clearing
 * the wrong one is a wasted move.
 */
export function emptyScopeMessage(scope: RowScope): string | null {
  if (scope.pack !== null) return "No rows from this torrent are on the page.";
  if (scope.query.trim() !== "") return `No rows match “${scope.query.trim()}”.`;
  if (scope.packsOnly) return "No packs on this page.";
  return null;
}

/**
 * What one pack has left to give, for the banner over its own rows.
 *
 * **Two units, named separately, because they are not the same number.** `fanOut` is
 * `TorrentRelease.Videos.Count` — video files *inside the torrent* — while a row exists only where one
 * of those files matches something in the library. A fan-out 128 torrent with 40 rows has 88 files
 * this library does not hold, so "31 of 128 applied" would be false; it is 31 of 40. Reporting one
 * unit as the other is how a count stops meaning anything, and this line is the one place both
 * appear at once.
 *
 * The buckets match `overviewSummary`'s deliberately — `applied` and `updated` are separate there, so
 * they are separate here, and a zero is omitted rather than printed as a situation that did not
 * happen.
 */
export function packFocusSummary(rows: readonly BatchRow[], pack: string): string {
  const mine = rows.filter((row) => row.torrentName === pack);
  if (mine.length === 0) return "None of this torrent's files are in your library.";

  const of = (status: string) => mine.filter((row) => row.status === status).length;
  const matched = of("matched");
  const applied = of("applied");
  const updated = of("updated");

  return [
    `${mine.length.toLocaleString("en-US")} of this torrent's ${mine[0].fanOut.toLocaleString("en-US")}`
      + ` video files are in your library`,
    ...(matched ? [`${matched} to apply`] : []),
    ...(applied ? [`${applied} applied`] : []),
    ...(updated ? [`${updated} with new tags`] : []),
  ].join(" · ");
}

/**
 * What the row filter reports, or null when nothing is being filtered.
 *
 * It names its unit. Almost every figure on this page counts torrent video files, one counts videos,
 * and reporting one as the other is how a count stops meaning anything — so this says *rows*
 * out loud rather than joining a row of bare numbers.
 */
export function describeRowFilter(shown: number, total: number): string | null {
  if (shown === total) return null;
  return `${shown.toLocaleString("en-US")} of ${total.toLocaleString("en-US")} rows shown`;
}

/** The toggles a bulk apply would run under. `includePacks` is spent before this, on `eligibleRows`. */
export interface ApplyMode {
  createNewTags: boolean;
  importCovers: boolean;
}

/** What a bulk apply would write, as opposed to what the rows have on offer. */
export interface ApplyScale {
  videos: number;
  /** Tag links that would be written. Not the sum of `tagsToAdd` — see `summariseApply`. */
  tags: number;
  /** Of `tags`, how many need a tag row created first. Always 0 when `createNewTags` is off. */
  created: number;
  /** Covers that would be replaced, counting only rows whose host the operator has allowed. */
  covers: number;
  /**
   * Rows whose torrent describes more than one video file.
   *
   * Counted because a selection can put one in a run without the page's *Include packs* warning ever
   * being on screen: ticking the row is the consent, and the confirm is then the only place that says
   * what was consented to.
   */
  packs: number;
}

/**
 * How much a bulk apply would actually change.
 *
 * The confirm dialog named the mode and the row count but not the magnitude, and the row count is
 * the least alarming of the three numbers: a run over 693 videos writes something like 22,000 tag
 * links, and the tag links are the part with no undo — a created tag is stamped
 * `torrent-metadata.source` and can be picked back out as a set, while a link to a tag the library
 * already had is indistinguishable afterwards from one the user applied by hand.
 *
 * **It has to be mode-aware, which is why this is not a sum.** `tagsToAdd` is what the torrent
 * offers and `tagsToCreate` is a subset of it, not a second bucket — so with "create new
 * tags" off, the default, only the difference is written. Summing `tagsToAdd` there would overstate
 * the blast radius in the one sentence whose entire job is to state it accurately, and overstate it
 * worst on packs, whose tags are the least likely to already exist.
 *
 * Covers count allowed hosts only. The allowlist ships empty, so on a fresh install "replacing their
 * cover art" describes work that will not happen.
 */
export function summariseApply(rows: readonly BatchRow[], mode: ApplyMode): ApplyScale {
  let tags = 0;
  let created = 0;
  let covers = 0;
  let packs = 0;

  for (const row of rows) {
    tags += mode.createNewTags ? row.tagsToAdd : row.tagsToAdd - row.tagsToCreate;
    if (mode.createNewTags) created += row.tagsToCreate;
    if (mode.importCovers && row.torrentCoverUrl && row.torrentCoverAllowed) covers += 1;
    if (row.fanOut > 1) packs += 1;
  }

  return { videos: rows.length, tags, created, covers, packs };
}

const count = (n: number, noun: string) => `${n.toLocaleString("en-US")} ${noun}${n === 1 ? "" : "s"}`;

/**
 * The confirm dialog's whole message.
 *
 * It lives here rather than in the component for the same reason `describeApplyResult` does: the
 * numbers are the substance of the warning, and a sentence assembled inline is one nobody can test.
 *
 * The two zero cases are worth their own wording. No tags with "create new tags" off means every tag
 * on offer is one the library lacks, so unticking the box is what emptied the run — a plain "0 tags"
 * reads as a bug. No covers with the import on means no row has a cover from an allowed host, which
 * is the shipped default and would otherwise be silence.
 */
export function describeApplyScale(scale: ApplyScale, mode: ApplyMode): string {
  const tags =
    scale.tags === 0
      ? mode.createNewTags
        ? "no tags"
        : "no tags — every tag these torrents offer is one your library does not have yet"
      : mode.createNewTags
        ? `${count(scale.tags, "tag")}${scale.created ? `, creating ${scale.created.toLocaleString("en-US")} of them` : ""}`
        : `${count(scale.tags, "tag")} your library already has`;

  const covers = !mode.importCovers
    ? ""
    : scale.covers === 0
      ? "No cover will be replaced — none of these torrents has a cover from an allowed host."
      : `Cover art is replaced on ${count(scale.covers, "video")}.`;

  // Said here because a selection can carry a pack without the page's own warning being on screen:
  // ticking the row is the consent, so the confirm is the last place it can be spelled out.
  const packs =
    scale.packs === 0
      ? ""
      : `${count(scale.packs, "row")} ${scale.packs === 1 ? "is a pack" : "are packs"}, whose tag list`
        + ` describes a whole release rather than one scene.`;

  return [
    `${count(scale.videos, "video")} will be updated with ${tags}.`,
    packs,
    covers,
    "Fields are only filled where empty, and nothing is removed. This cannot be undone.",
  ]
    .filter(Boolean)
    .join(" ");
}
