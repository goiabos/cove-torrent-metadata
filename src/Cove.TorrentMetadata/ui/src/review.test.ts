/**
 * The browser's half of the apply contract.
 *
 * Fixtures are invented here, in code — never transcribed from a real torrent. The tag lists and
 * filenames in a real one are the most identifying artefact in the project, and this directory is
 * published. A fixture where each tag drives one assertion reads better anyway.
 */

import { describe, expect, it } from "vitest";
import { rowKey } from "./queue";
import type {
  BatchRow,
  ProposedPerformer,
  ProposedRelation,
  TorrentApplyResult,
  TorrentMatchProposal,
} from "./api";
import {
  applyButtonLabel,
  buildApplyRequest,
  buildFields,
  countTags,
  coverStartsOpen,
  defaultSelection,
  coverHost,
  describeApplyResult,
  describeApplyScale,
  describeRowFilter,
  describeRowSweep,
  describeTagFilter,
  eligibleRows,
  emptyScopeMessage,
  emptySelection,
  filterRows,
  filterTags,
  packFocusSummary,
  planApply,
  partitionTags,
  relationBadge,
  scopeRows,
  showsTagFilter,
  studioProposal,
  summariseApply,
  surprisingSource,
  sweepRows,
  sweepTags,
  matchedFileLabel,
  videoDisplayName,
  wholeList,
  type FieldKey,
  type Selection,
} from "./review";

const relation = (name: string, over: Partial<ProposedRelation> = {}): ProposedRelation => ({
  name,
  source: null,
  matchesExisting: false,
  alreadyApplied: false,
  ...over,
});

/** Ids ascend from 1 by call order; the tests only need them distinct and stable. */
let nextPerformerId = 0;
const performer = (name: string, over: Partial<ProposedPerformer> = {}): ProposedPerformer => ({
  id: (nextPerformerId += 1),
  name,
  source: null,
  alreadyApplied: false,
  ...over,
});

const studio2 = (name: string, source: string) => ({ name, source });

const proposalOf = (over: Partial<TorrentMatchProposal> = {}): TorrentMatchProposal => ({
  videoId: 7,
  torrentName: "release.torrent",
  fileName: "scene.mp4",
  matchedOn: "size",
  fanOut: 1,
  torrentTagCount: 12,
  title: "Proposed Title",
  date: "2021-03-04",
  studioName: "Proposed Studio",
  studioChoices: [],
  studioMatchCount: 1,
  coverUrl: "https://images.example/cover.jpg",
  coverHostAllowed: true,
  url: "https://tracker.example/torrents.php?id=1",
  torrentId: "1",
  currentTitle: null,
  currentDate: null,
  currentStudioName: null,
  currentUrls: [],
  tags: [],
  performers: [],
  videoHasImage: true,
  tagNameStyle: "titlecase",
  ...over,
});

const select = (
  over: {
    fields?: Iterable<string>;
    tags?: Iterable<string>;
    performers?: Iterable<number>;
    studio?: string | null;
  } = {},
): Selection => ({
  fields: new Set((over.fields ?? []) as Iterable<FieldKey>),
  tags: new Set(over.tags ?? []),
  performers: new Set(over.performers ?? []),
  studio: over.studio ?? null,
});

describe("buildFields", () => {
  it("offers a proposed scalar the video does not have, as a fill rather than a replacement", () => {
    const fields = buildFields(proposalOf({ date: null, studioName: null, url: null }));

    expect(fields).toEqual([
      { key: "title", label: "Title", proposed: "Proposed Title", current: null, replaces: false },
    ]);
  });

  it("marks a scalar that would overwrite an existing value", () => {
    const fields = buildFields(proposalOf({ currentTitle: "Existing Title" }));

    expect(fields.find((field) => field.key === "title")).toMatchObject({
      current: "Existing Title",
      replaces: true,
    });
  });

  it("leaves out a value identical to the current one, ignoring surrounding whitespace", () => {
    const fields = buildFields(proposalOf({ currentTitle: "  Proposed Title  " }));

    expect(fields.map((field) => field.key)).not.toContain("title");
  });

  it("treats a blank current value as empty, so accepting it fills rather than replaces", () => {
    const fields = buildFields(proposalOf({ currentTitle: "   " }));

    expect(fields.find((field) => field.key === "title")?.replaces).toBe(false);
  });

  it("omits a scalar the torrent does not carry", () => {
    const fields = buildFields(proposalOf({ studioName: null }));

    expect(fields.map((field) => field.key)).not.toContain("studioName");
  });

  it("offers the URL as additive, and not at all when the video already has it", () => {
    const url = "https://tracker.example/torrents.php?id=1";

    expect(buildFields(proposalOf({ url })).find((field) => field.key === "url")).toMatchObject({
      replaces: false,
      current: null,
    });
    expect(buildFields(proposalOf({ url, currentUrls: [url] })).map((field) => field.key)).not.toContain("url");
  });
});

describe("defaultSelection", () => {
  it("pre-ticks what would fill a gap, and nothing that would replace", () => {
    const proposal = proposalOf({
      currentTitle: "Existing Title",
      tags: [relation("alpha"), relation("beta")],
      performers: [performer("First Performer")],
    });

    const selection = defaultSelection(proposal, buildFields(proposal));

    expect([...selection.fields].sort()).toEqual(["date", "studioName", "url"]);
    expect([...selection.tags]).toEqual(["alpha", "beta"]);
    // Ids, not names: the selection holds what the apply will send.
    expect([...selection.performers]).toEqual([proposal.performers[0].id]);
  });

  it("leaves out relations the video already carries", () => {
    const proposal = proposalOf({
      tags: [relation("alpha", { alreadyApplied: true }), relation("beta")],
      performers: [performer("First Performer", { alreadyApplied: true })],
    });

    const selection = defaultSelection(proposal, buildFields(proposal));

    expect([...selection.tags]).toEqual(["beta"]);
    expect([...selection.performers]).toEqual([]);
  });

  it("ticks nothing at all for a pack, because its metadata describes every scene at once", () => {
    const proposal = proposalOf({
      fanOut: 12,
      tags: [relation("alpha"), relation("beta")],
      performers: [performer("First Performer")],
    });

    const selection = defaultSelection(proposal, buildFields(proposal));

    expect([...selection.fields, ...selection.tags, ...selection.performers]).toEqual([]);
  });
});

