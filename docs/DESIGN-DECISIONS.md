# Design decisions

Why the extension is shaped the way it is. Each of these was a real fork in the road; the reasoning
matters more than the outcome, because reversing one without knowing why is how the bugs come back.

## Why not `IScraperProvider`

The scraper route looks like the natural fit and was the original plan. What stops it is not that the
path is missing but that it is **lossy in the middle**, and the loss is exactly the input this
extension needs. Re-checked 2026-08-24 against 1.3.0 and against `main`; the code is identical in
both, and one of the original five reasons no longer holds.

A fragment scrape is reachable: `MediaScrapeDialog` offers a `fragment` input kind and submits
through `scrapeAttempts.create`, and `ScrapeAttemptService.BuildFragmentInputAsync` enriches the
fragment with `phash`/`oshash`/`md5` read from the video's `VideoFiles.Fingerprints`. Then
`ScraperService.BuildVideoInput` maps that dictionary onto `VideoScrapeInput` and keeps seven keys:
`Url`, `Urls`, `Title`, `Code`, `Date`, `Details`, `Director`. The fingerprints it was just handed
are not among them. `Files` is never populated, `LocalVideoId` is never set, `VideoScrapeFile` is
constructed nowhere in the codebase, and `IScraperHost.GetVideoAsync` — the interface method that
would hand a scraper the video — is declared in `Cove.Plugins` with no implementation anywhere. The
one caller-side leftover is `client.ts`'s `scrapeFragment`, which nothing in the UI calls.

This extension matches on **exact file size**, so a scraper that cannot see the video's files has
nothing to match on. Building on that path would have meant either shipping against a Cove that does
not exist yet, or matching on the title — which is the guess this design exists to avoid.

`IActionExtension` + `IApiExtension` + `IUIExtension` keeps it installable against stock Cove with
**zero core changes**. The cost — rebuilding the review UI — was smaller than it looked, since a
fragment scrape's own dialog is a JSON textarea rather than a review surface.

## Matching is by exact file size, per video file

Size is exact and already indexed on `VideoFile.Size`, so a match needs no search, no fuzzy scoring
and no user-facing candidate picker.

**Size is not unique, and the original justification here was wrong.** This section used to read
"unique in practice — 54/54 distinct in the corpus", which was an artefact of a 54-torrent sample.
Measured across 3,218 torrents and 139,141 video files: **2.32% of sizes are
shared by more than one video file** — 3,152 sizes, 6,717 files — and the worst single size carries
**35** files. Against the real library, 694 of 878 files match a corpus size and **20 match more
than one torrent**.

What keeps size the right key is *what those collisions are*. Two files with the same byte count are
almost always the same encode indexed twice — a scene that also sits inside a siterip or a megapack —
not two different videos that happen to agree. Unrelated content landing on the same byte at
10⁸–10¹⁰ scale effectively does not happen, which is why this stays a lookup rather than a search.

So disambiguation is one deterministic rule instead of the candidate-picking machinery core uses:
`TorrentIndex.Find` prefers the **lowest fan-out**, i.e. the most single-scene source. On all 20
library collisions that chose the single-scene torrent, and in 20 of 20 its filename was exactly the
library file's; the rejected candidates were packs at fan-out 189, 746 and 1678.

**The residual risk is equal fan-out**, where the pick is arbitrary — 8 cases corpus-wide, none
touching the library. That is the one place this can silently choose wrong, and it is the reason the
tiebreak must not be deleted as redundant: it is load-bearing, not defensive.

**One index row per video file, not per torrent.** Indexing by "the largest video" would let a
50-video pack match exactly one video. Per-file indexing is what lets a pack match each of its scenes
independently, and makes partial completion expressible.

### The name is a fallback, and it is fenced rather than trusted

`TorrentIndex.Find` consults the **basename** where the size finds nothing — the file that was remuxed
or renamed and kept its name. That is a heuristic inside a section arguing against heuristics, so it
is worth being explicit about why it survives and what stops it spreading.

It survives because the corpus says the filename is a strong signal in this domain, not a coincidence:
in **20 of 20** library collisions the winning torrent's filename was exactly the library file's. It
is fenced by three things. An exact size match always wins, so a name collision can never override
one. The proposal reports `matched on file name`, so a reviewer is never told a heuristic was a byte
count. And **the batch overview does not do it** — it matches on size alone and builds no rows from
names, because a name match is something a reviewer accepts for one video in front of them, not
something a hundred-row bulk apply sweeps up.

That last fence is what created the gap the name-match fallback closed. The two directions disagreed, and the
disagreement was invisible: a video whose file the size missed but the name would find counted as
plain `unmatched` on the batch page, while its own dialog offered the torrent. `unmatched` was
therefore two answers wearing one number — "you never downloaded this", which is almost all of it and
which nothing can be done about, and "you have this, and the metadata is one click away" — so the
page now reports the second separately.

Two details that are decisions rather than mechanics. It counts **videos**, not video files: it is the
one figure on that line not in torrent-file units, because a pack naming its files `01.mp4` would
otherwise inflate it into noise, and because a video holding a size match on one file and a name match
on another is simply matched. And it stops at a count. Rows are affordable here in a way the review dialog's were
not — the near-miss universe is bounded by the library, at most 184 of 878 files — but the count is
also the measurement nobody had, so it comes first and rows are a decision to take once the number is
visible on real data.

## The video page always opens the drop zone

It never auto-matches. A video pulled out of a megapack **will** match that pack by file size, so
silently using it hands the user the pack's union metadata at precisely the moment they went and found
the individual scene's torrent. The dropped file wins; anything already indexed is offered as a
clearly-labelled second choice.

A dropped torrent is **pinned by name**: the search never leaves it, so nothing already indexed can
win over the file the user went and found. Which file *inside* it the proposal describes is chosen by
size, because the drop zone knows what it uploaded but not which scene of a pack this video is —
naming one anyway is how a pack's opening scene got handed to a video from its middle. A caller
that does know may still name the file, and that choice is honoured over the size preference.

Either way the result is labelled honestly: `matched on file size` when bytes agree, `matched on your
selection` when they don't. An intentional override must never masquerade as a verified match.

**The drop is committed before the review is.** `ingest` uploads, the server writes the file and
rebuilds the index, and only then is a proposal fetched — so the torrent is on disk and matchable
before the dialog has shown a single tag, and closing without applying leaves it there, matching that
video from then on.

Keeping it is the right outcome and not an oversight. The file is a perfectly good torrent for other
videos and for this one later, and making Cancel delete it would turn "no thanks, not this metadata"
into a destructive act nobody asked for — the same argument that keeps source folders read-only, one
scope down. What was wrong was that nothing said so, which is a surprise a user meets by accident and
then cannot explain. The dialog now states the ordering where the drop happens, since nobody reads
Settings before dragging a file, and the settings panel says it again beside the list that can undo it
.

## No control inside the review re-fetches the review

The naming style is chosen on Cove's Settings page, not in the dialog, and the dialog only names the
style in effect. It was a dropdown in the dialog's header, and changing it re-fetched the proposal —
correctly, because the new spelling changes which tags resolve to existing rows, so the badges have
to come from the server to stay truthful. Both callers implemented "re-fetch" as unmount and reopen,
which discarded every tick the reviewer had made. On a single-scene torrent that reads as everything
silently re-selecting itself.

The fix is the control's location, not the reload: a setting chosen before a review starts cannot
destroy one. Preserving selection across a remount would have kept a control in the dialog that has
no business being there.

**The cover-host allowlist stays in the dialog, and does not re-fetch.** It belongs there for the
opposite reason — the list ships empty, so the moment a user needs it is the moment they are looking
at a cover that will not import. It used to call the same reload and so destroyed the review
just as thoroughly, which was the half of that defect the report did not mention. It now tells the dialog
what it already knows: `coverHostAllowed` is the only thing about a proposal that allowing a host
changes, and the request that just succeeded is proof of the new value. The server remains the
authority at apply time; the flag only decides what is offered.

The rule that falls out, and the one worth keeping: **a control that changes what the server would
propose does not belong inside a review of that proposal.** Everything left in the dialog either
changes nothing server-side, or is knowable without asking.

**Apply is the exception the rule is shaped around, not a violation of it.** The dialog re-reads the
proposal after a successful apply and re-seeds itself from it. Nothing is thrown away by that,
because the selection it would have destroyed has just been spent — and the alternative is worse than
untidy: the review would go on describing a video that no longer exists, listing tags it had itself
just written as still waiting to be written. That mattered little while applying also closed
the dialog; the review queue keeps it open, which is what turned a hidden staleness into the main
thing on screen. The receipt for the apply sits at the top of the body for the same reason, rather
than in the footer where the first version put it.

