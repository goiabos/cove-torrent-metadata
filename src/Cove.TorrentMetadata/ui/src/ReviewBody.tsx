import React from "@cove/runtime/react";
import {
  matchApi,
  type ProposedRelation,
  type TorrentApplyResult,
  type TorrentMatchProposal,
} from "./api";
import { CLOSE_PENDING_LABEL, resolveCloseRequest } from "./closeGuard";
import { CoverImg } from "./CoverImg";
import { tagStyleLabel } from "./naming";
import {
  applyButtonLabel,
  buildApplyRequest,
  buildFields,
  countTags,
  coverHost,
  describeTagFilter,
  coverStartsOpen,
  defaultSelection,
  describeApplyResult,
  filterTags,
  partitionTags,
  relationBadge,
  showsTagFilter,
  studioProposal,
  surprisingSource,
  sweepTags,
  matchedFileLabel,
  videoDisplayName,
  type Selection,
} from "./review";

const { forwardRef, useCallback, useImperativeHandle, useMemo, useRef, useState } = React;

/**
 * The walk this review is part of, when it is part of one.
 *
 * Optional, and the review knows nothing else about queues: it renders a position and two buttons,
 * and the decisions behind them live in `queue.ts`. That is what lets one review body serve a
 * single review opened from a video's action, a footer pager in a modal, and the split-pane page.
 */
export interface ReviewPager {
  /** Where in the walk this row is, already worded — "3 of 51". */
  position: string;
  canPrev: boolean;
  canNext: boolean;
  onPrev: () => void;
  onNext: () => void;
  /** True while the next proposal is on its way. The frame is held, not blanked. */
  busy: boolean;
  /** A step that failed, put where the reviewer is actually looking. */
  error: string | null;
  /**
   * The keys that also move this walk, when something has bound them.
   *
   * Set by whoever binds them rather than assumed here, so the review can never advertise a shortcut
   * that nothing is listening for — the same review body is mounted in a frame that walks and in one
   * that does not.
   */
  keyHint?: string;
}

export interface ReviewBodyProps {
  proposal: TorrentMatchProposal;
  onClose: () => void;
  /**
   * Called after a successful apply, with what the server reports it changed.
   *
   * A notification, not a teardown instruction: the dialog leaves itself in a valid state before
   * calling this and shows the summary itself, so a caller is free to unmount, to surface the
   * summary somewhere of its own, or to do nothing at all. It used to be the only thing that
   * ended the applying state, which meant a caller that did nothing froze the dialog for good.
   *
   * A caller walking a queue is exactly that third case: it records the row and leaves the dialog
   * open, so the reviewer reads the result and then decides whether to move on.
   */
  onApplied?: (proposal: TorrentMatchProposal, result: TorrentApplyResult) => void;
  pager?: ReviewPager;
  /**
   * Gathers this torrent's other rows, where the frame has a list to gather them in.
   *
   * Offered from inside the review because this is where a pack is recognised — the reviewer is
   * looking at one scene of it. It narrows the list beside the review and never the review itself, so
   * it is not the control `docs/DESIGN-DECISIONS.md` bars: nothing about this proposal changes.
   */
  onFocusPack?: () => void;
}

/**
 * What a shell can ask of the review body through a ref, rather than act on directly.
 *
 * `requestClose` is the one method: it is the Close button's own handler, exposed so Escape and a
 * backdrop click — both live in the shell wrapping this body, `MatchDialog` or `ReviewPane` — reach
 * the exact same decision instead of each growing its own copy of "is an apply in flight?". The body
 * is the only thing that holds that state, so it stays the only thing that answers the question; the
 * shell asks rather than deciding for itself, which is what keeps it free of `useState`.
 *
 * `forwardRef`/`useImperativeHandle` has no other use in this codebase, and is reached for here on
 * purpose rather than by habit: the alternative — handing the shell a plain callback prop rebuilt on
 * every render — would still need somewhere to hold it, and a ref in the shell for that is no
 * different in kind from this one. A ref that calls back into the body it points at is the direct
 * shape of "ask, don't act"; growing a second state container in the shell to fake the same thing
 * would be the state duplication the split is meant to prevent, only spelled differently.
 */
export interface ReviewBodyHandle {
  requestClose: () => void;
}

/**
 * Shown in place of the cover toggle when the cover's host is not configured.
 *
 * The allowlist ships empty, so on a fresh install this is what every reviewer sees first —
 * which is why it offers the fix inline rather than only naming the setting. Adding the host is one
 * click from the place the user just discovered they needed it.
 *
 * It writes the *whole* list, so it reads the current one first rather than assuming it is empty: a
 * second tracker's host must not wipe the first.
 */