describe("sweepTags", () => {
  /** Unfiltered, the sweep's scope is every importable tag — which is what it always was. */
  const shownOf = (proposal: TorrentMatchProposal) => partitionTags(proposal).toImport;

  it("takes every tag worth importing", () => {
    const proposal = proposalOf({ tags: [relation("alpha"), relation("beta")] });

    expect([...sweepTags(new Set(), shownOf(proposal), true)]).toEqual(["alpha", "beta"]);
  });

  it("still leaves out relations already on the video, whose checkboxes are disabled", () => {
    const proposal = proposalOf({ tags: [relation("alpha", { alreadyApplied: true }), relation("beta")] });

    expect([...sweepTags(new Set(), shownOf(proposal), true)]).toEqual(["beta"]);
  });

  it("offers a pack's tags, which start unticked but are still the reviewer's to sweep", () => {
    const proposal = proposalOf({ fanOut: 12, tags: [relation("alpha")] });

    expect([...sweepTags(new Set(), shownOf(proposal), true)]).toEqual(["alpha"]);
  });

  it("clears the whole list when nothing is filtering it", () => {
    const proposal = proposalOf({ tags: [relation("alpha"), relation("beta")] });

    expect([...sweepTags(new Set(["alpha", "beta"]), shownOf(proposal), false)]).toEqual([]);
  });

  it("ticks only what is shown, leaving the rest of the list alone", () => {
    // The whole of what makes a filter safe: the button beside a filtered list must not reach the
    // tags behind it, in either direction.
    const proposal = proposalOf({ tags: [relation("outdoor"), relation("indoor"), relation("poolside")] });
    const shown = filterTags(shownOf(proposal), "door");

    expect([...sweepTags(new Set(["poolside"]), shown, true)].sort()).toEqual(["indoor", "outdoor", "poolside"]);
  });

  it("clears only what is shown, so a tick it is hiding survives", () => {
    const proposal = proposalOf({ tags: [relation("outdoor"), relation("poolside")] });
    const shown = filterTags(shownOf(proposal), "outdoor");

    expect([...sweepTags(new Set(["outdoor", "poolside"]), shown, false)]).toEqual(["poolside"]);
  });

  it("cannot reach the fields, so it cannot be what sets overwrite", () => {
    // The point of the change, asserted where the coupling lives rather than in the component: this
    // proposal's title would replace a curated one, and a request built from a swept tag selection
    // still leaves it alone.
    const proposal = proposalOf({ currentTitle: "Existing Title", tags: [relation("alpha")] });

    const request = buildApplyRequest({
      proposal,
      fields: buildFields(proposal),
      selection: { ...emptySelection(), tags: sweepTags(new Set(), shownOf(proposal), true) },
      importCover: false,
    });

    expect(request.tags).toEqual(["alpha"]);
    expect(request.title).toBeNull();
    expect(request.overwrite).toBe(false);
  });
});

describe("buildApplyRequest", () => {
  it("sends an unticked field as null even while another ticked field asks to overwrite", () => {
    // The load-bearing one. `overwrite` is a single flag the server applies to every field in the
    // request, so the only thing keeping Date and Studio as they are is their being null here. If
    // this ever regresses, ticking one "would replace" box rewrites values nobody chose.
    const proposal = proposalOf({
      currentTitle: "Existing Title",
      currentDate: "1999-01-01",
      currentStudioName: "Existing Studio",
    });
    const fields = buildFields(proposal);

    const request = buildApplyRequest({
      proposal,
      fields,
      selection: select({ fields: ["title"] }),
      importCover: false,
    });

    expect(request.overwrite).toBe(true);
    expect(request.title).toBe("Proposed Title");
    expect(request.date).toBeNull();
    expect(request.studioName).toBeNull();
  });

  it("keeps overwrite off when every ticked field only fills an empty one", () => {
    const proposal = proposalOf({ currentDate: "1999-01-01" });
    const fields = buildFields(proposal);

    const request = buildApplyRequest({
      proposal,
      fields,
      selection: select({ fields: ["title", "studioName"] }),
      importCover: false,
    });

    expect(request.overwrite).toBe(false);
    expect(request.date).toBeNull();
  });

  it("sends performer ids, not the names the reviewer read", () => {
    // The label on the row is the library's canonical name, which may be nothing the torrent wrote —
    // an alias can be what found her. Sending a name would have asked Cove to resolve it again, and
    // from 1.3 that resolves to nothing and creates a duplicate.
    const angela = performer("Angela Frost", { source: "angela.blanche" });
    const proposal = proposalOf({ performers: [angela, performer("Noa Amane")] });

    const request = buildApplyRequest({
      proposal,
      fields: buildFields(proposal),
      selection: select({ performers: [angela.id] }),
      importCover: false,
    });

    expect(request.performers).toEqual([angela.id]);
  });

  it("sends nothing but the identifiers when nothing is ticked", () => {
    const proposal = proposalOf({ tags: [relation("alpha")], performers: [performer("First Performer")] });

    const request = buildApplyRequest({
      proposal,
      fields: buildFields(proposal),
      selection: emptySelection(),
      importCover: false,
    });

    expect(request).toEqual({
      videoId: 7,
      coverUrl: null,
      tags: [],
      performers: [],
      tagSources: {},
      title: null,
      date: null,
      studioName: null,
      url: null,
      torrentId: "1",
      // An identifier, not a field: it says what the torrent looked like, so the server can tell a
      // later re-tag from tags left uncreated. Nothing the reviewer ticks affects it.
      torrentTagCount: 12,
      overwrite: false,
    });
  });

  it("records the source spelling only for ticked tags that have one", () => {
    const proposal = proposalOf({
      tags: [
        relation("Alpha", { source: "alpha" }),
        relation("Beta Gamma", { source: "beta.gamma" }),
        relation("Delta"),
      ],
    });

    const request = buildApplyRequest({
      proposal,
      fields: buildFields(proposal),
      selection: select({ tags: ["Alpha", "Delta"] }),
      importCover: false,
    });

    expect(request.tags).toEqual(["Alpha", "Delta"]);
    expect(request.tagSources).toEqual({ Alpha: "alpha" });
  });

  it("sends the cover only when it is ticked, and independently of overwrite", () => {
    // The cover checkbox is its own intent signal: it replaces existing artwork with `overwrite`
    // off. `CoverImportTests.Replaces_an_existing_cover_even_though_overwrite_is_off` is the other
    // half of the same rule.
    const proposal = proposalOf();
    const fields = buildFields(proposal);
    const args = { proposal, fields, selection: emptySelection() };

    expect(buildApplyRequest({ ...args, importCover: true })).toMatchObject({
      coverUrl: "https://images.example/cover.jpg",
      overwrite: false,
    });
    expect(buildApplyRequest({ ...args, importCover: false }).coverUrl).toBeNull();
  });

  it("has no cover to send when the torrent carries no URL", () => {
    const proposal = proposalOf({ coverUrl: null });

    const request = buildApplyRequest({
      proposal,
      fields: buildFields(proposal),
      selection: emptySelection(),
      importCover: true,
    });

    expect(request.coverUrl).toBeNull();
  });

  it("ignores a ticked field the proposal does not actually offer", () => {
    const proposal = proposalOf({ studioName: null });

    const request = buildApplyRequest({
      proposal,
      fields: buildFields(proposal),
      selection: select({ fields: ["studioName"] }),
      importCover: false,
    });

    expect(request.studioName).toBeNull();
    expect(request.overwrite).toBe(false);
  });
});