Two host details this depends on. A settings panel carries **no** `RequiredPermission` — unlike a
page — so the host renders it for anyone who can open Settings while its endpoints stay behind
`videos:scrape`; the panel therefore has to show a load failure rather than swallow it, or it is an
empty box to exactly the users who cannot use it. And the stylesheet is injected by whichever of our
components mounts first, which on the Settings page is only ever the panel — it calls `ensureStyles`
itself for that reason.

## Only the inert half of the review collapses

The review dialog spent its first ~380px on the cover comparison — one yes/no — and put the first of
its thirty-odd tag checkboxes about 150px below the fold, on a 920px modal at `88vh`. Measured from
the stylesheet at the corpus median, the body held ~1,083px of content in a ~628px window, and every
one of the things above the fold was worth a single decision.

What collapses is decided by one rule: **content that cannot be acted on**. A tag already on the
video renders with its checkbox disabled — it cannot be ticked or unticked, and the section header
already states how many there are — so it is counted and available rather than listed. The cover
comparison collapses to two thumbnails and a *Compare* button, because the comparison is evidence and
the checkbox beside it is the decision. A field that merely fills an empty value loses its "current:
(nothing)" line.

**Nothing that can be imported is ever hidden.** The tag list renders every importable tag, however
long it runs; if that outgrows the window, the window scrolls. Importing tags is what this extension
is for, so a list that folds part of itself behind a "more" link would be hiding the product.

**A filter is not that, and it arrives at 200 importable tags** — the length this file named before
the feature existed, and the case measured then: median 37, p95 98, and a corpus torrent carrying 1,122
content tags that does describe a file in the test library. The list still never collapses and never
paginates; a filter changes what is *shown*, and three rules keep that from being the same thing as
hiding:

- **Both sweeps are scoped to what is shown, and their labels say so** — *All 14 shown*, *None of the
  14 shown*. A sweep that reached the tags behind the filter would repeat an older mistake in a new place in either
  direction: ticking what cannot be seen, or clearing decisions already made about it.
- **A tick survives the filter that hid it**, because the selection is a set of names and the filter
  only decides what renders. That is the one way this could apply something the reviewer cannot see,
  so it is counted out loud: *9 selected tags are not shown by this filter. Apply still takes all 10.*
- **Escape backs out of the filter first**, and only reaches the frame's own dismissal once there is
  nothing to back out of.

There is no grouping, and that is a decision rather than an omission: the only axis the data offers is
existing versus would-be-created, and every row already wears it as a badge.

Two decisions never collapse and never start ticked: a field that would **replace** a curated value,
which is the only thing in the window that can destroy something, and a pack's tag selection, which
starts empty by design.

The heading is the video's own title, not the torrent's proposed one. It used to be the proposal's —
so the window was named after a claim one of its own checkboxes was asking the reviewer to accept.

## The review queue is a snapshot, and it records rows rather than statuses

Reviewing rows one at a time meant returning to the batch page between each one. The dialog now walks
the rows itself, and two properties of that walk are load-bearing:

**The queue is frozen when it starts.** Re-deriving it from a refreshed overview would refetch the
whole list after every apply — for a table nobody can see, because it is behind the dialog's own
backdrop — and, with *Hide applied* on, would delete the row being reviewed out from under the index.
One refresh happens on close, and only if something was applied.

**It records that a row was applied, never what the row became.** Whether a row then reads `applied`
or `updated` is derived by `TorrentBatchService` from the torrent's own tag count.
Computing that in the browser to patch a snapshot would put a second copy of that rule in the repo,
and the copy that drifts is never the one deciding what the user sees. Asking the server once is
cheaper than being subtly wrong.

The queue walks the rows **visible** on the page, filters included — not the bulk-apply eligible set,
which excludes packs, because a pack is precisely the row that has to be judged one scene at a time.
Stepping remounts the dialog on the `(torrent, file)` key rather than swapping its proposal, since
the selection is seeded at mount: a swap would carry one video's ticks into the next, and that
selection is what decides what the server is allowed to change.

Both properties were written when the list spent the whole walk behind a backdrop. Beside the review
it is reachable, and two things follow. **A filter re-anchors the walk rather than reindexing it**: the
position follows the row being reviewed, the applied record survives — a filter does not undo an act —
and a row the filter has hidden leaves a walk of one, which is what an off-list row has always
produced. The review is never closed or swapped by a filter. **A row clicked in the list jumps rather
than starting a new walk**, for the same reason: a new walk discards the record the close refresh and
the run summary are both built from.

## The review has two frames, and only the frame differs

The batch page used to review a row by throwing a modal over its own table, so the list the reviewer
was working through was behind a backdrop for the whole walk. `MatchDialog` was the backdrop, the box
and every piece of the review in one file, which is why there was no other option.

It is a review plus a frame now. **`ReviewBody` holds all of it** — selection, cover import, host
allowed, busy, the apply receipt, the disclosure, the tag filter — and renders a header, a body and a
footer with nothing around them. **A shell owns position and dismissal, and nothing else.**
`MatchDialog` keeps its name and is the modal, still used by the entity action on a video's page,
where there is no list to sit beside. `ReviewPane` is the batch page's.

The test that the split is honest is that **a shell holds no state**. If a piece has to move up into
one, it belonged to the review. The dimming while the queue steps moved from `.tm-modal.is-stepping
.tm-body` to `.tm-body.is-stepping` for exactly that reason: the frame had been reading the pager to
draw itself.

**The pane is sticky rather than a fixed two-pane frame.** A frame wants a height and the host's
chrome is not ours to measure — the page is a component Cove renders inside its own layout, and the
route contract
is a standing reminder of how much of that surface is guesswork. So the list scrolls with the page and
the pane stays beside it, bounded by the viewport. Under 1100px there is no room for both and the
stylesheet turns the same element into a sheet over the page: one DOM, two presentations, no width
measured in JavaScript and no third component. It is not `aria-modal` even then, because it traps no
focus, and saying otherwise would be the lie.

**It is not a second view, and there is no mode to toggle.** The page has three states — no review
open, which is the table exactly as it was; a review open, which narrows the table into a list; and a
narrow window, which is the sheet. Closing restores everything. A toggle would imply the two hold
different data, and would need a preference, an empty-pane state and a control on the page's busiest
strip. What the walk gets instead is a second door: *Review one by one* opens the first visible row,
because bulk applying and reviewing scene by scene are two sittings and one of them should not begin
by hunting for a row to start on.

**What the list keeps is what picks the next row.** The video leads and the torrent is muted beneath
it, because a list beside a review should say what the review's own header says. One thumbnail,
and it is the library's: the torrent's cover is a *comparison*, it happens in the pane at a size where
that means something, and every one of them costs a paced request through the proxy while the
library's is local. The status pill appears only when it says something — `matched` is
every row on the page by default. The current-tags column and the second name line go for the width,
and all of it comes back when the review closes.

**A row this walk applied is marked, and the mark is an act rather than a status.** It comes from
`queue.applied`, which the browser owns. Whether the row now reads `applied` or `updated` is
`TorrentBatchService`'s rule, so the pill does not move until the refresh on close asks the
server. Re-deriving it here to patch a snapshot is the thing this repo keeps refusing to do, and with
the table on screen it would be refused in front of the user.

## The cover-host list is edited where it can be read, and normalised only on the server

The dialog's notice appends the one host in front of the reviewer, which is right for the moment it
serves and wrong as the only way in: every path into the list was an append, so a host added by
mistake, or one that normalised into something unintended, could not be corrected without editing
`extension_data` by hand. The panel lists, adds and removes; the notice stays as the shortcut.

**The panel sends what was typed and renders what comes back.** `CoverHostSetting.Normalise` reduces
an entry to a bare host — cutting a scheme, a port, a path, a trailing dot — and `Clean`
de-duplicates case-insensitively, both on every write. Normalising in the browser as well would put a
second copy of that rule in the repo, and the copy that drifts is not the one deciding whether a
fetch is allowed.

The cost is that a rejected entry and an accepted one look identical from the client: both answer 200
with a list. `coverHosts.ts` compares the list before and after to tell them apart, and the
unchanged case is reported ambiguously on purpose — a submission that collapses onto an existing
entry and one that normalises to nothing cannot be told apart from here, and "already listed" on a
typo tells the user they have done something they have not.

Removal is a restriction, so it needs no ceremony: it stops future fetches and touches nothing
already imported, because `CoverCache` keeps mapping stored URLs to the blobs the library now owns.
The panel says so, since a control that looks destructive should say what it does not destroy.

## Packs are excluded from bulk apply by default

A pack's tag list is the **union across every scene it contains**, so applying it wholesale tags each
video with the others' content. `FanOut > 1` rows are skipped unless explicitly included, and start
with nothing selected in the review dialog.