function CoverHostNotice({ host, onAllowed }: { host: string | null; onAllowed: () => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const allow = async () => {
    if (!host) return;
    setBusy(true);
    setError(null);
    try {
      const current = await matchApi.getSettings();
      await matchApi.setCoverHosts([...current.coverHosts, host]);
      // Told to the dialog, not re-fetched. `coverHostAllowed` is the only thing about a proposal that
      // allowing a host changes, and the request that just succeeded is proof of the new value — so
      // re-deriving it from the server bought nothing and cost the whole review, because the callers
      // implemented a re-fetch by unmounting the dialog.
      onAllowed();
    } catch (failure) {
      setError((failure as Error).message);
      setBusy(false);
    }
  };

  return (
    <div className="tm-warn tm-cover-warn">
      {host ? (
        <>
          Covers are not fetched from <code className="tm-code">{host}</code> yet. The extension only
          requests images from hosts you have named, because the URL comes out of the torrent itself.
          <button type="button" className="tm-allow" disabled={busy} onClick={() => void allow()}>
            {busy ? "Adding…" : `Allow ${host}`}
          </button>
        </>
      ) : (
        <>This torrent's cover URL cannot be read, so no cover can be imported from it.</>
      )}
      {error ? <div className="tm-allow-error">{error}</div> : null}
    </div>
  );
}

/**
 * One of the two covers being compared, degrading to the same blank frame as the not-yet-fetched
 * case rather than disappearing.
 *
 * The point of this grid is judging the artwork before taking it, so a cover that fails to load has
 * to *say* it failed. Hiding the `<img>` left its `<figcaption>` labelling nothing, and hiding it
 * with `visibility` only appeared to hold the space: an `alt=""` image with no intrinsic size
 * collapses to nothing whether it is hidden or not. The frame is what keeps the two columns level.
 */
function CoverImage({ src }: { src: string }) {
  const [failed, setFailed] = useState(false);
  if (failed) return <div className="tm-cover-blank">cover unavailable</div>;

  return <img src={src} alt="" loading="lazy" onError={() => setFailed(true)} />;
}

/**
 * The cover decision, at the size of a decision.
 *
 * The comparison used to be the first thing in the body and about 380px of it — one yes/no, spending
 * more vertical space than the entire tag list, which is thirty of them. The *decision* stays
 * in view; the *evidence* is one click away, and the two thumbnails are enough to notice that the
 * torrent's artwork is a different scene entirely.
 *
 * This also carries the identity the header cover used to: it is now the only place the library's own
 * artwork appears, rather than the second.
 *
 * It opens itself when the video has no artwork at all, because that is the case the comparison
 * exists for — there is nothing to weigh the torrent's cover against, and the answer is almost
 * always yes. `coverStartsOpen` decides that from the proposal's own `videoHasImage`, which the
 * server answers: it used to be learned by rendering an image and waiting for the 404.
 */
function CoverSection({
  proposal,
  importCover,
  onToggleImport,
  coverHostAllowed,
  onAllowed,
}: {
  proposal: TorrentMatchProposal;
  importCover: boolean;
  onToggleImport: () => void;
  coverHostAllowed: boolean;
  onAllowed: () => void;
}) {
  // Seeded from the proposal rather than discovered: whether the video has artwork decides both what
  // the strip shows and whether the comparison opens by itself, and the dialog used to learn it by
  // rendering an image and waiting for the 404.
  const [open, setOpen] = useState(() => coverStartsOpen(proposal));
  const host = useMemo(() => coverHost(proposal.coverUrl), [proposal.coverUrl]);
  const torrentCover = proposal.coverUrl;
  const libraryArtwork = proposal.videoHasImage;

  return (
    <div className="tm-section is-first">
      {torrentCover && !coverHostAllowed ? <CoverHostNotice host={host} onAllowed={onAllowed} /> : null}

      <div className="tm-cover-strip">
        {libraryArtwork ? (
          <img className="tm-strip-thumb" src={`/api/videos/${proposal.videoId}/image?max=160`} alt="" title="in library" />
        ) : (
          <span className="tm-strip-thumb is-blank" title="no artwork in your library" />
        )}

        {/* Only through our own endpoint, and only once the host is allowed — never an `<img>`
            pointed at the torrent's URL, which is the one attribute that bypasses the allowlist,
            the User-Agent, the pacing and the cache all at once. */}
        {torrentCover && coverHostAllowed ? (
          <CoverImg url={torrentCover} className="tm-strip-thumb" title="from torrent" urgent />
        ) : null}

        {torrentCover ? (
          <>
            {/* The button sits outside the label deliberately: a button inside one has its click
                forwarded to the label's checkbox, so "Compare" would silently tick "import". */}
            <label className="tm-check">
              <input
                type="checkbox"
                checked={importCover}
                disabled={!coverHostAllowed}
                onChange={onToggleImport}
              />
              Import the torrent's cover
              <span className="tm-hint">{libraryArtwork ? "(replaces the current one)" : "(this video has none)"}</span>
            </label>
            <button type="button" className="tm-btn is-small tm-strip-action" onClick={() => setOpen((value) => !value)}>
              {open ? "Hide" : "Compare"}
            </button>
          </>
        ) : (
          <span className="tm-hint">This torrent carries no cover.</span>
        )}
      </div>

      {open && torrentCover ? (
        <div className="tm-cover-grid">
          <figure className="tm-cover">
            {libraryArtwork ? (
              <CoverImage src={`/api/videos/${proposal.videoId}/image?max=320`} />
            ) : (
              <div className="tm-cover-blank">no artwork yet</div>
            )}
            <figcaption>in library</figcaption>
          </figure>
          <figure className="tm-cover">
            {coverHostAllowed ? (
              <CoverImg url={torrentCover} className="tm-cover-shot" failedLabel urgent />
            ) : (
              <div className="tm-cover-blank">not fetched yet</div>
            )}
            <figcaption>from torrent {importCover ? "— will replace" : ""}</figcaption>
          </figure>
        </div>
      ) : null}
    </div>
  );
}

/**
 * One tickable row: a tag or a performer.
 *
 * `source` is passed in rather than derived here because the two callers answer it differently, and
 * the difference is the point. A tag shows its source only where the normaliser did something a
 * reviewer would not have predicted (`surprisingSource`) — for a tag about to be created, that
 * spelling is what gets written and seeded as an alias. A performer shows the tag-list entry that
 * found them, and only when an alias did and their own name never appeared. Deriving one rule
 * from the other here would put a chip on the reversed `last.first` permutation that half the corpus
 * carries, which is not a surprise and not an alias.
 */
function RelationRow({
  name,
  source,
  title,
  matchesExisting,
  alreadyApplied,
  checked,
  onToggle,
}: {
  name: string;
  source: string | null;
  title: string;
  matchesExisting: boolean;
  alreadyApplied: boolean;
  checked: boolean;
  onToggle: () => void;
}) {
  const badge = relationBadge({ alreadyApplied, matchesExisting });

  return (
    <label className={`tm-row${alreadyApplied ? " is-applied" : ""}`}>
      <input type="checkbox" checked={checked} disabled={alreadyApplied} onChange={onToggle} />
      <span className="tm-name" title={title}>{name}</span>
      {source ? <span className="tm-source" title="as written in the torrent">{source}</span> : null}
      <span className={`tm-badge${!matchesExisting && !alreadyApplied ? " is-new" : ""}`}>{badge}</span>
    </label>
  );
}

/**
 * The review itself: everything between the frame it is drawn in and the proposal it is about.
 *
 * It renders a header, a body and a footer and nothing around them, so the same review serves a modal
 * over a video's page and a pane beside the batch page's list. **A shell owns position and
 * dismissal; this owns every decision.** The test that the split is honest is that no shell holds
 * state — if a piece of it has to move up there, it belonged to the review and the split is in the
 * wrong place.
 *
 * It is a component, so it carries no tests: the suite has no DOM and no stand-in for
 * `@cove/runtime/react`, deliberately. What that costs is bounded by everything it defers to —
 * `review.ts` decides what a badge says, what the window is named, what a sweep reaches and what the
 * counts mean; `queue.ts` decides the walk. What is left here is arrangement.
 *
 * Wrapped in `forwardRef` so a shell can reach `requestClose` — see `ReviewBodyHandle`. Nothing else
 * about this component is imperative; the ref exists for that one method and nothing is read back
 * through it.
 */
export const ReviewBody = forwardRef<ReviewBodyHandle, ReviewBodyProps>(function ReviewBody(
  { proposal, onClose, onApplied, pager, onFocusPack },
  ref,
) {
  /**
   * The review as it stands, which is the proposal it opened with until an apply changes the video
   * underneath it.
   *
   * Seeded once. The caller remounts on the `(torrent, file)` key when the queue steps, so a
   * different row is a different dialog rather than a prop swap — which is what lets the selection be
   * a mount-time default.
   */
  const [current, setCurrent] = useState(proposal);

  // A pack's tags describe the whole release, so the dialog says so as well as starting empty; what
  // is ticked on open is decided by `defaultSelection`, not here.
  const isPack = current.fanOut > 1;

  const fields = useMemo(() => buildFields(current), [current]);
  // Derived once per mount, and nothing in the dialog re-fetches the proposal any more, so a review in
  // progress now survives every control on it. A queue steps by remounting on the row key, which
  // is why this can stay a mount-time default rather than growing a reset.
  const [selection, setSelection] = useState<Selection>(() => defaultSelection(current, fields));

  // Cover import is opt-in even on a fresh video: it fetches from a third-party host, so it should be
  // a visible choice rather than something that happens because a field was empty.
  const [importCover, setImportCover] = useState(false);
  // Seeded from the proposal and then owned here, because the user can allow the host from inside this
  // dialog. The server is the authority at apply time regardless; this only decides what is offered.
  const [coverHostAllowed, setCoverHostAllowed] = useState(current.coverHostAllowed);
  const [busy, setBusy] = useState(false);
  // Set the moment Close is asked for while an apply is in flight, and read from a ref rather than
  // this state inside `apply()` — that function's closure is fixed at the click that started it, so a
  // later `setState` from a second click would never be seen without one. The state exists only to
  // drive the button's own label; `closeRequestedRef` is what actually gates the teardown.
  const [closePending, setClosePending] = useState(false);
  const closeRequestedRef = useRef(false);
  const [status, setStatus] = useState<{ text: string; error: boolean } | null>(null);
  // What the last apply did, kept apart from `status` because it is a receipt rather than a message:
  // it stays on screen while the reviewer looks at what changed, and a later error joins it instead
  // of overwriting it.
  const [applied, setApplied] = useState<string | null>(null);
  // Tags already on the video are inert — disabled checkboxes that cannot be acted on — so they are
  // counted rather than listed, and opened on request. Nothing importable is ever behind this.
  const [showOnVideo, setShowOnVideo] = useState(false);
  // What the reviewer is looking for in a tag list too long to read. Per review, and reset by the
  // remount the queue does on every step — a filter is about this list, not about the walk.
  const [tagQuery, setTagQuery] = useState("");

  const toggle = <T,>(set: ReadonlySet<T>, value: T) => {
    const next = new Set(set);
    if (next.has(value)) next.delete(value);
    else next.add(value);
    return next;
  };

  // Tags only, and the buttons live in the tag section so that reads. Fields and performers keep
  // whatever the reviewer set — sweeping them from a control that looks like a list header is how a
  // careful default got undone in one click.
  const setAllTags = useCallback(
    (on: boolean, shown: readonly ProposedRelation[]) =>
      setSelection((set) => ({ ...set, tags: sweepTags(set.tags, shown, on) })),
    [],
  );

  // Clears `busy` in a `finally`, and used to clear it only on error — the success path relied on the
  // caller unmounting the dialog to escape the disabled state. One caller does not (`main.tsx`), and
  // `onApplied` is optional besides, so the dialog sat at "Applying…" with every button dead. A
  // component must reach a valid state on its own; being unmounted is the caller's choice, never the
  // exit route.
  const apply = async () => {
    setBusy(true);
    try {
      const result = await matchApi.apply(
        buildApplyRequest({ proposal: current, fields, selection, importCover }),
      );

      setApplied(describeApplyResult(result));
      setStatus(null);
      onApplied?.(current, result);

      // A close asked for while this was in flight is now safe to honour: `onApplied` just ran, so
      // the caller's own record of the apply — the queue's `applied` set, the reload flag in
      // `main.tsx` — is made. Closing any earlier is the defect this guard exists for: `onClose` tears the caller's state down
      // synchronously, and a queue that is already gone when this call finally lands drops the record
      // it was meant to receive — the same failure through this door. Re-seeding below is for a reviewer
      // still looking at the dialog; one who has already asked to leave gets nothing from it.
      if (closeRequestedRef.current) {
        onClose();
        return;
      }

      // The review is over, so the dialog stops describing a video that no longer exists. Everything
      // on screen — which tags are on the video, which fields still have a gap, what the counts say —
      // was computed from the state before the apply, and leaving it there is how an apply that
      // worked reads as one that did nothing.
      //
      // This is not the reload the remount rule forbade. That rule bars a *control* from re-fetching a review in
      // progress, because doing so throws away the reviewer's selection. Here the selection has just
      // been spent: re-seeding from what the server now holds is the only honest thing left to show.
      try {
        const after = await matchApi.match(current.videoId, current.torrentName, current.fileName);
        const afterFields = buildFields(after);
        setCurrent(after);
        setSelection(defaultSelection(after, afterFields));
        setCoverHostAllowed(after.coverHostAllowed);
        setImportCover(false);
        setShowOnVideo(false);
      } catch (failure) {
        // The apply stands whatever this says, so the receipt survives and the failure joins it
        // rather than replacing it.
        setStatus({ text: `Applied, but could not re-read the video: ${(failure as Error).message}`, error: true });
      }
    } catch (error) {
      setStatus({ text: (error as Error).message, error: true });
      // Nothing was written, so there is no bookkeeping to wait for — a close asked for during a
      // failed apply is honoured the moment the failure is known, same as it would have been had the
      // reviewer waited for the message and then clicked Close themselves.
      if (closeRequestedRef.current) onClose();
    } finally {
      setBusy(false);
    }
  };

  /**
   * The one function every way to leave the dialog calls, and the only place `closeRequestedRef` is
   * set — the Close button below calls it directly, and it is also what `requestClose` on the
   * imperative handle hands to Escape and the backdrop click in whichever shell wraps this body. All
   * three are the same request, so `resolveCloseRequest` is what actually decides — this function
   * only carries the answer out.
   *
   * While idle the request is granted at once. While an apply is in flight it defers: the request is
   * acknowledged — the button turns to `CLOSE_PENDING_LABEL` rather than staying mute — but the
   * actual teardown waits for `apply()` to reach a point where it is safe, above. The alternative was
   * disabling every door outright, which was rejected: a reviewer stuck behind a slow apply (a large
   * pack's tags, a slow cover fetch) would have no way out of the dialog at all, which is a worse
   * complaint than a close that takes a moment to land.
   */
  const requestClose = useCallback(() => {
    if (resolveCloseRequest(busy) === "defer") {
      closeRequestedRef.current = true;
      setClosePending(true);
      return;
    }
    onClose();
  }, [busy, onClose]);

  // The only thing exposed to a shell, and the only reason this component is wrapped in `forwardRef`
  // at all — see `ReviewBodyHandle`. Nothing else here is imperative.
  useImperativeHandle(ref, () => ({ requestClose }), [requestClose]);

  // Split so the header can say what the numbers actually mean: what is already on the video, and of
  // the remainder, how much would reuse an existing tag versus create a new one. Both the split and
  // the arithmetic live in `review.ts`, where a test can see them — this exact sum answering a
  // different question than it appeared to is a defect this codebase has already had.
  const { toImport, onVideo } = useMemo(() => partitionTags(current), [current]);
  // The list as it stands: every importable tag until the reviewer types, and only then a subset.
  // Both sweeps and every count below read `shown`, so the filter can never be true on screen and
  // false in what a button does.
  const shownTags = useMemo(() => filterTags(toImport, tagQuery), [toImport, tagQuery]);
  const tagFilter = useMemo(
    () => describeTagFilter({ query: tagQuery, shown: shownTags, total: toImport.length, selection: selection.tags }),
    [tagQuery, shownTags, toImport.length, selection.tags],
  );
  const tagCounts = useMemo(() => countTags(current), [current]);
  const studio = useMemo(() => studioProposal(current), [current]);

  // The video is what this window edits, so it is what the window is named after. It used to be
  // titled `proposal.title` — the torrent's *proposed* title, which is one of the checkboxes below:
  // the heading was a claim under review, and a reviewer who declined it had been reading a name that
  // would not survive the apply.
  const videoName = videoDisplayName(current);
  // Which file of the torrent this window is about. Its own line rather than a fourth clause on the
  // one above, so a long name truncates instead of wrapping the match description in half — and so
  // it sits where the list puts it, directly under the torrent name.
  const matchedFile = matchedFileLabel(current);

  return (
    <>
      <div className="tm-head">
        <div className="tm-head-main">
          <h3 className="tm-title">{videoName}</h3>
          <p className="tm-sub">
            {current.torrentName} · matched on {current.matchedOn} ·{" "}
            {/* Opens in a new tab so an in-progress review is never lost to navigation. */}
            <a className="tm-link" href={`/video/${current.videoId}`} target="_blank" rel="noreferrer">
              open video ↗
            </a>
          </p>
          {matchedFile ? (
            <p className="tm-sub tm-sub-file" title={matchedFile}>
              {matchedFile}
            </p>
          ) : null}
          {isPack ? (
            <div className="tm-warn">
              This torrent covers {current.fanOut} video files. Its tags describe the whole set, so most
              will not apply to this one. Nothing is selected by default — pick only what fits.
              {/* One tag list divided across scenes is a working set, and bulk apply refuses it on
                  purpose — so this is the only route it has. */}
              {onFocusPack ? (
                <button type="button" className="tm-btn is-small tm-warn-action" onClick={onFocusPack}>
                  Show this pack's rows
                </button>
              ) : null}
            </div>
          ) : null}
          {/* Stated, not offered. The control that changed this used to live here, and changing it
              mid-review threw the review away; it is on the Settings page now. Naming the style
              still earns its place — it is what the "new" badges below will be called. */}
          <div className="tm-toolbar">
            <span className="tm-hint">
              New tags are named <strong>{tagStyleLabel(current.tagNameStyle)}</strong> — change this
              in Settings.
            </span>
          </div>
        </div>

        {/* The second door out, and the only one that is always on screen. The footer's Close is the
            first, and in `MatchDialog` that is enough — the box is centred and its footer is in view.
            `ReviewPane` is as tall as the viewport allows and stands taller than it until its sticky
            position engages, so a review opened from the batch page put its only exit below the fold
            and the page read as stuck. Both call `requestClose`, so the apply-in-flight rule is still
            decided in one place. */}
        <button
          type="button"
          className="tm-close"
          onClick={requestClose}
          disabled={closePending}
          aria-label="Close review"
          title={closePending ? CLOSE_PENDING_LABEL : "Close review"}
        >
          ×
        </button>
      </div>

      <div className={`tm-body${pager?.busy ? " is-stepping" : ""}`}>
        {/* The receipt, where the reviewer is looking rather than in the footer's margin. What is
            below it is the video as it now stands, re-read after the apply — so "20 tags" and a
            list showing those tags still on offer can no longer disagree. */}
        {applied ? <div className="tm-applied">{applied}</div> : null}

        <CoverSection
          proposal={current}
          importCover={importCover}
          onToggleImport={() => setImportCover((value) => !value)}
          coverHostAllowed={coverHostAllowed}
          onAllowed={() => setCoverHostAllowed(true)}
        />

        {fields.length || studio.kind !== "none" ? (
          <div className="tm-section">
            <div className="tm-section-head">
              Fields <span className="tm-hint">unticked keeps the current value</span>
            </div>
            {/* A field that only fills a gap is one line: its current value is nothing, and printing
                "current: " above an empty string was 70px saying so. A field that would *replace*
                keeps both values, stacked, and is never collapsed or pre-ticked — it is the only
                decision in this window that can destroy something curated. */}
            {fields.map((field) => (
              <label key={field.key} className={`tm-field${field.replaces ? " is-replace" : " is-fill"}`}>
                <input
                  type="checkbox"
                  checked={selection.fields.has(field.key)}
                  onChange={() => setSelection((set) => ({ ...set, fields: toggle(set.fields, field.key) }))}
                />
                <div className="tm-field-body">
                  <div className="tm-field-label">
                    {field.label}{field.replaces ? " — would replace" : ""}
                  </div>
                  {field.replaces && field.current ? <div className="tm-value is-current">{field.current}</div> : null}
                  <div className="tm-value is-proposed">{field.proposed}</div>
                </div>
              </label>
            ))}

            {/* The studio the library could not decide. Two curated studios both matched, so
                the extension proposes neither — but the person looking at the video knows which, and
                the alternative is spending that in silence. "None" is selected, and is not a
                placeholder for an unmade decision: it is the answer the matcher already reached, drawn. */}
            {studio.kind === "choose" ? (
              <div className="tm-field is-choose">
                <div className="tm-field-body">
                  <div className="tm-field-label">Studio — your library holds both</div>
                  <div className="tm-choice" role="radiogroup" aria-label="Studio">
                    <button
                      type="button"
                      role="radio"
                      aria-checked={selection.studio === null}
                      className={`tm-choice-opt${selection.studio === null ? " is-on" : ""}`}
                      onClick={() => setSelection((set) => ({ ...set, studio: null }))}
                      disabled={busy}
                    >
                      None
                    </button>
                    {studio.options.map((option) => (
                      <button
                        key={option.name}
                        type="button"
                        role="radio"
                        aria-checked={selection.studio === option.name}
                        className={`tm-choice-opt${selection.studio === option.name ? " is-on" : ""}`}
                        onClick={() => setSelection((set) => ({ ...set, studio: option.name }))}
                        disabled={busy}
                      >
                        {option.name}
                        {/* The domain, because network and imprint differ by domain and not by how
                            the library spells them — it is the half the reviewer recognises from the
                            release. */}
                        <span className="tm-choice-dom">{option.source}</span>
                      </button>
                    ))}
                  </div>
                </div>
              </div>
            ) : null}

            {/* A count and no names: naming studios the window will not offer is noise, and a
                shortlist would have to be ordered — which is the defect the studio rule exists to kill. */}
            {studio.kind === "many" ? (
              <div className="tm-field is-inert">
                <div className="tm-field-body">
                  <div className="tm-field-label">Studio</div>
                  <div className="tm-value is-quiet">
                    {studio.count} studios in your library match this torrent — none proposed
                  </div>
                </div>
              </div>
            ) : null}
          </div>
        ) : null}

        {/* Inline rather than a section of its own: 1.9 performers is the corpus average, and a
            heading plus its margins cost more than the two rows under it. */}
        {current.performers.length ? (
          <div className="tm-section is-tight">
            <div className="tm-section-head">Performers ({current.performers.length})</div>
            <div className="tm-chips is-auto">
              {current.performers.map((performer) => (
                <RelationRow
                  key={performer.id}
                  name={performer.name}
                  // Set only when an alias is the sole reason this name is on screen.
                  source={performer.source}
                  title={performer.source ?? performer.name}
                  // Always: the server only proposes performers the library already holds.
                  matchesExisting
                  alreadyApplied={performer.alreadyApplied}
                  checked={selection.performers.has(performer.id)}
                  onToggle={() =>
                    setSelection((set) => ({ ...set, performers: toggle(set.performers, performer.id) }))
                  }
                />
              ))}
            </div>
          </div>
        ) : null}

        {current.tags.length ? (
          <div className="tm-section">
            <div className="tm-section-head">
              Tags ({tagCounts.total})
              <span className="tm-hint">
                {tagCounts.toImport > 0
                  ? `${tagCounts.toImport} to import: ${tagCounts.existing} existing, ${tagCounts.created} would be created`
                  : "nothing left to import"}
              </span>
              {/* Here rather than in the footer beside Apply: a footer button reads as acting on
                  the dialog, and these act on this list. Named for what they move, so the scope is
                  legible without reading the code. */}
              {/* Named for what they move, so the scope is legible without reading the code — and
                  under a filter that scope is what is *shown*, which is the whole of what keeps a
                  filtered sweep from repeating an older mistake in a new place. */}
              <span className="tm-section-actions">
                <button
                  type="button"
                  className="tm-btn is-small"
                  onClick={() => setAllTags(true, shownTags)}
                  disabled={busy || shownTags.length === 0}
                >
                  {tagFilter.selectAll}
                </button>
                <button
                  type="button"
                  className="tm-btn is-small"
                  onClick={() => setAllTags(false, shownTags)}
                  disabled={busy || shownTags.length === 0}
                >
                  {tagFilter.selectNone}
                </button>
              </span>
            </div>

            {/* At the length `docs/DESIGN-DECISIONS.md` named before this existed: 200 importable
                tags, where reading the list stops being how anyone finds the handful that apply. The
                list still never collapses and never paginates — a filter narrows what is *shown*, says
                so, and scopes both sweeps to it. */}
            {showsTagFilter(toImport.length) ? (
              <div className="tm-filter-row">
                <input
                  type="search"
                  className="tm-filter"
                  value={tagQuery}
                  placeholder={`Filter ${toImport.length.toLocaleString("en-US")} tags`}
                  aria-label="Filter tags"
                  onChange={(event) => setTagQuery(event.target.value)}
                  onKeyDown={(event) => {
                    // Escape means "back out of the filter" while there is one to back out of, and
                    // only then the shell's own dismissal. Stopped here so the modal's document
                    // listener and the pane's own handler both stay out of it.
                    if (event.key !== "Escape" || tagQuery === "") return;
                    event.stopPropagation();
                    setTagQuery("");
                  }}
                />
                {tagFilter.count ? <span className="tm-hint">{tagFilter.count}</span> : null}
              </div>
            ) : null}

            {/* A tick survives the filter that hid it, because the selection is a set of names and the
                filter only decides what renders. That is right, and it is the one way this could apply
                something the reviewer cannot see — so it is counted out loud. */}
            {tagFilter.hidden ? <div className="tm-filter-hidden">{tagFilter.hidden}</div> : null}

            {/* Every importable tag the filter leaves, however long the list runs. Importing tags is
                what this extension is for, so this list never collapses and never paginates — if it
                outgrows the window, the window scrolls. */}
            <div className="tm-chips">
              {shownTags.map((tag) => (
                <RelationRow
                  key={tag.name}
                  name={tag.name}
                  source={surprisingSource(tag)}
                  title={tag.source ?? tag.name}
                  matchesExisting={tag.matchesExisting}
                  alreadyApplied={tag.alreadyApplied}
                  checked={selection.tags.has(tag.name)}
                  onToggle={() => setSelection((set) => ({ ...set, tags: toggle(set.tags, tag.name) }))}
                />
              ))}
            </div>

            {onVideo.length ? (
              <>
                <button
                  type="button"
                  className="tm-disclose"
                  aria-expanded={showOnVideo}
                  onClick={() => setShowOnVideo((value) => !value)}
                >
                  <span className="tm-caret" aria-hidden="true">{showOnVideo ? "▾" : "▸"}</span>
                  {onVideo.length} already on this video
                  <span className="tm-hint">nothing to decide — shown for reference</span>
                </button>
                {showOnVideo ? (
                  <div className="tm-chips">
                    {onVideo.map((tag) => (
                      <RelationRow
                        key={tag.name}
                        name={tag.name}
                        source={surprisingSource(tag)}
                        title={tag.source ?? tag.name}
                        matchesExisting={tag.matchesExisting}
                        alreadyApplied={tag.alreadyApplied}
                        checked={false}
                        onToggle={() => undefined}
                      />
                    ))}
                  </div>
                ) : null}
              </>
            ) : null}
          </div>
        ) : null}
      </div>

      <div className="tm-foot">
        {pager ? (
          <span className="tm-pager">
            <button
              type="button"
              className="tm-btn is-small"
              title="Previous match (←)"
              aria-label="Previous match"
              aria-keyshortcuts="ArrowLeft"
              disabled={!pager.canPrev || pager.busy || busy}
              onClick={pager.onPrev}
            >
              ‹
            </button>
            <span className="tm-pager-count">
              {pager.busy ? <span className="tm-spinner" aria-hidden="true" /> : null}
              {pager.position}
            </span>
            <button
              type="button"
              className="tm-btn is-small"
              title="Next match (→)"
              aria-label="Next match"
              aria-keyshortcuts="ArrowRight"
              disabled={!pager.canNext || pager.busy || busy}
              onClick={pager.onNext}
            >
              ›
            </button>
          </span>
        ) : null}

        {/* A failed step is reported where the reviewer is looking, not on the page behind the
            backdrop. It outranks the apply summary because it is the newer news. */}
        {pager?.error ? (
          <span className="tm-status is-error">{pager.error}</span>
        ) : status ? (
          <span className={`tm-status${status.error ? " is-error" : ""}`}>{status.text}</span>
        ) : (
          <span className="tm-status" />
        )}

        {/* Not `disabled={busy}`: the click still has to be *received* mid-apply, or a reviewer
            behind a slow apply has no way out of the dialog at all. `requestClose` is what makes the
            deferral safe instead — see the comment on it and on `apply()`'s `closeRequestedRef` check
           . Escape and the backdrop click reach this same function through the imperative
            handle, not a copy of it. */}
        <button type="button" className="tm-btn" onClick={requestClose} disabled={closePending}>
          {closePending ? (
            <>
              <span className="tm-spinner" aria-hidden="true" />
              {CLOSE_PENDING_LABEL}
            </>
          ) : (
            "Close"
          )}
        </button>
        <button
          type="button"
          className="tm-btn is-primary"
          onClick={() => void apply()}
          disabled={busy || (pager?.busy ?? false)}
        >
          {busy ? (
            <>
              <span className="tm-spinner" aria-hidden="true" />
              Applying…
            </>
          ) : (
            // After an apply the list holds only what was declined or has arrived since, so the
            // button offers that rather than repeating an act already carried out.
            applyButtonLabel(applied !== null)
          )}
        </button>
      </div>

      {/* Under the footer rather than in it: the buttons are the control, and this says the same thing
          is available without the mouse. Rendered only where the keys are actually bound. */}
      {pager?.keyHint ? <div className="tm-keys">{pager.keyHint}</div> : null}
    </>
  );
});