describe("describeApplyResult", () => {
  const result = (over: Partial<TorrentApplyResult> = {}): TorrentApplyResult => ({
    tagsAdded: 0,
    tagsCreated: 0,
    performersAdded: 0,
    aliasesSeeded: 0,
    titleChanged: false,
    dateChanged: false,
    studioChanged: false,
    urlAdded: false,
    coverChanged: false,
    coverSkipped: null,
    ...over,
  });

  it("names only what actually changed", () => {
    const text = describeApplyResult(result({ tagsAdded: 12, tagsCreated: 3, titleChanged: true }));

    expect(text).toBe("Applied 12 tags, 3 created, title.");
    expect(text).not.toContain("performers");
    expect(text).not.toContain("date");
  });

  it("says so when an apply changed nothing, rather than reporting a success with no content", () => {
    expect(describeApplyResult(result())).toBe("Nothing to apply.");
  });

  it("appends the cover's reason instead of folding it into the list of what changed", () => {
    const text = describeApplyResult(
      result({ tagsAdded: 12, coverSkipped: "images.example is not an allowed cover host." }),
    );

    expect(text).toBe("Applied 12 tags. images.example is not an allowed cover host.");
  });

  it("still reports a refused cover when it was the only thing asked for", () => {
    const text = describeApplyResult(result({ coverSkipped: "No cover hosts are configured yet." }));

    expect(text).toBe("Nothing to apply. No cover hosts are configured yet.");
  });

  it("does not mention the cover when it was imported without complaint", () => {
    expect(describeApplyResult(result({ coverChanged: true }))).toBe("Applied cover.");
  });
});

const row = (over: Partial<BatchRow>): BatchRow => ({
  torrentName: "release.torrent",
  fileName: "scene.mp4",
  torrentId: null,
  fanOut: 1,
  status: "matched",
  videoId: 1,
  videoTitle: null,
  videoHasImage: false,
  videoTagCount: 0,
  tagsToAdd: 0,
  tagsToCreate: 0,
  performersToAdd: 0,
  torrentCoverUrl: null,
  torrentCoverAllowed: false,
  ...over,
});

describe("eligibleRows", () => {
  // Distinct by *video*, not by file name: a row is one video described by one torrent, and file
  // name stopped being part of that once rows gained a real identity.
  const rows = [
    row({ fileName: "single.mp4", videoId: 1 }),
    row({ fileName: "pack-scene.mp4", videoId: 2, fanOut: 40 }),
    row({ fileName: "done.mp4", videoId: 3, status: "applied" }),
    row({ fileName: "pack-done.mp4", videoId: 4, fanOut: 40, status: "applied" }),
    row({ fileName: "retagged.mp4", videoId: 5, status: "updated", tagsToAdd: 3 }),
  ];

  it("holds packs back unless they were asked for", () => {
    expect(eligibleRows(rows, false).map((r) => r.fileName)).toEqual(["single.mp4"]);
  });

  it("includes packs once the user opts in", () => {
    expect(eligibleRows(rows, true).map((r) => r.fileName)).toEqual(["single.mp4", "pack-scene.mp4"]);
  });

  it("never re-applies a row that is already applied", () => {
    expect(eligibleRows(rows, true).some((r) => r.status === "applied")).toBe(false);
  });

  it("leaves a re-tagged row out too, however much it has to offer", () => {
    // "updated" is applied plus something left over, and the something left over can be tags the
    // reviewer declined rather than tags the torrent gained. Bulk apply must not decide between those
    // on their behalf — the row is surfaced for a person to open, not swept up.
    expect(eligibleRows(rows, true).some((r) => r.status === "updated")).toBe(false);
    expect(eligibleRows(rows, true).map((r) => r.fileName)).toEqual(["single.mp4", "pack-scene.mp4"]);
  });
});