**Naming a row is that explicit inclusion.** `BatchApplyRequest.Rows` lists the rows to apply, and a
row listed there is applied whatever `IncludePacks` says; the flag guards the *sweep*, which names
nothing and therefore cannot have consented to anything. Filtering a named pack row out instead
is a request that reports success and writes nothing — the silent failure this repo keeps finding in
new places. The confirm dialog is where the consent is spelled out: a run carrying packs says so and
counts them, because a ticked pack never lit the page's own *Include packs* warning.

Status is derived from `VideoRemoteId` (endpoint `torrent-metadata`), not from moving or deleting `.torrent` files.
That survives renames, keeps files intact for seeding, and — because a pack maps to many videos — can
express *partial* completion, which a file-level flag cannot.

**Refusing them in bulk left packs with no workflow at all, and the pane is where they get one**
. Two scopes, both narrowing the list rather than adding data: *Packs only*, which is the sitting
bulk apply declines to serve, and one release's own rows, gathered from inside a review of one of them
— because that is where a pack is recognised. Since the walk is built from what is visible, scoping
the list is also how a walk gets aimed at one set.

The progress line over a focused pack counts **two different units and says so**. `FanOut` is
`TorrentRelease.Videos.Count` — video files inside the torrent — while a row exists only where one of
those files matches something in the library, so a fan-out 128 torrent with 40 rows is *40 of this
torrent's 128 video files are in your library*, and the applied figures are of the 40. "31 of 128"
would be false. It is also counted over every row of that torrent rather than over the page, because
the progress belongs to the pack: *Hide applied* must not make a set look less finished than it is.

## A studio comes from the library, and the library is also the tiebreak

Two rules, and the second was added after the first proved insufficient.

**A studio is linked, never created.** The tag list carries a bare lowercase domain — `lanternbay` once
the TLD is stripped — and the domain is not the studio's name. Studio names are a unique identity, so
creating `lanternbay` *claims* it and the user can no longer add "Lanternbay" properly afterwards: only a
merge undoes it. The rule was first argued when the same write was merely untidy — a curated library
acquiring lowercase near-duplicates — and the answer did not change when the cost went up.

**Which studio, when a torrent names several, is decided by the library.** `ExtractStudioCandidates`
returns every site tag; `StudioMatcher.Resolve` proposes one only when they agree on exactly one
studio the library already holds. Two studios, a name the library holds twice, or none — all propose
nothing.

The old rule was `FirstOrDefault`, and it was arbitrary in a way the corpus makes plain: **955 of
3,218 torrents (29.7%) carry two or more candidates**, the shape is almost always network plus imprint
(`lanternbay` + `lbgold`, `regattakings` + `rkcrew`), and the order tracks the uploader's title rather
than the release — the same pair appears both ways round on different torrents. So two releases of one
network resolved to two different studios.

Deferring to the library needs no new preference and invents nothing, which is why it beats the
obvious alternative of a deterministic tiebreak like shortest-domain: that is consistent and still
confidently wrong on a pack. Since a site tag matching nothing can never do anything anyway, the only
candidates that can matter are the ones the library holds.

**The megapack case falls out with no threshold of its own.** A torrent spanning seventeen sites hits
either none of them or several, and both answers are "propose nothing" — right, because a release
covering thirty-one scenes across seventeen sites has no studio. Same argument as excluding packs from
bulk apply.

**A studio that does not resolve is not proposed at all**, which is the half that was a live defect
rather than an ambiguity. The bare domain used to go into the proposal, `buildFields` rendered a Studio
row for it, `defaultSelection` pre-ticked it, and the apply's lookup then missed and did nothing
without saying so — `describeApplyResult` omits studio when `StudioChanged` is false. On a library that
does not already hold that studio, which is every library on a fresh install, that is a pre-ticked
control that cannot work, reporting success. The same failure two earlier defects were each about.

What it does *not* do is choose between two studios the user curates. That is a UI question with its
own issue, and proposing none is the correct interim rather than a placeholder.

## Performers are matched, never detected

Nothing about a name's shape distinguishes it from a content tag: `oil.slick`, `first.frost`,
`big.black.cock` and `casey.storm.chaser` all read as plausible names. Detection by pattern produces
constant false positives and no tuning fixes it.

Matching against known performers inverts the problem and makes it exact. The known set is
**every performer in the library**, not just those on the current video — for a pack, the narrower set
would leave dozens of other performers' names sitting in the tag list looking like content.

Entries that look like names but match nothing stay as tags, so a torrent can also help *populate* the
performer list rather than being blocked by its absence. This is why the external-metadata-first
workflow is a recommendation, not a precondition.

The corpus run measured the consequence: 101 performers resolved across 53 videos, 6 videos resolving
none, and **0 that would ever be created**. That was a property of how the matcher happened to behave —
a create path sat on the apply and simply never fired. It is now a property of there being no such
path: a match yields a performer *id*, the apply request carries ids, and there is no name in
it to invent a row from.

The prompt was Cove 1.3, which stops resolving performers by alias and cannot address a performer
carrying a disambiguation by a name-only request at all. Following that change by sending the
canonical name instead would have fixed half of it and left the disambiguated half creating
duplicates. Sending ids removes the question rather than answering it: the extension no longer asks
Cove what a name means, so which version is underneath stops mattering, `minCoveVersion` does not
move, and the guarantee is testable here without reproducing Postgres identity constraints.

## Classification precedes normalisation

Dots are usually word separators but not in `h.265`, `lanternbay.com`,
`2018.03.20`, `sammy.j` or `2.man.crew`. Protected shapes are matched first; only the remainder
gets dot-to-spaces.

The configured naming style (Title Case / spaces / dotted) applies **only to tags that would be
created**. A tag that resolves keeps the library's own spelling — the library is the authority on how
its tags are named.

## Technical tags are dropped, not grouped or configurable

Resolution, codec, container and frame rate — `1080p`, `x265.10bit`, `mp4`, `60.fps` — are routed
away rather than imported, and so is encoding provenance (`x265.reencode`, `ai.upscale`). All of
them describe the *file*, not the scene, and a library that accumulates them loses the ability to
filter on anything that describes the content.

The two justifications are not the same strength, which is worth knowing before reopening this.
`Resolution` and `CodecOrContainer` are fully derivable from `VideoFile`, so importing them
duplicates a fact Cove already holds. `SourceQuality` is not derivable — nothing in the file says it
was re-encoded — so it is dropped on the weaker "describes the file" argument alone. If any of this
is ever revisited, that kind is where the case is strongest.

Two alternatives were weighed and rejected. A setting is the worst of them:
it adds a configuration surface for a preference nobody has asked for, in an extension whose value
is restraint. `TagGroup` — a real Cove entity — is the shape this would take if the answer ever
changes, because grouping is exactly what answers the "cannot filter on anything meaningful"
objection. It stays cheap to add later: the classifier already identifies these as their own
`TorrentTagKind`, so routing them into a group is a destination change, not a reclassification.
Left demand-driven rather than built speculatively; the volume is about 135 tag applications across
the corpus either way.

## Accepted tags seed a `TagAlias`

The torrent's dotted spelling is recorded on whatever tag it resolved to, so later torrents match by
alias instead of re-running the normaliser and hoping it lands on the same string. With 2564 aliases
already in the test library, this compounds quickly.

**Which means matching has to ask for both spellings**, and it does. The alias is written as `Source`
(`deep.blue.sea`) while the classifier's `Value` is `deep blue sea`, so a lookup by `Value` alone
never finds it — the seeded row was written and then read by nothing. The same gap swallowed tags
created under the dotted naming style, which are *named* by their source: the extension offered its
own creations back as tags that would be created, and bulk apply with "existing tags only" declined
to apply them at all.

`TorrentMatchService` resolves with `Value` and `Source`, normalised form first, and
`TorrentBatchService.KnownSpelling` does the same for the vocabulary check. That second one returns
the spelling rather than a bool on purpose: the caller has to send a name the apply will resolve to
the same row, and sending the normalised form for a tag held only in source form would create a
second tag beside it rather than link the first.

## One proposed tag per name

Several tag-list entries can arrive at one name, most often because the library holds more than one
spelling of a tag as aliases. `TorrentMatchProposal.Tags` is deduplicated by name, case-insensitively,
keeping the first entry's source.

The comparer matches `ApplyTagsAsync`, which resolves case-insensitively — two rows the reviewer sees
as separate would be one tag on apply, so showing both offers a choice that does not exist. The
duplicates were otherwise two React rows sharing a key, sharing a checkbox (`selection.tags` is a set
of names), and inflating the header count above what would actually be written.

**The batch path follows the same rule, and did not always.** `TorrentBatchService` walked the
classification twice — once for the "would add" column and once to build the apply — and the second
walk deduplicated nothing, so its `TagSources` dictionary kept whatever spelling arrived *last*.
The two walks are now one (`Propose`), with the column and the apply reading its output, so the
agreement is structural rather than two edits staying in step.

**Only the first spelling survives, and that is a real if small loss.** `TagSources` is one source per
name at both ends of the wire — `Record<string, string>` in the request, a single `TryGetValue` per
tag in `ApplyTagsAsync` — so carrying every spelling would be a contract change on both sides, not a
different projection. Whether it is worth one is unanswerable here: it depends on how often two
entries carrying *genuinely distinct* new spellings collapse, which is a property of the library's
alias table, and a test in this repo builds its own empty database. A corpus canary can see the
tracker's tag lists but not the library that resolves them, so it would measure the wrong half.

## "Would add" is counted against the video, not the library

The batch table's headline number is what this torrent would add to *this video*, with the tags that
do not exist yet named as a subset of it. It used to count the torrent's tags against the library
vocabulary instead, which made it wrong in a way that read as a different bug: a video already
carrying every tag in its torrent still said "would add 43", and applying did not move the number,
because the created tags merely crossed from the "would be created" half into the "already in your
library" half and the total was unchanged. A row that had refreshed correctly looked stale.

The dialog was already right — its badges come from the proposal's per-relation `alreadyApplied` —
so the two disagreed about the same pair of things, which is how it was found.

Counting per video needs a **name-to-id** view of the vocabulary rather than the set of spellings it
was, because "does the library know this spelling" is no longer the question; "does this video carry
that particular tag" is. Both the tag names and the aliases map to the tag they belong to, names
first and inserted with `TryAdd`, so a primary-name match wins over an alias match — the precedence
`RelationNameResolver.ResolveTagsAsync` applies on the apply path, and the two have to agree or the
table and the dialog will disagree in a new way instead of the old one. Ties are broken by lowest id,
for the same reason the video tiebreak is: nothing else makes it deterministic.

The video's own tags come from `Video.TagIds`, the denormalised array Cove maintains on save, so this
is a wider projection of a read that was already happening rather than a join or a second query. The
set is built once per video and not once per row, which matters only because a pack asks the same
video the same question up to 1,913 times.

**The count is deduplicated by the name the apply would be sent** — the same key `ApplyTagsAsync`
folds on. Two tag-list entries reaching one name is what this extension's own alias seeding builds up
over time (see *One proposed tag per name*), and counting both would promise more than an apply
delivers. That is the same class of untruth the column was fixed for, so it would be odd to leave it
in the fix.

## A match says which of the two "no" answers it means

`MatchAsync` returns an outcome carrying a status, not a nullable proposal. Null could not
distinguish *this video does not exist* from *no indexed torrent describes it*, so the endpoint had
one sentence for both and told a user whose video had been deleted in another tab to go rescan a
torrent folder that was never the problem. Both are reachable from the shipping UI: a stale
detail page, a bookmarked action, an id held across a refresh.

The status is deliberately not a message. What to say about a missing video is the endpoint's
business — a service that returns user-facing prose acquires a second audience it cannot see, and the
apply endpoint already had the right words for this exact case.

`TorrentMatchOutcome`'s constructor is private and its three states come from factories, so a matched
outcome with no proposal in it is not expressible. That matters more than it looks: after this change
a failure is a non-null object, so a test that only checked nullability would pass on the wrong
outcome. The test helper asserts the status on the way through for the same reason.

## Torrents are read from the operator's folders and written only to ours

Sources are any number of folders the operator names, and the extension only ever *reads* them.
Torrents already live in the user's own folders, and asking them to duplicate a collection into ours
was the wrong ask — the folder used to be a fixed path under the Cove data root with no setting at
all, reachable only by moving `COVE_HOME`, which moves everything Cove owns.

Read-only turned out to cost nothing, which is why the shape is this one. Completion was never a
filesystem fact: `VideoRemoteId` per (video, torrent id) is the record, chosen so a pack can express
"12 of 47 applied", which no file-level flag can. Nothing ever needed to move, rename or tag a file to
know what had been done, so the obvious worry about giving up write access to the source turned out
not to exist.

**One folder stays ours, and it is the only place anything is written.** Uploads land there. It is not
in the source list and cannot be moved, because a user pointing a source at their torrent client's
watch directory is exactly the intended case, and dropping uploads into a directory something else is
watching is not. It is not for recording what was processed either — that would be a second source of
truth beside `VideoRemoteId`, which is an argument this document has already settled.

It is also **the only folder the UI ever names as a destination**. The empty batch page has to answer
"where do I put this file", and the honest answer is never a source: a source is read-only, may sit on
an unmounted drive, and usually belongs to something else — a torrent client's watch directory is the
intended case. So the empty state names ours, and says when it does not exist yet, which on a fresh
install it does not: the folder is created by the first upload.

**And it is the only folder anything can be deleted from.** The settings panel lists what is in it and
removes any of it. That is the mirror of the read-only rule rather than an exception to it: a
source folder holds files the operator manages, and this one holds files they handed us through our own
UI, which is the whole of the difference. Every name a removal gives is resolved against the folder and
refused if it lands outside, so the one part of this that arrives from a browser cannot address a source
folder.

Three things fell out of building it, and each is a decision rather than a detail.

**Removing an applied torrent destroys nothing.** The `VideoRemoteId` Cove stores and the baseline the
extension stores are keyed by (video, torrent id) and neither is touched, so re-adding the file restores
the row exactly — applied, with nothing left to apply. It is already what happens when someone deletes a
torrent from a source folder of their own. So applied torrents are listed and removable like anything
else: hiding them would make a file dropped by mistake and then applied the only unremovable thing in
the folder, and the premise that it needed protecting was wrong.

**The bulk button's label is its specification.** A "Remove all" beside a filter box could mean the
twelve on screen or the three thousand behind them, and rather than settle that in a tooltip the button
reads the filter and counts it — *Remove all 3182* with nothing typed, *Remove 12 matching "quay"* with
a filter. It acts on every match rather than the page being shown, which is only safe *because* the
count is in the label; the label and the request are therefore built from the same list. What was
refused is a bulk action with a *predicate* — "remove what matched nothing" was the obvious one and is
wrong, because 138,426 of 139,141 indexed files are for videos not in the library, so it would offer to
delete almost the whole folder. A filter the user typed carries an intent that a guess cannot.

**The wait is labelled, and a removal does not pay for it twice.** Reading and parsing the write
folder was measured at 1.06 s warm and 2.34 s cold over 3,272 torrents, against 8 ms for the stat
sweep that merely counts them — so the count arrives long before the list, and the panel says *Reading
3,182 torrents…* rather than *Loading…*. Parallelising the parse was measured too and rejected:
1.2×, for concurrency through the listing. And a removal no longer re-reads the folder, because
nothing about the rows that stay depends on the one that left; the rows are dropped locally, and the
re-read happens only when something was **refused**, which is precisely when the list the user acted
on disagreed with the folder.

**The listing reads the folder, not the index.** It has to: `TorrentIndexEntry` carries no path and no
source folder, so the index cannot say which of its torrents came from here, nor what any of them are
called on disk — and the filename is both the identity a removal names and the thing the user
recognises, because it is what they dragged in. The cost is one parse per file per listing. A file that
will not parse is listed rather than skipped, since it is the one entry here that can never do anything
useful and hiding it would hide the only removal that is unambiguously right.

**Reload stays manual, and the page says when one is owed.** The folders change when someone
downloads a torrent: rare, and deliberate. Rescanning re-reads and re-parses everything with no mtime
cache, and the measurement that was owed here has now been taken: over the real folder — 3,272
torrents, 528 MB — a stat-only sweep costs **8 ms warm and 37 ms cold**, against **0.28 s warm and
3.39 s cold** merely to read and hash the same files, before a byte of bencode is parsed.

**Detecting a change is two orders of magnitude cheaper than acting on one**, and that asymmetry is
what settled the shape. A `FileSystemWatcher` makes the cheap half cheaper and pays for it in
inotify's platform gaps — no events on `/mnt/*` under WSL, none over SMB or NFS, which is where a
torrent client's watch directory often lives — plus silent event loss on buffer overflow in exactly
the copy-500-files-at-once case, a watcher set to rebuild on every settings write, and a core no test
here can reach without a real filesystem and timing assertions. So the rebuild stays behind the
button, and a probe compares a fingerprint of each folder against the one taken at the last scan.

Three things that decide whether such a probe is honest, each of which the obvious version gets
wrong:

- **The fingerprint is the whole stat set, not a count and a newest mtime.** `cp -p`, `rsync -t` and
  most archive extractions preserve the source mtime, so a torrent replaced under the same name lands
  with an unchanged count and an unchanged or *older* timestamp. Folding `(relative path, size,
  mtime)` per file catches that, and catches renames and delete-one-add-one at an unchanged count
  with it. What it cannot catch — a rewrite keeping name, size and mtime — is asserted in the tests
  rather than left to be discovered.