describe("summariseApply", () => {
  const both = { createNewTags: true, importCovers: true };
  const existingOnly = { createNewTags: false, importCovers: false };

  // 30 tags each, 12 of which the library does not have. The numbers are chosen so that the two
  // modes cannot agree by accident: 60 written with creation on, 36 with it off.
  const rows = [
    row({ videoId: 1, tagsToAdd: 30, tagsToCreate: 12 }),
    row({ videoId: 2, tagsToAdd: 30, tagsToCreate: 12 }),
  ];

  it("counts every tag on offer when the run is allowed to create them", () => {
    expect(summariseApply(rows, both)).toMatchObject({ videos: 2, tags: 60, created: 24 });
  });

  it("counts only the tags the library already has when creation is off", () => {
    // The default mode, and the one the dialog understated: `tagsToCreate` is a subset of
    // `tagsToAdd`, so the tags actually written are the difference, not the sum.
    expect(summariseApply(rows, existingOnly)).toMatchObject({ videos: 2, tags: 36, created: 0 });
  });

  it("reports no tags at all when every tag on offer would have to be created", () => {
    const fresh = [row({ tagsToAdd: 9, tagsToCreate: 9 })];

    expect(summariseApply(fresh, existingOnly).tags).toBe(0);
    expect(summariseApply(fresh, both).tags).toBe(9);
  });

  it("counts a cover only where the operator has allowed its host", () => {
    const covers = [
      row({ videoId: 1, torrentCoverUrl: "https://images.example/a.jpg", torrentCoverAllowed: true }),
      row({ videoId: 2, torrentCoverUrl: "https://elsewhere.example/b.jpg", torrentCoverAllowed: false }),
      row({ videoId: 3, torrentCoverUrl: null, torrentCoverAllowed: true }),
    ];

    expect(summariseApply(covers, both).covers).toBe(1);
  });

  it("counts no covers at all when the import is not ticked", () => {
    const covers = [row({ torrentCoverUrl: "https://images.example/a.jpg", torrentCoverAllowed: true })];

    expect(summariseApply(covers, existingOnly).covers).toBe(0);
  });

  it("is empty rather than undefined when nothing is eligible", () => {
    expect(summariseApply([], both)).toEqual({ videos: 0, tags: 0, created: 0, covers: 0, packs: 0 });
  });
});

describe("describeApplyScale", () => {
  const scale = (over: Partial<ReturnType<typeof summariseApply>> = {}) => ({
    videos: 693,
    tags: 22_000,
    created: 0,
    covers: 0,
    packs: 0,
    ...over,
  });

  it("puts the tag count in front of the reviewer, not just the video count", () => {
    const text = describeApplyScale(scale(), { createNewTags: false, importCovers: false });

    expect(text).toContain("693 videos");
    expect(text).toContain("22,000 tags your library already has");
    expect(text).toContain("This cannot be undone.");
  });

  it("names how many of those tags would be created", () => {
    const text = describeApplyScale(scale({ tags: 22_000, created: 4_100 }), {
      createNewTags: true,
      importCovers: false,
    });

    expect(text).toContain("22,000 tags, creating 4,100 of them");
  });

  it("does not claim to create anything when creation is on but nothing is missing", () => {
    const text = describeApplyScale(scale({ created: 0 }), { createNewTags: true, importCovers: false });

    expect(text).toContain("22,000 tags.");
    expect(text).not.toContain("creating");
  });

  it("explains an empty tag count rather than reporting a bare zero", () => {
    // Unticking "create new tags" is what emptied this run, and that is the actionable half.
    const text = describeApplyScale(scale({ tags: 0 }), { createNewTags: false, importCovers: false });

    expect(text).toContain("no tags — every tag these torrents offer is one your library does not have yet");
  });

  it("says how many covers would be replaced", () => {
    const text = describeApplyScale(scale({ covers: 40 }), { createNewTags: false, importCovers: true });

    expect(text).toContain("Cover art is replaced on 40 videos.");
  });

  it("says so when covers were asked for and no host is allowed", () => {
    // The allowlist ships empty, so this is what a fresh install sees. Silence would read as a
    // promise to replace artwork that the proxy is then going to refuse.
    const text = describeApplyScale(scale({ covers: 0 }), { createNewTags: false, importCovers: true });

    expect(text).toContain("No cover will be replaced");
  });

  it("says nothing about covers when they were not asked for", () => {
    const text = describeApplyScale(scale({ covers: 0 }), { createNewTags: false, importCovers: false });

    expect(text).not.toContain("over art");
    expect(text).not.toContain("No cover");
  });

  it("keeps the singular readable for a one-video run", () => {
    const text = describeApplyScale(
      { videos: 1, tags: 1, created: 0, covers: 1, packs: 0 },
      { createNewTags: false, importCovers: true },
    );

    expect(text).toContain("1 video will be updated with 1 tag your library already has.");
    expect(text).toContain("replaced on 1 video.");
  });
});

describe("studioProposal", () => {
  const studio = (name: string, source: string) => ({ name, source });

  it("proposes the one studio that resolved", () => {
    expect(studioProposal(proposalOf({ studioName: "Harbour Lights", studioMatchCount: 1 })))
      .toEqual({ kind: "one", name: "Harbour Lights" });
  });

  it("offers the choice when the library holds both", () => {
    const options = [studio("Harbour Lights", "harbourlights.com"), studio("Pier House", "pierhouse.com")];
    expect(studioProposal(proposalOf({ studioName: null, studioChoices: options, studioMatchCount: 2 })))
      .toEqual({ kind: "choose", options });
  });

  it("reports a count instead of a shortlist once more than two match", () => {
    // Naming five studios the window will not offer is noise, and a shortlist of two drawn from five
    // would have to be ordered — which is the defect the studio rule exists to kill.
    expect(studioProposal(proposalOf({ studioName: null, studioChoices: [], studioMatchCount: 5 })))
      .toEqual({ kind: "many", count: 5 });
  });

  it("reports a count when one studio is in the library twice", () => {
    // Two matched, neither offerable. Picking between two spellings of one studio is a library repair,
    // not a metadata decision, so it reads as the count case rather than as a choice.
    expect(studioProposal(proposalOf({ studioName: null, studioChoices: [], studioMatchCount: 2 })))
      .toEqual({ kind: "many", count: 2 });
  });

  it("does not open a chooser with one option in it", () => {
    // The server cannot currently send one — `StudioMatcher` fills Choices at exactly two or not at
    // all — so this pins the cap on the client's side of the wire rather than inheriting it. A
    // one-option chooser is a control with nothing to choose, and the rule is "exactly two", not "some".
    expect(
      studioProposal(proposalOf({ studioName: null, studioChoices: [studio("Harbour Lights", "harbourlights.com")], studioMatchCount: 1 })).kind,
    ).not.toBe("choose");
  });

  it("says nothing when the torrent named no studio the library has", () => {
    // The state that must stay silent: nothing could have been offered, so there is nothing to report.
    expect(studioProposal(proposalOf({ studioName: null, studioChoices: [], studioMatchCount: 0 })))
      .toEqual({ kind: "none" });
  });
});

describe("the studio choice in an apply request", () => {
  const options = [studio2("Harbour Lights", "harbourlights.com"), studio2("Pier House", "pierhouse.com")];
  const ambiguous = proposalOf({ studioName: null, studioChoices: options, studioMatchCount: 2 });

  it("sends nothing when the reviewer leaves it on None", () => {
    // The default, and it has to be a real no-op: an unsent studio leaves the video's own alone.
    const request = buildApplyRequest({
      proposal: ambiguous,
      fields: buildFields(ambiguous),
      selection: select({ studio: null }),
      importCover: false,
    });
    expect(request.studioName).toBeNull();
  });

  it("sends the studio the reviewer picked", () => {
    const request = buildApplyRequest({
      proposal: ambiguous,
      fields: buildFields(ambiguous),
      selection: select({ studio: "Pier House" }),
      importCover: false,
    });
    expect(request.studioName).toBe("Pier House");
  });

  it("does not start the choice made", () => {
    // Every other field pre-ticks when it fills a gap. This one must not: the server proposed nothing
    // deliberately, and picking for the reviewer is the guess the studio rule removed.
    expect(defaultSelection(ambiguous, buildFields(ambiguous)).studio).toBeNull();
  });

  it("draws no Studio field of its own while the choice is open", () => {
    // buildFields keys off `studioName`, which is null in this state — so the two can never both be on
    // screen, and a reviewer cannot tick a studio and pick a different one.
    expect(buildFields(ambiguous).map((field) => field.key)).not.toContain("studioName");
  });
});

describe("partitionTags", () => {
  const proposal = proposalOf({
    tags: [
      relation("Outdoor", { matchesExisting: true }),
      relation("Pier"),
      relation("Brunette", { matchesExisting: true, alreadyApplied: true }),
      relation("Handheld", { matchesExisting: true }),
    ],
  });

  it("separates what can be decided from what cannot", () => {
    const { toImport, onVideo } = partitionTags(proposal);

    expect(toImport.map((tag) => tag.name)).toEqual(["Outdoor", "Pier", "Handheld"]);
    expect(onVideo.map((tag) => tag.name)).toEqual(["Brunette"]);
  });

  it("keeps every importable tag, however long the list runs — the list is the point", () => {
    const long = proposalOf({ tags: Array.from({ length: 214 }, (_, index) => relation(`tag ${index}`)) });

    expect(partitionTags(long).toImport).toHaveLength(214);
  });

  it("holds the list order, so collapsing the inert half does not reshuffle the other", () => {
    expect(partitionTags(proposal).toImport[0].name).toBe("Outdoor");
  });
});

describe("countTags", () => {
  it("counts the importable remainder, and the created tags as a subset of it", () => {
    const counts = countTags(
      proposalOf({
        tags: [
          relation("Outdoor", { matchesExisting: true }),
          relation("Pier"),
          relation("Two Cam"),
          relation("Brunette", { matchesExisting: true, alreadyApplied: true }),
        ],
      }),
    );

    expect(counts).toEqual({ total: 4, onVideo: 1, toImport: 3, existing: 1, created: 2 });
  });

  it("reports a proposal with nothing left to give without going negative", () => {
    const counts = countTags(
      proposalOf({ tags: [relation("Brunette", { matchesExisting: true, alreadyApplied: true })] }),
    );

    expect(counts).toEqual({ total: 1, onVideo: 1, toImport: 0, existing: 0, created: 0 });
  });
});

describe("surprisingSource", () => {
  it("stays quiet when the source is the name with dots for spaces", () => {
    expect(surprisingSource(relation("Big Red Barn", { source: "big.red.barn" }))).toBeNull();
  });

  it("shows the source when normalising did something else to it", () => {
    expect(surprisingSource(relation("Older / Younger", { source: "older.younger" }))).toBe("older.younger");
  });

  it("says nothing about a tag that resolved to a row the library already holds", () => {
    expect(
      surprisingSource(relation("Older / Younger", { source: "older.younger", matchesExisting: true })),
    ).toBeNull();
  });

  it("says nothing about a tag already on the video", () => {
    expect(surprisingSource(relation("Older / Younger", { source: "older.younger", alreadyApplied: true }))).toBeNull();
  });

  it("says nothing when the torrent carried no source for it", () => {
    expect(surprisingSource(relation("Two Cam"))).toBeNull();
  });
});

describe("coverStartsOpen", () => {
  it("stays shut when the video already has artwork — the decision is a replacement, not a first fill", () => {
    expect(coverStartsOpen(proposalOf({ videoHasImage: true }))).toBe(false);
  });

  it("opens itself when there is nothing to compare against", () => {
    expect(coverStartsOpen(proposalOf({ videoHasImage: false }))).toBe(true);
  });

  it("stays shut when the cover would not be fetched anyway", () => {
    expect(coverStartsOpen(proposalOf({ videoHasImage: false, coverHostAllowed: false }))).toBe(false);
  });

  it("stays shut when the torrent carries no cover at all", () => {
    expect(coverStartsOpen(proposalOf({ videoHasImage: false, coverUrl: null }))).toBe(false);
  });
});

describe("filterTags", () => {
  const tags = [
    relation("outdoor"),
    relation("two cam", { source: "two.cam" }),
    relation("natural light"),
  ];

  it("is not a filter when nothing has been typed", () => {
    expect(filterTags(tags, "").map((tag) => tag.name)).toEqual(["outdoor", "two cam", "natural light"]);
    expect(filterTags(tags, "   ").map((tag) => tag.name)).toEqual(["outdoor", "two cam", "natural light"]);
  });

  it("matches part of a name, in any case", () => {
    expect(filterTags(tags, "DOOR").map((tag) => tag.name)).toEqual(["outdoor"]);
  });

  it("matches the torrent's own spelling, which is what the reviewer just read", () => {
    expect(filterTags(tags, "two.cam").map((tag) => tag.name)).toEqual(["two cam"]);
  });

  it("treats a dot, a hyphen, an underscore and a space as the same join", () => {
    // Cove's own tag search does not, and a dot or hyphen hides the tag there. A filter over
    // a thousand rows that repeated it would be unusable on exactly the list it exists for.
    for (const query of ["two cam", "two-cam", "two_cam", "two.cam"])
      expect(filterTags(tags, query).map((tag) => tag.name)).toEqual(["two cam"]);
  });

  it("answers with nothing rather than with everything when a query matches none", () => {
    expect(filterTags(tags, "zebra")).toEqual([]);
  });
});