- **It compares a fingerprint against a fingerprint, never files on disk against torrents indexed.**
  The indexed count is post-skip: a folder holding three image-set torrents would read as permanently
  behind and no rescan would ever settle it. That is the same unit mix-up reached from a new direction.
- **The claim is narrow.** It answers *has the disk changed since the last scan*, not *would a rescan
  change the index* — those differ under the index cap and under every skip reason. So the notice says
  the folders changed, never that anything is new, and never that the index is stale.

The fingerprint is taken **before** the walk it belongs to, not after. A torrent copied in mid-walk
can land in a directory the walk has already passed, so capturing at the end would record a file that
was never indexed and report the folder as current. Capturing first can only over-report, and the
worst case there is a rescan that finds nothing.

Three consequences worth knowing. Paths are validated on write: relative ones are refused because they
would resolve against the server's working directory, and filesystem roots because `AllDirectories`
from a root crawls the disk. A reload de-duplicates by file **contents** rather than path, so the same
torrent kept in two folders is one row rather than two. And a missing folder is reported rather than
thrown, because a source can sit on an unmounted drive and one absence must not cost the rest.

**A file the walk passes over is counted, by reason.** Unreadable, unparseable, holding no video, or
a repeat of one already indexed — each has its own number on the reload report, and the rescan line
names the ones that are not zero.

The reason this is worth code rather than a shrug is a gap in what the numbers can express. A skipped
file reaches *nothing*: it has no row, and it is not in the batch page's `unmatched` count either,
because that count is per indexed video file. So it is **invisible rather than unmatched**, and the
two read identically — "none of the N video files across M torrents are in your library" is what the
page shows whether the user has 400 torrents they never downloaded or 400 the extension could not
open. The first is the overwhelmingly common case and nothing can be done about it; the second is
the only kind of failure the user can act on, and it was the one being hidden.

Two smaller decisions inside it. The parse failure and the no-video case are *separate* counters,
though they were one short-circuited condition, because a release carrying no video is routine — image
sets, comics and audio-only, which is what `HasVideo` exists for — and reporting the routine beside
the broken as one number describes neither. And these are counts rather than paths: a list is the more
useful answer and a different decision, since it grows with the folder and the measured corpus is
3,218 torrents in one source. The number is what turns silence into a statement.

**A directory the walk cannot open is not a skipped file, and both walks skip the same one.**
`SearchOption.AllDirectories` throws `UnauthorizedAccessException` from the *enumerator*, which is
outside every per-file guard because there is no file yet — so one locked subdirectory aborted a whole
reload, while the fingerprint caught the same throw and reported the folder unchecked, which
`DiffersFrom` answers with "not changed". Both walks agreed the folder was fine, for the same wrong
reason, and a torrent copied in beside the locked directory was invisible with nothing on the page
hinting why.

Three things settled it. The descent is **one type**, `TorrentFileWalk`, used by the index and the
fingerprint alike: making one resilient and not the other produces two answers about one folder, which
is the shape of an earlier defect. `EnumerationOptions.IgnoreInaccessible` is the framework's answer and is
deliberately **off**, because it makes the directory vanish silently, which is what counting the skip exists to
stop; the descent is explicit so the skip can be counted. And the count is **kept out of the skip
record and out of its total**, because that record counts files and this counts directories with an
unknown number of files behind each — adding them would produce a figure in no unit at all: the
same unit mix-up again. The rescan line therefore says it in a sentence of its own, and says the only thing the walk
honestly knows: how many directories it could not open.

A folder that will not open *at all* is still reported unchecked, and the distinction is the point: a
partly-read folder yields a claim about the readable part, taken identically on both sides and
therefore comparable, while a folder never opened yields no claim to compare.

### Uninstalling keeps the folder, and says so somewhere the user will be

The folder lives under the Cove data root and survives an uninstall, as do the settings in
`extension_data`. Both are right: the torrents are the user's own files, and a reinstall keeping the
cover-host allowlist is the behaviour anyone would want. `IExtension.OnUninstallAsync` exists and is
deliberately **not implemented** — deleting a folder of someone's torrents because they removed a
metadata extension is the destructive act this project has now refused four times over.