describe("showsTagFilter", () => {
  it("stays away from the lists that already fit", () => {
    // Corpus median 37, p95 98 — a search box over those is chrome.
    expect(showsTagFilter(37)).toBe(false);
    expect(showsTagFilter(98)).toBe(false);
    expect(showsTagFilter(199)).toBe(false);
  });

  it("appears at the threshold DESIGN-DECISIONS named before the feature existed", () => {
    expect(showsTagFilter(200)).toBe(true);
    expect(showsTagFilter(1122)).toBe(true);
  });
});

describe("describeTagFilter", () => {
  const tags = [relation("outdoor"), relation("outdoor shower"), relation("poolside")];

  it("says nothing, and names no scope, while nothing is filtering", () => {
    const lines = describeTagFilter({ query: "", shown: tags, total: 3, selection: new Set() });

    expect(lines.count).toBeNull();
    expect(lines.selectAll).toBe("All tags");
    expect(lines.selectNone).toBe("No tags");
    expect(lines.hidden).toBeNull();
  });

  it("puts the scope in the buttons' own labels, not only in the code", () => {
    const shown = filterTags(tags, "outdoor");
    const lines = describeTagFilter({ query: "outdoor", shown, total: 1122, selection: new Set() });

    expect(lines.count).toBe("2 of 1,122 shown");
    expect(lines.selectAll).toBe("All 2 shown");
    expect(lines.selectNone).toBe("None of the 2 shown");
  });

  it("counts the ticks its own filter is hiding, and says apply still takes them", () => {
    const shown = filterTags(tags, "outdoor");
    const lines = describeTagFilter({
      query: "outdoor",
      shown,
      total: 3,
      selection: new Set(["outdoor", "poolside", "seaside"]),
    });

    expect(lines.hidden).toBe("2 selected tags are not shown by this filter. Apply still takes all 3.");
  });

  it("reads as one tag when it is one", () => {
    const lines = describeTagFilter({
      query: "outdoor",
      shown: filterTags(tags, "outdoor"),
      total: 3,
      selection: new Set(["poolside"]),
    });

    expect(lines.hidden).toBe("1 selected tag is not shown by this filter. Apply still takes all 1.");
  });

  it("does not warn about hidden ticks when the filter is hiding none", () => {
    const lines = describeTagFilter({
      query: "outdoor",
      shown: filterTags(tags, "outdoor"),
      total: 3,
      selection: new Set(["outdoor"]),
    });

    expect(lines.hidden).toBeNull();
  });

  it("names no scope when nothing matched, rather than offering to sweep zero rows", () => {
    const lines = describeTagFilter({ query: " zebra ", shown: [], total: 3, selection: new Set(["outdoor"]) });

    expect(lines.count).toBe("Nothing matches “zebra”");
    expect(lines.selectAll).toBe("All shown");
    expect(lines.selectNone).toBe("None shown");
    // Still true, and still the thing an apply would take.
    expect(lines.hidden).toBe("1 selected tag is not shown by this filter. Apply still takes all 1.");
  });
});

describe("filterRows", () => {
  const rows = [
    row({ torrentName: "harbour.lights.04.1080p", fileName: "scene2.mp4", videoTitle: "Harbour Lights 04 — Scene 2" }),
    row({ torrentName: "quarry.nights.02", fileName: "s01.mp4", videoTitle: "Quarry Nights 02 — Scene 1" }),
    row({ torrentName: "winter.set.2019", fileName: "disc2/03.mp4", videoTitle: null }),
  ];

  it("is not a filter when nothing has been typed", () => {
    expect(filterRows(rows, "")).toHaveLength(3);
  });

  it("finds a release by the name the library gave the video", () => {
    expect(filterRows(rows, "quarry").map((r) => r.fileName)).toEqual(["s01.mp4"]);
  });

  it("finds it by the torrent's name, joined however the uploader joined it", () => {
    expect(filterRows(rows, "harbour lights").map((r) => r.fileName)).toEqual(["scene2.mp4"]);
  });

  it("searches the file too, which is the other half of what the row shows", () => {
    expect(filterRows(rows, "disc2").map((r) => r.torrentName)).toEqual(["winter.set.2019"]);
  });

  it("does not fall over on a row the library has not titled", () => {
    expect(filterRows(rows, "winter").map((r) => r.torrentName)).toEqual(["winter.set.2019"]);
  });
});

describe("describeRowFilter", () => {
  it("says nothing while the list is whole", () => {
    expect(describeRowFilter(715, 715)).toBeNull();
  });

  it("names its unit, because this page counts three different things", () => {
    // Rows, videos and torrent video files all appear on this page, and reporting one as another is
    // how a count stops meaning anything.
    expect(describeRowFilter(51, 715)).toBe("51 of 715 rows shown");
    expect(describeRowFilter(4, 1122)).toBe("4 of 1,122 rows shown");
  });
});

describe("the wording the review window uses", () => {
  it("calls an inert relation what it is, whatever else is true of it", () => {
    expect(relationBadge({ alreadyApplied: true, matchesExisting: true })).toBe("on video");
    expect(relationBadge({ alreadyApplied: true, matchesExisting: false })).toBe("on video");
  });

  it("separates a tag that reuses a row from one that would create it", () => {
    expect(relationBadge({ alreadyApplied: false, matchesExisting: true })).toBe("existing");
    expect(relationBadge({ alreadyApplied: false, matchesExisting: false })).toBe("new");
  });

  it("titles the window after the video, never after the title under review", () => {
    // The heading used to be the torrent's proposed title — a claim one of the checkboxes below was
    // asking the reviewer to accept.
    expect(videoDisplayName(proposalOf({ currentTitle: "Harbour Lights 04", title: "Proposed" })))
      .toBe("Harbour Lights 04");
  });

  it("names an untitled video by its id rather than leaving the heading empty", () => {
    expect(videoDisplayName(proposalOf({ videoId: 47, currentTitle: null }))).toBe("Video 47");
  });

  it("names the matched file, because a pack's rows differ by nothing else", () => {
    // Two rows of one torrent: same name, same fan-out warning, same heading in a library whose
    // rows are untitled. The file is the whole of what separates them.
    expect(matchedFileLabel(proposalOf({
      torrentName: "Harbour Lights - Season One",
      fileName: "harbour.lights.s01e02.mp4",
    }))).toBe("harbour.lights.s01e02.mp4");
  });

  it("says nothing when the torrent is named after its own file", () => {
    // The single-file case, where naming it spends a line repeating the line above it.
    expect(matchedFileLabel(proposalOf({ torrentName: "HL-204", fileName: "HL-204.mp4" }))).toBeNull();
    expect(matchedFileLabel(proposalOf({ torrentName: "HL-204.mp4", fileName: "HL-204.mp4" }))).toBeNull();
  });

  it("treats a case difference as the same name, since that is how the repetition arrives", () => {
    expect(matchedFileLabel(proposalOf({ torrentName: "hl-204", fileName: "HL-204.mkv" }))).toBeNull();
  });

  it("keeps a file whose stem merely starts with the torrent's name", () => {
    // Prefix containment is not repetition: the suffix is the part that identifies the scene.
    expect(matchedFileLabel(proposalOf({ torrentName: "HL-204", fileName: "HL-204.trailer.mp4" })))
      .toBe("HL-204.trailer.mp4");
  });

  it("says nothing when there is no file to name", () => {
    expect(matchedFileLabel(proposalOf({ fileName: "" }))).toBeNull();
    expect(matchedFileLabel(proposalOf({ fileName: "   " }))).toBeNull();
  });

  it("offers what is left rather than repeating an apply already made", () => {
    expect(applyButtonLabel(false)).toBe("Apply selected");
    expect(applyButtonLabel(true)).toBe("Apply again");
  });

  it("names the host a cover would come from", () => {
    expect(coverHost("https://images.example/covers/1.jpg")).toBe("images.example");
  });

  it("answers null for a URL nothing has promised is one", () => {
    expect(coverHost(null)).toBeNull();
    expect(coverHost("")).toBeNull();
    expect(coverHost("not a url")).toBeNull();
  });
});

describe("scopeRows", () => {
  const rows = [
    row({ torrentName: "winter.set", fileName: "01.mp4", fanOut: 128, videoTitle: "Winter Set — Scene 1" }),
    row({ torrentName: "winter.set", fileName: "02.mp4", fanOut: 128, videoTitle: "Winter Set — Scene 2" }),
    row({ torrentName: "winter.set.2", fileName: "01.mp4", fanOut: 64, videoTitle: "Winter Set 2 — Scene 1" }),
    row({ torrentName: "harbour.lights.04", fileName: "s2.mp4", videoTitle: "Harbour Lights 04" }),
  ];

  it("leaves the list whole when nothing is scoping it", () => {
    expect(scopeRows(rows, wholeList)).toHaveLength(4);
  });

  it("keeps only the rows whose torrent covers more than one file", () => {
    expect(scopeRows(rows, { ...wholeList, packsOnly: true }).map((r) => r.torrentName)).toEqual([
      "winter.set",
      "winter.set",
      "winter.set.2",
    ]);
  });

  it("matches a focused pack exactly, so a longer name is a different release", () => {
    // A query would drag `winter.set.2` in, and the progress line beside it would then be counting a
    // set the reviewer did not choose.
    expect(scopeRows(rows, { ...wholeList, pack: "winter.set" }).map((r) => r.fileName)).toEqual([
      "01.mp4",
      "02.mp4",
    ]);
  });

  it("narrows a focused pack further when a query is typed as well", () => {
    const scoped = scopeRows(rows, { ...wholeList, pack: "winter.set", query: "scene 2" });

    expect(scoped.map((r) => r.fileName)).toEqual(["02.mp4"]);
  });
});

describe("packFocusSummary", () => {
  const pack = (over: Partial<BatchRow>) => row({ torrentName: "winter.set", fanOut: 128, ...over });

  it("names both units, because a fan-out is files in the torrent and a row is one we can act on", () => {
    // 128 is `TorrentRelease.Videos.Count`; three of those files are in this library. Reporting the
    // first as the second is how a count stops meaning anything.
    const rows = [
      pack({ fileName: "01.mp4", status: "matched" }),
      pack({ fileName: "02.mp4", status: "matched" }),
      pack({ fileName: "03.mp4", status: "matched" }),
    ];

    expect(packFocusSummary(rows, "winter.set")).toBe(
      "3 of this torrent's 128 video files are in your library · 3 to apply",
    );
  });

  it("keeps applied and re-tagged apart, as the page's own summary line does", () => {
    const rows = [
      pack({ fileName: "01.mp4", status: "applied" }),
      pack({ fileName: "02.mp4", status: "updated" }),
      pack({ fileName: "03.mp4", status: "matched" }),
    ];

    expect(packFocusSummary(rows, "winter.set")).toBe(
      "3 of this torrent's 128 video files are in your library · 1 to apply · 1 applied · 1 with new tags",
    );
  });

  it("omits a bucket at zero rather than reporting something that did not happen", () => {
    const rows = [pack({ fileName: "01.mp4", status: "applied" }), pack({ fileName: "02.mp4", status: "applied" })];

    expect(packFocusSummary(rows, "winter.set")).toBe(
      "2 of this torrent's 128 video files are in your library · 2 applied",
    );
  });

  it("counts only the rows of the torrent it was asked about", () => {
    const rows = [pack({ fileName: "01.mp4" }), row({ torrentName: "other", fileName: "x.mp4" })];

    expect(packFocusSummary(rows, "winter.set")).toContain("1 of this torrent's 128");
  });

  it("groups the thousands, because a pack can hold 1,913 files", () => {
    const rows = [pack({ fileName: "01.mp4", fanOut: 1913, status: "applied" })];

    expect(packFocusSummary(rows, "winter.set")).toBe(
      "1 of this torrent's 1,913 video files are in your library · 1 applied",
    );
  });
});