That makes it a wording problem, and the wording had three candidate homes. One was checked and ruled
out: **the host's uninstall dialog cannot carry text from an extension.** `ExtensionManifest` has no
field for it, and the message is built entirely by the host from the extension's name and its
dependents (`formatDependentUninstallMessage` in Cove's `SettingsPage.tsx`). `OnUninstallAsync` runs
*after* the user has confirmed, so it cannot inform the decision either.

So it went to the README, where someone evaluating the extension can read it *before* installing, plus
one clause on the settings hint. The clause is deliberately not a fourth sentence there: the hint
already says torrents are "kept until you remove them below", and uninstalling is the single case
where that stops being the whole answer, because it is the one action that takes away the screen while
leaving the files. At the measured corpus density that is 528 MB for 3,272 torrents, reachable only on
the server afterwards.

## A re-tagged torrent is shown, not swept up

A tracker keeps a torrent's id when its tags are edited, so re-downloading a re-tagged `.torrent`
yields the same `TorrentId`. The row therefore read "applied", which meant bulk apply skipped it and
the page's "Hide applied" filter — on by default — kept it off screen. An improved torrent was worth
nothing until someone thought to untick a filter and open the row by hand.

The signal needed no new state. Since "would add" is counted against the video, an applied row
with `tagsToAdd > 0` already *means* "this torrent has more for you than when you last ran it". The
row now reports that as a third status, `updated`.

**It is visible and deliberately not eligible.** A row can also have tags left over because the
reviewer declined them — most often on a pack, where most of the list belongs to other scenes — and
those two cases are indistinguishable from here without recording what was offered at apply time.
Making `updated` bulk-eligible would decide between them on the user's behalf and overwrite a
decision, which *Restraint rules on apply* forbids. Showing it costs nothing and asks a person.

Third status rather than a flag beside "applied" for a mechanical reason worth keeping in mind:
`ApplyAsync`, `eligibleRows` and the hide filter each test this field against an exact string, so a
new value is excluded from both apply paths and survives the hide filter without any of them being
touched. A boolean would have needed all three changed, and a missed one fails silently in the
direction of applying something nobody asked for.

Performers count too. `PerformersToAdd` is what this video is missing rather than what the torrent
names, on the same rule as the tag column beside it, so a torrent that gained only a performer now
surfaces as `updated` instead of staying `applied`.

Only the "still has something to give" half needed widening. The growth half — the recorded tag-list
size — already sees a new performer, because performers are lifted *out of* the tag list, so gaining
one is gaining an entry.

The number had never been rendered. It was in the DTO from the beginning and no revision of
`TorrentBatchPage.tsx` ever displayed it, which is how it went on answering the wrong question
unnoticed: a field nothing shows has nothing to contradict it. It is now in the "Would add" column
beside the tag counts.

## An apply is addressed by row, never by video

A row on the batch page is a `(torrent, file)` pair, and two torrents can describe the same file —
2.32% of corpus sizes are shared, 20 files in the real library. `BatchApplyRequest` carried video ids,
so naming a video named *every* row that video appears in: ticking one of two rows applied both, and
nothing in the request could say which was meant. The request carries rows now, and the client
always sends what it means rather than a set of ids it hopes will be re-derived the same way.

**An empty list is the sweep** — every eligible row — which is what *Apply to N* runs when nothing is
ticked. That is a deliberate asymmetry rather than a default: a caller that names rows gets exactly
those rows, and a caller that names none is asking for the page's own scope.

The label, the confirm and the request are built from **one list** (`planApply`), because the label is
the specification of what the run does; two derivations of it drift, which is the rule the write
folder's bulk removal already holds down. A selection outlives a filter that hides it, and the count
of ticks the filter is hiding is stated rather than left to be discovered — the same obligation the
tag filter carries.

## Restraint rules on apply

A torrent is a **suggestion, never an authority**:

- fields are filled only where empty, unless the reviewer explicitly ticks a field that already has a
  value (which sets `overwrite`; unticked fields are never sent, so nothing unchosen can be touched)
- tags and performers are only ever **added**
- studios are only ever **linked**, never created — the tag list carries a bare lowercase domain
  (`lanternbay`), and creating from that would litter the library with near-duplicates
- **bulk never writes fields at all**, and never overwrites; that stays a per-item decision
- a cover URL is only sent when the box is ticked, so its presence *is* the intent to replace

## Provenance on created tags, and on every link we write

Tags the extension creates are stamped `torrent-metadata.source = torrent-metadata` (a `CustomFieldValue` on the tag). Only on
creation: the field lives on the tag globally, so a tag the *user* made must never be relabelled as
imported, or it would be swept into any future "undo the import" selection.

That covers vocabulary the extension invented. It says nothing about the ~16 links a video gets in the
default "existing tags only" mode, which is most of what a bulk apply writes — so each of those now
gets a `TagApplication` row too, `SourceKey = "torrent-metadata"`, with one `SourceRunId` per apply and
one shared across a whole bulk run.

**The undo is the host's, not ours.** `AiDataPurgeService.PurgeAsync` selects `tag_applications` on
`SourceKey`/`SourceRunId` with no AI-only restriction, deletes them, and then calls
`RemoveOrphanedTagLinksAsync`, which drops the `video_tags` rows left with no provenance behind them.
So "remove everything this extension applied" and "undo that one run" are both `POST /api/ai-data/purge`
with a `dryRun` first, and we build no UI. The cost is one row per link, inside the save the apply
already makes — not `ITagProvenanceService`, whose implementation is in `Cove.Api` where no extension
can reach it, and whose `RecordAsync` queries per tag.

**Only links we actually write.** Recording provenance for a tag the video already carried would hand
the user's own work to that purge, because a link with no provenance left behind it is deleted — and
almost no link in a real library has provenance at all. Cove's own `VideoMetadataApplyService` does
exactly that, which means a later purge of that source can delete a link the user made by hand; we
record inside the `existingTagIds.Add(tag.Id)` branch instead.

**The key deliberately omits Cove's `ext:` prefix.** `SourceKeyConventions.IsExtensionSource` matches
that prefix, and `EffectiveTagDtoLoader.HasEditableDirectSource` then reports `CanRemove = false` for
any tag whose only host-level provenance is an extension — a locked, derived chip the user cannot
delete by hand, only report as incorrect. That is right for a tagger that keeps re-deriving and would
put the tag straight back; this extension writes once, on request, so the lock would remove the
user's simplest correction and return nothing. `extension:<id>` would behave the same as our plain key
today, but only by failing to match `ext:` — and since the key can never be renamed without orphaning
every row, a single broadened check upstream would lock every tag retroactively.

## Frontend is bundled, with the host's React left external

`src/Cove.TorrentMetadata/ui/build.mjs` marks every `@cove/runtime/*` specifier external. Cove serves those through an import
map backed by its own React and react-query; bundling copies would give the extension a second React
instance and break hooks in ways that look like random rendering bugs.

**Classic JSX transform, deliberately.** esbuild's automatic runtime emits
`${jsxImportSource}/jsx-runtime`, and the host publishes `@cove/runtime/react-jsx-runtime` — no value
of `jsxImportSource` produces that specifier, so an automatic build emits an import the map cannot
resolve.

## Writes are batched

The naive shape — save each new tag, look up the provenance field, save its value, query whether an
alias exists — costs ~110 round trips per video (≈2 minutes for 50 videos, observed). Restructured to
create all missing tags in one save and preload aliases in one query: **~5 per video**.

The improvement is reasoned from round-trip counts, **not measured**. See the open issue.

## An apply is one transaction, and the host has to be asked for one

Everything a single apply writes commits together or not at all. It used to be two unwrapped
saves: `ApplyTagsAsync` committed the new `Tag` rows so EF would assign their ids, and the outer save
then wrote the provenance stamps, the aliases, the links, the remote id and the scalars. A throw in
between grew the library's vocabulary with nothing pointing at it, and the endpoint answered with a
raw 500 that said nothing about the tag that now existed.

Cove 1.3 is what turned that window into the expected path. Names and aliases share one
case-insensitive namespace enforced inside `SaveChanges`, so an apply that would write a second
spelling of a name the library already answers to throws on the second save rather than writing, and
two applies racing to create the same missing tag no longer both succeed. Note where the collision
comes from: the request builds it, not the library. A library carrying two spellings cannot reach 1.3
at all — the upgrade's preflight refuses while any conflict remains, and its own SQL guard refuses
independently.

Three things make the transaction possible, and none of them are decoration:

- **Through the execution strategy, not `BeginTransactionAsync` directly.** Cove's own
  `AddDbContext<CoveContext>` turns on `EnableRetryOnFailure`, and a retrying strategy refuses a
  user-initiated transaction outright. A retry re-runs the whole delegate, which is why the video is
  read inside it and why the change tracker is cleared first. `TagMergeService` is written this way for
  the same reason.
- **The cover is fetched before the transaction opens, and once.** It is an outbound HTTP request
  through a rate limiter plus a blob write, and holding a transaction across either would turn a slow
  image host into contention on a tag namespace that 1.3 serialises behind one global advisory lock
 . A second attempt reuses the blob rather than asking that host for the same image again,
  which is the promise the clearance rests on.
- **Blob references need `BlobReferenceTransactionCoordinator`.**
  `BlobReferenceSaveChangesInterceptor` rejects outright any save that changes a blob reference inside
  an explicit transaction — detaching a blob deletes a file, and a file delete does not roll back — so
  setting `ImageBlobId` inside one throws on the way in. The coordinator is the host's own opt-in: it
  holds the reference lease across the transaction and defers the cleanup until the commit is
  confirmed. **Dropping that dependency does not cost tidiness, it breaks every apply that imports a
  cover**, and the fixtures that would catch it are the two in `CoverImportTests` that wire the
  interceptor exactly as the host does.

**A rollback un-creates rows, and one of them is cached.** `GetOrCreateSourceFieldAsync` remembers the
provenance definition's id for the life of the service, which was safe while its own save committed on
its own. Inside a transaction that id is provisional until the commit, and the batch path is where
that bites: one service instance applies every row, so a failed row would hand the next one a
definition id that no longer exists and fail it on a foreign key — turning one bad row into a run of
them, which the bulk apply's breaker then reads as systemic.

**Losing the race to create a tag is recovered, not raised.** The same shape the provenance definition
already had, one table over: the failed insert's entities are detached, the names are re-resolved, the
winner's row is adopted, and any name genuinely still absent is inserted on one more attempt. Adoption
matters beyond not throwing — "created" decides which tags carry the "imported from" stamp, and a tag
this extension did not create must never be relabelled as imported. Recovering in place is only
possible because EF takes a savepoint before a `SaveChanges` that runs inside an open transaction, so
the failure rolls back to it rather than poisoning the transaction.

**A refused name answers 409, not 500.** An extension's minimal-API routes get no global exception
handler — 1.3's patches only the MVC controllers — so an uncaught `TagNameConflictException` reaches
the browser as a bare 500 with an HTML body, and `readApiResponse` can then only show its own fallback
wording. Nothing was written, so the endpoint answers 409 carrying the host's message, which names the
spelling that conflicts.

## A bulk apply carries on past a row it cannot finish

A row that throws is skipped, counted, and the run continues. The alternative — stopping —
sounds safer and is not: the rows are independent by construction, a transaction and a
`ChangeTracker.Clear()` each, so **every row before the throw is already committed either way**. Aborting rolls nothing back.
It buys a shorter run and strictly less information about a library that has already been written to,
and one malformed tag name should not cost the other 499 rows.

**Five failures in a row stops it.** That is no longer "some rows are bad" — a library failing that
consistently is failing on its own state, a legacy duplicate the host now refuses or a database that
has gone away, and grinding out hundreds of identical failures produces no new information. The
threshold is deliberately **its own constant** rather than `CoverRateLimiter.BreakerThreshold`: that
number is quoted verbatim to the tracker's staff, so sharing it would let a change to bulk-apply
behaviour silently alter a promise made to a third party. Same shape, same value, separate reason to
change.

**`StoppedEarly` is on the wire.** The page applies a large selection in chunks, so a breaker only the
server can see resets on every request and trips once per chunk while the run walks the whole
selection anyway — which is most of what it exists to prevent.

**A failure is reported as a count plus the first reason**, never a list, on the rule
`CoverSkipReason` already follows: a systemic fault fails every row with the same sentence, and 468
copies of it is not more information than one. That count used to be a **floor** rather than a tally
of rows that wrote nothing, because `ApplyTagsAsync` saved created tags before it saved aliases and
links and a row throwing on the second save had already written the first. An apply is now one
transaction, so a failed row leaves nothing at all and the count means what it says. The wording stays
"failed" rather than "skipped": the row was attempted, and the reviewer's selection for it was not
applied.

Two traps, both of which were live before this landed:

- **The tracker clear belongs in a `finally`.** A throwing row leaves its own entities tracked, some
  of them `Added`; the next row's save would try to write them again and fail on the same fault,
  turning one bad row into every row after it. It reads as the breaker firing on a library with a
  single bad row in it.
- **The totals must survive a failed run, and the table must refresh anyway.** The page discarded
  every chunk's counts on a throw and left `load()` inside the `try` after the success line, so a run
  that had already committed forty videos reported an exception, no counts, and a table describing the
  videos as they were before it. Rows are committed per row: the table is exactly as stale after a
  failed run as after a clean one.

Cancellation is rethrown rather than counted. A caller that went away is not a partly broken library,
and counting it would let the breaker swallow the cancellation entirely.

## A setting is persisted before it takes effect, and one save happens at a time

`LoadAsync` treats the store as authoritative at startup, so the store has to be what decides whether
a change happened at all. Every setter therefore writes first and assigns second: assigning first
would leave a failed write showing the new value for the rest of the session and reverting on
restart, while the caller had been told it failed — every signal disagreeing with the next one.

That contract held per call and, for a while, not *between* calls. Two saves of one setting could
reach the store in one order and memory in the other, and the two then disagreed permanently — until
a restart re-read the store and produced a third answer. A single write gate makes the pair one step,
so whichever write lands last is also the value in memory. The three setters are one-field calls onto
the same `ApplyAsync` rather than three independent paths, because two ways of writing a setting is
two things that can interleave.

**Every setting is held in one record, published by one assignment.** Three separately assigned
properties could hand the settings panel a new tag style beside an old cover-host list, and the panel
treats what it reads back as the document it just saved. A PUT's response is the state read under the
gate that wrote it, so it describes that save rather than whatever landed beside it. `LoadAsync`
builds to the side and publishes once for the same reason — "any failure leaves the defaults in
place" is what it has always claimed, and assigning per key delivered a mix instead.

**It is not a transaction and does not pretend to be one.** `IExtensionStore` is a key-value store
with no batch write, so a store failure on the second of two keys leaves the first written. Faking
atomicity would mean either rolling back with writes that can themselves fail, or folding three keys
into one JSON document — a stored-format change, with a migration, for a case the shipped UI cannot
even reach, since the panel sends one field per PUT. What is guaranteed is narrower and is the half
that was actually broken: memory and the store never describe different settings.

## The cover-host allowlist is a user setting, not the manifest

A cover URL arrives inside a `.torrent` downloaded from a tracker and is fetched **server-side**, from
inside whatever network the Cove host sits in. Unchecked, a crafted torrent points that request at
`169.254.169.254`, at `localhost`, or at anything else reachable from the host. So it is checked, and
redirects are followed by hand so each hop is checked too — a 302 is the obvious way around a URL
check.

The list started as `permissions.network` in `extension.json`, which had the pleasing property that
the declaration and the enforcement were the same text. That is gone, and the reasoning matters
because the tidier arrangement is the one a refactor would drift back toward:

- **The manifest cannot express it.** The extension is published as working with any Luminance-based
  tracker, so the right host list is the operator's. A manifest field holds a value fixed at
  packaging time and can only ever be wrong for somebody.
- **It was never enforcement anyway.** Cove parses `permissions.network` and reads it nowhere. A
  host listed there does nothing except appear in the one artifact that gets published.
- **So the field is absent, not empty.** An empty list would read as "declares no network use" from
  an extension that plainly makes requests.

It **ships empty**, which keeps the fail-safe direction (empty allows nothing) but moves the default
from "covers work" to "covers are off until configured". That is the part with a consequence: every
refusal now carries a reason the user can act on, the refused host is named, and the *unconfigured*
case is worded differently from the *rejected* one — "you have not set this up" is not "your tracker
is blocked". `TorrentMatchProposal.CoverHostAllowed` answers the same question before the user waits
for an apply. Reverting to a silent null would turn the shipped default into a feature that looks
broken, which is what the wording exists to prevent.

## A name is not where the packet goes

The allowlist above compares **names**, and that is only half of a check. Three things follow, and
each of them was a hole:

- **The socket resolves the name again.** `CoverHostAllowlist.Allows` looks at `uri.Host`; the
  connect that follows does its own DNS. A host that answers a public address when the allowlist
  checks it and `127.0.0.1` when the socket connects is fetched anyway — DNS rebinding — and every
  redirect hop is a fresh connection, so hand-following the hops re-opens the window rather than
  closing it. The cover URL comes out of an untrusted `.torrent`, so the name is attacker-chosen.
- **A name check cannot refuse an address it never sees**, and the settings field accepted address
  literals: `127.0.0.1`, `169.254.169.254` and `10.x` all normalised to themselves. Any account
  holding `videos:scrape` could add one line and turn `…/cover?url=` into an authenticated proxy onto
  the host's network — and where the target answers `image/*` the body comes back, so it was not even
  blind.
- **Subdomains were included automatically.** A listed apex is frequently a shared suffix, so
  allowlisting one admitted any subdomain an attacker could get a record for.

So the check is now in two places that cannot substitute for each other, plus one narrowed rule:

| | |
|---|---|
| `CoverHostSetting.Normalise` | refuses an entry that could never be a public image host: an internal address literal, `localhost` and `*.localhost` (RFC 6761 reserves them for loopback), an undotted intranet name. Runs on **read** as well as on write, so a list stored before this check is filtered rather than grandfathered |
| `CoverAddressPolicy` | resolve-then-pin, wired in as the cover client's `SocketsHttpHandler.ConnectCallback`: resolve once, check every address, connect to *those addresses*. There is no second lookup for an attacker to answer differently |
| `CoverHostAllowlist.Allows` | an entry means that host alone; `*.host.example` (or `.host.example`) means the host and anything under it |

Details worth not undoing:

- **Any address, not "the ones that are not".** A rebinding answer is a mix — one public address to
  satisfy whatever is looking, one internal address to be connected to — so filtering would leave the
  outcome to whichever the socket happened to pick. A real image host does not resolve to
  `127.0.0.1`, so refusing the whole name costs nothing legitimate.
- **The transition prefixes are refused whole.** 6to4, Teredo and NAT64 each embed an IPv4 address
  inside a global-looking IPv6 one. Decoding them would be a second place for this rule to be subtly
  wrong; they are effectively dead on the public internet.
- **The policy is deliberately not inside the connect callback.** `SocketsHttpConnectionContext`
  cannot be constructed by a test, so a rule living there would be a security check nothing could
  reach. `CoverAddressPolicy.IsInternal` and `ResolveAsync` are plain functions with an injectable
  resolver; `ConnectAsync` is wiring that decides nothing.
- **Pinning the address does not weaken TLS.** The handler layers TLS on top of the returned stream
  and still validates the certificate against the request's hostname.
- **The wildcard rule is a breaking change, and the refusal says so.** A list that worked before the
  upgrade refuses subdomains now, so `Explain` detects "this is a subdomain of an entry you already
  have" and names the edit. "Not in the allowlist" would send the operator to add a host that is
  already sitting there.

## What the cover fetch promises the tracker

The tracker's staff cleared publication **conditionally** on 2026-08-15: caching, rate limiting, and
a unique User-Agent. These are therefore not tuning parameters. Each number below was quoted to a third party
who is relying on it, and changing one changes a promise.

| | |
|---|---|
| User-Agent | `TorrentMetadata/<version> (+https://github.com/goiabos/cove-torrent-metadata)`, version read from the manifest at request time |
| Caching | one download per cover URL, persisted across restarts; failures negative-cached for 10 minutes |
| Minimum interval | 1s per host |
| Burst | 3 |
| Concurrent requests | 1 per host |
| `Retry-After` | honoured as given |
| Backoff otherwise | doubling from 1s, capped at 30s |
| Circuit breaker | 5 consecutive failures per host, 60s cooldown, one half-open trial |

Three things about the shape rather than the values:

- **Per host, not global.** A third-party image host and the tracker's own have nothing to do with
  each other; a shared budget would let a slow image host throttle requests to the tracker.
- **The cache is the biggest lever, and it is not an optimisation.** A pack's metadata is applied to
  each of its scenes in turn, and the measured folder holds packs of up to 1913 videos sharing one
  cover URL. Uncached that is 1913 identical requests in a single run — the shape most likely to read
  as abuse. It also saves a duplicate blob, because Cove's store is GUID-keyed rather than
  content-addressed and cannot notice the same bytes twice.
- **Reusing one blob across videos makes the host's reference counting a correctness dependency.**
  Before the cache, one video meant one blob. Never delete a cover blob directly: the host's
  "delete if unreferenced" helper deletes unconditionally when its optional argument is omitted, and
  this repo cannot test the guarantee it now relies on.

### Forgetting a cover costs more than remembering one

The persisted map is `cover:<sha256(url)>` → blob id, about 110 bytes a row, and it is written in
exactly one place: `CoverResolver.StoreAsync`, the **import** path. Previewing writes nothing. So the
store holds one row per distinct cover the user has actually imported onto a video — a figure that
tracks their library, not anything a `.torrent` controls. Browsing a thousand covers costs no
persistent storage at all.

That arithmetic is what settles how the map is bounded, and it settled it the other way from the
obvious answer. `MaxCachedCovers` used to delete the persisted row along with the memory entry, on
the theory that a memory cap could bound the store through it. It could not — the map starts empty on
every boot and only ever learns about a URL something looks up by name, so rows from an earlier
session are invisible to that count forever. And where it *did* fire it was actively harmful:
dropping the row reclaims 110 bytes and charges the next video wanting that cover a fresh request to
the image host — against *one download per cover URL, persisted across restarts*, which is a
clearance condition — **plus a second blob of identical bytes**, because Cove's store is GUID-keyed
rather than content-addressed and cannot notice. Reclaiming a hundred bytes by spending two megabytes
and a promise is not a bound.

So eviction is memory-only, and a dropped entry is re-read from the store on the next lookup: one
store read, no network, no duplicate. **What bounds the store is the stale-blob prune** — a row is
deleted the moment a lookup finds it pointing at nothing. Lazily rather than at startup, by the same
arithmetic: `IBlobService` has no exists-check, so a boot-time sweep is one real blob *open* per row
to reclaim a hundred bytes each.

The general shape is worth keeping in mind before adding any cache bound here: a cover is
megabyte-scale and a record of one is byte-scale, so almost anything that trades the second to save
the first is the wrong way round.

The rate limiter refuses rather than waits when the wait would exceed 20s, because that wait is spent
inside the caller's HTTP timeout — waiting out a three-minute `Retry-After` would be cancelled and
would have achieved nothing but a slow failure. Not sending is the more polite outcome anyway.

### Every request goes through this, including the ones a page makes

The table above described the *import*. It did not describe what the review UI did, and for a while
the two were different things: the dialog and the batch page rendered the torrent's cover with a
plain `<img>` pointed at the URL out of the torrent, so the **browser** fetched it. None of the
machinery above applied — the traffic carried the browser's User-Agent, arrived one request per
visible row with no pacing, and touched neither cache. Three of the four conditions were bypassed by
a page render, and the request happened *before* the user had named the host, under a notice saying
the extension only requests images from hosts they have named.

Covers are now served by an extension endpoint — `GET …/cover?url=` — which runs the same sequence an
import does. Literally the same one: it is `CoverResolver`, and the endpoint is a thin adapter that
turns its answer into a status code. The order is allowlist, then the blob a sibling scene already
imported, then bytes already in memory, then a recent failure, then the network — one fetch per URL
at a time however many callers want it.

That order and that singleness are both the result of the sequence having existed **twice** and
drifted. The copies read correctly in isolation; what they disagreed about was invisible until
they were put side by side. Three things were wrong, and each is a rule now rather than a line:

- **A refusal by the rate limiter is never remembered.** Nothing was learned — the request was not
  sent. The import used to record it in the negative cache, which is a singleton the batch page also
  reads, so one host's sixty-second breaker became ten minutes of missing thumbnails.
- **Bytes already in memory beat a remembered failure.** Both copies asked the negative cache first,
  which refused covers whose bytes were sitting in `CoverPreviewCache`. A remembered failure is a
  claim about the network; those bytes are not.
- **The preview entry is dropped after the importer's save lands**, not when the blob is written —
  otherwise one failed `SaveChanges` leaves an orphaned blob and re-downloads the cover, which is the
  promise below broken by the code that keeps it.

So:

- **Preview and import are the same request.** Previewing warms the caches; ticking the box
  afterwards costs nothing, and the network is hit **at most once per URL** whichever order they
  happen in. That last part is what `CoverPreviewCache` exists for, and it is memory-only: a
  previewed cover is by definition an image no video references, and Cove deletes unreferenced blobs,
  so the blob store is the wrong place to hold one. Its 64 MB byte budget is **ours** — unlike the
  numbers above it was never quoted to anyone — and it evicts oldest-first rather than counting
  entries, because covers range from a few KB to multi-megabyte animated WebP.
- **A preview waits 2s at the limiter, not 20s.** A browser allows about six connections per origin,
  and a request parked for twenty seconds holds one of them for twenty seconds — a screenful would
  starve the extension's own API calls. Over the ceiling it answers `429` with `Retry-After` and the
  page comes back for it. This weakens nothing: the numbers above bound how fast requests go out, not
  how long one may queue, and refusing early sends *fewer* of them.
- **The refusals are the client's problem to absorb, and it absorbs them by asking less.** A page of
  covers sharing one image host gets about five served and the rest refused, which is the limiter
  working. What made that unbearable was the browser: twenty parallel `<img>` loads, two retries
  inside three seconds guessing a shorter wait than the server had asked for, and then a blank frame
  for good. `CoverImg` fetches instead, which makes a refusal *readable* — `Retry-After` is
  honoured as sent, refused covers queue in one paced line rather than coming back together, and
  nothing is requested until its frame is near the viewport.

  **Reading the refusal is not the same as not causing it, and the difference cost two rounds of
  this.** A browser logs every failed request whatever the code does with the response, so moving to
  `fetch` silenced nothing by itself. Pacing the retries on a *timer* did not either, and the reason
  is the limiter's least obvious number: `MaxConcurrentPerHost = 1`, held for the **whole upstream
  fetch**. Covers here are routinely multi-megabyte animated GIFs, so a four-second one refuses
  everything sent underneath it — a one-second cadence simply piles three requests onto a busy slot
  and collects three refusals.

  So the client's rule is the server's rule: **one cover request at a time, and the next one starts
  only when the previous has settled**. Refusals then stop being something to manage — the slot is
  free by construction whenever we ask. The token bucket still throttles a run of fast small covers,
  but it does so *inside* the request, where a `1s` wait is under the `2s` ceiling, so it slows the
  page instead of refusing it.

  A serial line costs the common case nothing: the proxy answers a cover it already holds before the
  limiter is consulted at all, and a served cover carries `Cache-Control: private, max-age=86400`, so
  a revisited page runs the line at a few milliseconds a row. What remains is one refusal per cover
  while a *bulk cover import* holds the slot with its own twenty-second ceiling, which is the one
  contention the page cannot arrange away.

- **A fact the client needs is asked for, not discovered by failing.** `BatchRow.VideoHasImage` and
  `TorrentMatchProposal.VideoHasImage` exist because the page renders one library thumbnail per row
  and the dialog decides whether to open its comparison on the same fact — both of which were
  previously learned from `/api/videos/{id}/image` answering 404, once per row, each logged. It is one
  boolean on a projection that was already being read.
- **Nothing is fetched before the host is allowed.** The dialog renders no torrent-side image until
  `coverHostAllowed`, and the batch page omits the thumbnail entirely for a host the operator has not
  named. The notice's wording is finally true on the page it appears on.
- **Taking a URL does not make it an open proxy.** It fetches only from allowlisted hosts, the list
  ships empty, and every redirect hop is re-checked — so it reaches nowhere an import could not
  already. That check is shared code rather than a second copy, which is the point: two copies of a
  redirect check is how the allowlist grows a hole.

## There is no generic import core, and the extension is not inverted

The original idea was to turn this inside out: a generic metadata-suggestion core ingesting JSON/CSV,
with torrent parsing as one adapter. Rejected after surveying the Cove checkout, for reasons that are
invisible from the code and would otherwise be relitigated every time someone notices how specific
the torrent path looks.

- **Cove is already the generic multi-source metadata platform** — but entirely *pull*-shaped.
  Extensions register scrapers via `IScraperProvider`, results persist as reviewable `ScrapeAttempt`s
  with field-selective apply and provenance. There is **no push-style import anywhere in the host**:
  no metadata sidecar parsing (only `.vtt`/`.srt` captions; `.nfo` is indexed as a text document,
  never parsed), no CSV handling, and `POST /api/scrape-attempts` requires a registered `ScraperId`
  and always *executes* it — so an extension cannot deposit a free-form proposal into the review
  queue. A generic ingest core here would duplicate the host's central feature. The host-shaped fix is
  an upstream feature, not this extension growing a second identity.
- **This extension's value is its direction.** Cove's pipeline is entity-first: pick a video, invoke a
  provider. Ours is artifact-first: drop a torrent, find which library videos its files cover, review,
  apply. That inversion is *why* it has its own match UI and batch pipeline, and it depends on the
  torrent's file list. A JSON or CSV document has no file list, so generic ingest needs its own
  matching contract — a real design, not a refactor.
- **No producer exists for the format** we would have to invent. The realistic author of such a file
  is a script written against a schema only we define.
- **Registering as a `ByFragment` scraper provider was considered and rejected**: it would inherit the
  host's review UI, but fragment scrapers are invoked entity-first, which is the wrong direction for
  this flow — and our review UI already exists and is tested.

What survives of the idea is the **dialect seam**: bencode parse → dialect extractor → a
source-agnostic record that match and apply consume. Another tracker family is a new
`ITorrentDialect`, not a new extension, and a JSON ingest later would be a small addition to a tested
pipeline rather than a rewrite.