describe("emptyScopeMessage", () => {
  it("says nothing while the list is whole, so the page's own counts explain it", () => {
    expect(emptyScopeMessage(wholeList)).toBeNull();
  });

  it("names which of the three is hiding everything", () => {
    // Clearing the wrong one is a wasted move, and an empty scope looks exactly like a library with
    // nothing left to do.
    expect(emptyScopeMessage({ ...wholeList, pack: "winter.set" })).toBe("No rows from this torrent are on the page.");
    expect(emptyScopeMessage({ ...wholeList, query: " harbour " })).toBe("No rows match “harbour”.");
    expect(emptyScopeMessage({ ...wholeList, packsOnly: true })).toBe("No packs on this page.");
  });

  it("answers for the narrowest scope first", () => {
    const both = { query: "harbour", packsOnly: true, pack: "winter.set" };

    expect(emptyScopeMessage(both)).toBe("No rows from this torrent are on the page.");
  });
});

describe("planApply", () => {
  const rows = [
    row({ torrentName: "a", fileName: "a-0.mp4", videoId: 1 }),
    row({ torrentName: "b", fileName: "b-0.mp4", videoId: 2, fanOut: 40 }),
    row({ torrentName: "c", fileName: "c-0.mp4", videoId: 3, status: "applied" }),
  ];
  const keys = (plan: ReturnType<typeof planApply>) => plan.rows.map((r) => r.torrentName);

  it("sweeps every eligible row on screen when nothing is ticked", () => {
    const plan = planApply({ all: rows, visible: rows, selected: new Set(), includePacks: false });

    expect(keys(plan)).toEqual(["a"]);
    expect(plan.chosen).toBe(false);
    expect(plan.label).toBe("Apply to 1");
    expect(plan.hidden).toBeNull();
  });

  it("still holds packs back from a sweep, which has named nothing", () => {
    expect(keys(planApply({ all: rows, visible: rows, selected: new Set(), includePacks: true }))).toEqual(["a", "b"]);
  });

  it("takes a ticked pack, because ticking it is the consent the flag asks for", () => {
    const plan = planApply({ all: rows, visible: rows, selected: new Set([rowKey(rows[1])]), includePacks: false });

    expect(keys(plan)).toEqual(["b"]);
    expect(plan.label).toBe("Apply to 1 selected");
    expect(plan.chosen).toBe(true);
  });

  it("never takes a ticked row that has nothing left to apply", () => {
    const plan = planApply({ all: rows, visible: rows, selected: new Set(["c/c-0.mp4"]), includePacks: true });

    expect(plan.rows).toEqual([]);
  });

  it("keeps a ticked row a filter is hiding, and says how many", () => {
    // The tick is the reviewer's statement; the filter is a view. The same rule the tag filter follows
    // — and the same obligation to count what it hides rather than let it be discovered after the run.
    const plan = planApply({
      all: rows,
      visible: [rows[0]],
      selected: new Set([rowKey(rows[0]), rowKey(rows[1])]),
      includePacks: false,
    });

    expect(keys(plan)).toEqual(["a", "b"]);
    expect(plan.hidden).toBe("1 selected row is not shown here. Apply still takes all 2.");
  });

  it("says nothing about hidden rows when the view is showing them all", () => {
    const plan = planApply({ all: rows, visible: rows, selected: new Set(["a/a-0.mp4"]), includePacks: false });

    expect(plan.hidden).toBeNull();
  });

  it("returns the rows themselves, so the label and the request cannot come from different lists", () => {
    const plan = planApply({ all: rows, visible: rows, selected: new Set([rowKey(rows[0]), rowKey(rows[1])]), includePacks: false });

    expect(plan.label).toBe(`Apply to ${plan.rows.length} selected`);
  });
});

describe("sweepRows", () => {
  const rows = [
    row({ torrentName: "a", fileName: "a-0.mp4", videoId: 1 }),
    row({ torrentName: "b", fileName: "b-0.mp4", videoId: 2, fanOut: 40 }),
    row({ torrentName: "c", fileName: "c-0.mp4", videoId: 3, status: "applied" }),
  ];

  it("ticks the rows shown, packs included", () => {
    expect([...sweepRows(new Set(), rows, true)]).toEqual([rowKey(rows[0]), rowKey(rows[1])]);
  });

  it("leaves out a row with nothing left to apply", () => {
    expect([...sweepRows(new Set(), rows, true)]).not.toContain(rowKey(rows[2]));
  });

  it("clears only what is shown, so a tick behind the filter survives", () => {
    // "behind the filter" is any key the shown list does not hold — spelled literally, so the test
    // does not depend on how a key is built.
    const kept = sweepRows(new Set([rowKey(rows[0]), "off-screen"]), [rows[0]], false);

    expect([...kept]).toEqual(["off-screen"]);
  });

  it("names its own scope, the way the tag sweep does", () => {
    expect(describeRowSweep(37, false)).toBe("Select the 37 rows shown");
    expect(describeRowSweep(37, true)).toBe("Clear the 37 rows shown");
  });
});

describe("the confirm dialog on a run that contains packs", () => {
  const packRow = (over = {}) => row({ fanOut: 40, tagsToAdd: 3, ...over });

  it("says so, because a ticked pack never lit the page's own warning", () => {
    const scale = summariseApply([packRow({ fileName: "1.mp4" })], { createNewTags: false, importCovers: false });

    expect(describeApplyScale(scale, { createNewTags: false, importCovers: false })).toContain(
      "1 row is a pack, whose tag list describes a whole release rather than one scene.",
    );
  });

  it("counts them, and reads as plural when it should", () => {
    const scale = summariseApply(
      [packRow({ fileName: "1.mp4" }), packRow({ fileName: "2.mp4" }), row({ fileName: "3.mp4", tagsToAdd: 2 })],
      { createNewTags: false, importCovers: false },
    );

    expect(scale.packs).toBe(2);
    expect(describeApplyScale(scale, { createNewTags: false, importCovers: false })).toContain("2 rows are packs");
  });

  it("stays quiet on a run of single scenes", () => {
    const scale = summariseApply([row({ tagsToAdd: 2 })], { createNewTags: false, importCovers: false });

    expect(describeApplyScale(scale, { createNewTags: false, importCovers: false })).not.toContain("pack");
  });
});
