import React from "@cove/runtime/react";
import { ConfirmDialog } from "@cove/runtime/components";
import { batchApi, matchApi, type BatchOverview, type BatchRow, type TorrentMatchProposal } from "./api";
import { describeBulkApply, emptyTotals, foldApplyResult, shouldContinue } from "./bulkApply";
import { CoverImg } from "./CoverImg";
import { ReviewPane } from "./ReviewPane";
import {
  describeApplyScale,
  describeRowFilter,
  describeRowSweep,
  packFocusSummary,
  planApply,
  scopeRows,
  summariseApply,
  sweepRows,
  type ApplyMode,
  type RowScope,
} from "./review";
import {
  canStep,
  currentRow,
  describeQueuePosition,
  describeQueueRun,
  jumpToRow,
  keyStep,
  KEY_STEP_HINT,
  markApplied,
  openQueue,
  resyncQueue,
  rowKey,
  rowRef,
  stepQueue,
  wasApplied,
  type ReviewQueue,
} from "./queue";
import { isTorrentFile, NOT_TORRENTS_IN_DROP } from "./upload";
import { folderChangeNotice, type FolderStateReport } from "./folderState";
import { emptyStateMessage } from "./emptyState";
import { overviewSummary } from "./overviewSummary";
import { reloadStatus } from "./reloadStatus";
import { ensureStyles } from "./styles";

const { useCallback, useEffect, useMemo, useRef, useState } = React;

function StatusBadge({ status, fanOut }: { status: string; fanOut: number }) {
  // "updated" is applied, so it is not styled as work waiting — but it is not dimmed like a finished
  // row either, because the whole point is that it has something left to give.
  const className =
    status === "applied" ? "tm-pill is-applied"
    : status === "updated" ? "tm-pill is-updated"
    : "tm-pill is-matched";
  return (
    <span className="tm-pill-group">
      <span className={className}>{status}</span>
      {fanOut > 1 ? <span className="tm-pill is-pack" title={`Shared across ${fanOut} video files`}>pack ×{fanOut}</span> : null}
    </span>
  );
}

export function TorrentBatchPage() {
  ensureStyles();

  const [overview, setOverview] = useState<BatchOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  /**
   * Which long operation owns the page, or null.
   *
   * This was a boolean that upload and bulk apply both drove and neither read, so a drop during a run
   * re-enabled Apply mid-run — two overlapping chunked runs — and the upload's own `load()` replaced
   * `rows` under `plan` while the run was still slicing it. Naming the operation makes the
   * collision unrepresentable rather than merely guarded: there is no value here that means "both".
   */
  const [busy, setBusy] = useState<"upload" | "apply" | null>(null);
  /**
   * The same fact, where an entry guard can actually read it.
   *
   * State is a snapshot of the render that produced the handler, so two events arriving in one frame
   * would both see `null` and both proceed. The ref is what serialises them; the state is what the
   * controls render from.
   */
  const running = useRef(false);
  const [status, setStatus] = useState<string | null>(null);
  const [hideApplied, setHideApplied] = useState(true);
  // What the reviewer is looking for. At 715 rows the checkboxes are enough; at four figures they are
  // looking for one release, and the walk is built from what is visible — so this is also how a walk
  // gets aimed.
  const [rowQuery, setRowQuery] = useState("");
  // The two pack views. `packsOnly` is the sitting bulk apply deliberately refuses to serve;
  // `packFocus` is one release's own rows, which are one tag list being divided across scenes and are
  // judged together rather than as neighbours that happen to be adjacent.
  const [packsOnly, setPacksOnly] = useState(false);
  const [packFocus, setPackFocus] = useState<string | null>(null);
  /**
   * Rows ticked for a bulk apply, by row key.
   *
   * Never cleared after a run, deliberately: `planApply` reads the refreshed rows, so a row that
   * applied stops being `matched` and leaves the plan by itself, while a run that stopped partway
   * leaves the rest of the selection exactly where the reviewer left it.
   */
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [createNewTags, setCreateNewTags] = useState(false);
  const [includePacks, setIncludePacks] = useState(false);
  const [importCovers, setImportCovers] = useState(false);
  const [confirming, setConfirming] = useState(false);
  // Applied in chunks so the count below is real progress rather than a spinner.
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const [dragging, setDragging] = useState(false);
  const [proposal, setProposal] = useState<TorrentMatchProposal | null>(null);
  // The walk the open review belongs to: a snapshot of the rows on screen plus a position in it
  //. Null when nothing is open.
  const [queue, setQueue] = useState<ReviewQueue | null>(null);
  const [stepping, setStepping] = useState(false);
  const [stepError, setStepError] = useState<string | null>(null);
  // The folder probe's last answer, held whole rather than pre-worded: two different lines read it —
  // the rescan nudge, and the empty state that has to name where a torrent goes. Null until it
  // has answered, and null for good if it failed.
  const [folderState, setFolderState] = useState<FolderStateReport | null>(null);
  const checkingFolders = useRef(false);
  const fileInput = useRef<HTMLInputElement | null>(null);
  /** Stamps each row-detail request so an overtaken one can be dropped rather than rendered. */
  const stepToken = useRef(0);

  /**
   * Asks the server whether the folders have changed since the index was built.
   *
   * Failures are swallowed on purpose. This is a nudge rather than something the page owes the user:
   * the rows either side of it loaded fine, and turning a failed stat sweep into the page's error
   * line would report a problem the user does not have. The guard stops a run of focus events
   * stacking sweeps over a slow network share.
   */
  const checkFolders = useCallback(async () => {
    if (checkingFolders.current) return;
    checkingFolders.current = true;
    try {
      setFolderState(await batchApi.folderState());
    } catch {
      // Deliberately silent — see above.
    } finally {
      checkingFolders.current = false;
    }
  }, []);

  /**
   * Loads the overview. `rescan` re-reads the watched folder first, which is what picks up torrents
   * copied in by hand — an upload reindexes itself, but a plain file copy has nothing to trigger it.
   */
  const load = useCallback(async (rescan = false) => {
    setError(null);
    try {
      if (rescan) {
        // What the line says is `reloadStatus`'s decision, not this component's: missing folders, the
        // index cap and the per-reason skip counts are each the only place their state surfaces at all
        //, and a branch none of them can be tested through is where one gets dropped.
        setStatus(reloadStatus(await batchApi.reload()));
        // The rescan just reset what the server compares against, so the notice this page may have
        // been showing is now answered. Re-asked rather than assumed cleared: a folder that went
        // missing during the walk still has something to say.
        void checkFolders();
      }
      setOverview(await batchApi.list());
    } catch (caught) {
      setError((caught as Error).message);
      setOverview({ rows: [], unmatched: 0, videosMatchableByName: 0, indexedFiles: 0, torrents: 0 });
    }
  }, [checkFolders]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    void checkFolders();
    // Copying torrents in and coming back to the tab is the whole scenario this answers, so a check
    // on mount alone would miss it by one page view — the user would return to the same page that
    // was already open and see nothing.
    const onFocus = () => void checkFolders();
    window.addEventListener("focus", onFocus);
    return () => window.removeEventListener("focus", onFocus);
  }, [checkFolders]);

  const rows = overview?.rows ?? null;

  // Null the overwhelming majority of the time, which is what it renders as.
  const folderNotice = useMemo(
    () => (folderState ? folderChangeNotice(folderState) : null),
    [folderState],
  );

  const onPage = useMemo(
    () => (rows ?? []).filter((row) => !(hideApplied && row.status === "applied")),
    [rows, hideApplied],
  );
  const scope: RowScope = useMemo(
    () => ({ query: rowQuery, packsOnly, pack: packFocus }),
    [rowQuery, packsOnly, packFocus],
  );
  const visible = useMemo(() => scopeRows(onPage, scope), [onPage, scope]);
  const rowFilter = describeRowFilter(visible.length, onPage.length);

  const counts = useMemo(() => {
    const all = rows ?? [];
    return {
      total: all.length,
      matched: all.filter((row) => row.status === "matched").length,
      applied: all.filter((row) => row.status === "applied").length,
      updated: all.filter((row) => row.status === "updated").length,
      // Not derivable from the rows any more: the server counts these instead of sending them.
      noMatch: overview?.unmatched ?? 0,
      // Not derivable from the rows either, and in a different unit from every figure beside it —
      // `overviewSummary` is where that is spelled out.
      matchableByName: overview?.videosMatchableByName ?? 0,
      indexed: overview?.indexedFiles ?? 0,
      torrents: overview?.torrents ?? 0,
      packs: all.filter((row) => row.status === "matched" && row.fanOut > 1).length,
    };
  }, [rows, overview]);

  /**
   * Claims the page for one operation, or refuses because another already holds it.
   *
   * Both long operations go through this. Guarding only one of them would leave the collision
   * reachable from the other side, which is how the defect was live in both directions at once.
   */
  const claim = (operation: "upload" | "apply") => {
    if (running.current) return false;
    running.current = true;
    setBusy(operation);
    return true;
  };

  const release = () => {
    running.current = false;
    setBusy(null);
  };

  const upload = async (files: FileList | File[]) => {
    const torrents = [...files].filter(isTorrentFile);
    if (torrents.length === 0) {
      setStatus(NOT_TORRENTS_IN_DROP);
      return;
    }
    // A drop mid-run used to start a second chunked run beside the first, and reload the rows the
    // first was still working through.
    if (!claim("upload")) return;

    setStatus(`Uploading ${torrents.length}…`);
    try {
      // Sent in chunks, so a folder-sized drop reports movement instead of one long silence — and,
      // before that, instead of a bare 413 from the host's default body limit.
      const result = await batchApi.upload(torrents, (sent, total) =>
        setStatus(sent < total ? `Uploading ${sent} of ${total}…` : `Reading ${total}…`),
      );
      setStatus(
        `Added ${result.saved} torrent${result.saved === 1 ? "" : "s"}; indexed ${result.files} video files.` +
          (result.rejected.length ? ` Rejected: ${result.rejected.join("; ")}` : ""),
      );
      await load();
      // The upload endpoint rebuilds the index, so whatever the notice was saying has been answered.
      void checkFolders();
    } catch (caught) {
      setStatus((caught as Error).message);
    } finally {
      release();
    }
  };

  /**
   * What pressing *Apply* would cover — the ticked rows, or every eligible row on screen when nothing
   * is ticked.
   *
   * The label, the confirm and the request are all built from this one list, because the label *is*
   * the specification of what the run does and two derivations of it drift (the write folder's rule, applied
   * here). The request now names rows rather than videos, so a selection means exactly what it says:
   * two torrents can describe one file, and a video id named both.
   */
  const plan = useMemo(
    () => planApply({ all: rows ?? [], visible, selected, includePacks }),
    [rows, visible, selected, includePacks],
  );

  // What the header checkbox sweeps: the rows on screen that have something left to apply. Packs are
  // in — ticking one is how a reviewer consents to it, one release at a time.
  const selectable = useMemo(() => visible.filter((row) => row.status === "matched"), [visible]);
  const allSelected = selectable.length > 0 && selectable.every((row) => selected.has(rowKey(row)));

  /**
   * Ticks or unticks one row.
   *
   * By `rowKey`, which is the video plus the torrent describing it. Keyed on the old
   * `torrentName/fileName` pair, a pack holding two same-named scenes collapsed into one tick that
   * applied both videos — so this wiring had to wait for that key to become real.
   */
  const toggleRow = (row: BatchRow) =>
    setSelected((current) => {
      const next = new Set(current);
      if (!next.delete(rowKey(row))) next.add(rowKey(row));
      return next;
    });

  /**
   * Keeps the walk pointed at what is on screen.
   *
   * The queue is a snapshot, and with the list beside the review rather than behind a backdrop its
   * filters are reachable during a walk — so the rows can change under a walk that was frozen when it
   * started. `resyncQueue` re-anchors on the row being reviewed and keeps the applied record; if a
   * filter has hidden that row the walk becomes one of one, which is honest, rather than an index
   * into a list that no longer holds it. The review itself is never closed or swapped by a filter
   *.
   */
  useEffect(() => {
    setQueue((current) => (current === null ? current : resyncQueue(current, visible)));
  }, [visible]);

  const applyMode: ApplyMode = { createNewTags, importCovers };

  // Whether the page is showing the review instead of the table. What hangs on it is which of the
  // page's own controls still mean anything: the ticks the bulk apply acts on live in the table, and
  // the table is not on screen — so an "Apply to 12" beside a review with no checkbox in sight offers
  // a run against a list the user cannot see or change. `ReviewBody`'s own footer carries the apply
  // that *is* in scope here, which is the one for the row being reviewed.
  const inReview = proposal !== null;

  // The row the pane is showing, as the key the list marks itself against. Read from the walk rather
  // than from the proposal so the two can never disagree about which row is under review.
  const reviewing = useMemo(() => {
    const row = queue === null ? null : currentRow(queue);
    return row === null ? null : rowKey(row);
  }, [queue]);

  /**
   * Applies in chunks so the user sees real progress.
   *
   * One request for everything is a single long silence — over two minutes on a library this size,
   * with no way to tell working from hung. Chunking costs some repeated setup per request and buys a
   * count that moves.
   */
  const applyBulk = async () => {
    setConfirming(false);
    const target = plan.rows;
    if (target.length === 0) {
      setStatus("Nothing eligible to apply.");
      return;
    }
    // The other half of that: an upload landing mid-run reloads `rows`, and `plan` — which this run is
    // still slicing — is derived from them.
    if (!claim("apply")) return;

    // Smaller chunks when covers are involved. Each cover is paced to roughly one request a second
    // per host, so a chunk of ten is ten seconds of silence — which is exactly the "is this
    // hung?" the chunking exists to avoid. Three keeps the count moving at a readable rate.
    const CHUNK = importCovers ? 3 : 10;
    let totals = emptyTotals();
    // The client's own failure, kept apart from the rows the server reported: a chunk that throws
    // means the run stopped being observed, not that a row was rejected.
    let halted: string | null = null;

    setProgress({ done: 0, total: target.length });
    try {
      for (let index = 0; index < target.length; index += CHUNK) {
        const slice = target.slice(index, index + CHUNK);
        try {
          totals = foldApplyResult(totals, await batchApi.apply({
            rows: slice.map(rowRef),
            createNewTags,
            includePacks,
            importCovers,
          }));
        } catch (caught) {
          // Every chunk before this one is already committed. Reporting only the error — which is what
          // this once did — throws away the count of what was written and leaves the user
          // guessing at the state of their library.
          halted = (caught as Error).message;
          break;
        }
        setProgress({ done: Math.min(index + CHUNK, target.length), total: target.length });
        // The breaker is the server's, but honouring it is ours: it resets per request, so a client
        // that keeps slicing turns "stop after five" into "five per chunk, for the whole selection".
        if (!shouldContinue(totals)) break;
      }

      setStatus(describeBulkApply(totals, halted));
    } finally {
      // Outside the try that reports, and unconditional. Rows are committed per row, so the table is
      // stale after a failed run exactly as it is after a clean one — and it was the failed one that
      // never refreshed, because this call used to sit after the status line inside the try.
      await load();
      release();
      setProgress(null);
    }
  };

  /**
   * Puts a row under review, and points the walk at it.
   *
   * A row *is* a `(torrent, file)` pair — it is keyed on one — so both are passed rather than letting
   * the server re-search by size. 2.32% of file sizes are shared and 20 of the real library's files
   * match more than one torrent, so a size search can answer with a different torrent than the row
   * displays: other tags, other title, other fan-out badge than the one just read.
   *
   * This does not make the proposal claim it was forced. `MatchAsync` reports "your selection" only
   * when no file size agrees, and a batch row exists precisely because one does — so these still read
   * "matched on file size", truthfully.
   *
   * The frame is held while the proposal is fetched rather than blanked: the walk has already moved,
   * and an empty pane between two rows reads as a bug. A step that fails puts the walk back where it
   * was and says so inside the review, next to where the reviewer is looking — unless there was no
   * review yet, in which case there is nowhere in it to say anything and the page says it instead.
   */
  const show = async (row: BatchRow, next: ReviewQueue) => {
    const previous = queue;
    // Rows stay clickable while a proposal is loading — deliberately, since waiting for a slow row
    // before allowing the next click is worse than arriving out of order. So the ordering has to be
    // handled rather than prevented: a slower earlier response could otherwise render proposal A while
    // the walk points at B, and Apply writes A's tags to B's video.
    const token = ++stepToken.current;
    const current = () => token === stepToken.current;

    setQueue(next);
    setStepError(null);
    setStepping(true);
    try {
      const proposal = await matchApi.match(row.videoId, row.torrentName, row.fileName);
      if (!current()) return;
      setProposal(proposal);
    } catch (caught) {
      // A stale failure is also dropped. Restoring `previous` here would rewind the walk past the row
      // the reviewer has since opened, and report an error about a row they have already left.
      if (!current()) return;
      setQueue(previous);
      if (previous === null) setStatus((caught as Error).message);
      else setStepError((caught as Error).message);
    } finally {
      // Only the newest request owns the spinner; an overtaken one clearing it would say the walk had
      // arrived while the request it is waiting for is still out.
      if (current()) setStepping(false);
    }
  };

  /**
   * Opens the row the user clicked, in the table or in the list beside an open review.
   *
   * The queue is the rows on screen, filters included — not `eligible`, which drops packs, and a pack
   * is exactly the row that has to be reviewed one at a time. A click during a walk *jumps* rather
   * than starting a new one: the applied set is what the refresh on close and the run summary are
   * built from, and `openQueue` would throw it away.
   */
  const openRow = (row: BatchRow) =>
    show(row, queue === null ? openQueue(visible, row) : jumpToRow(queue, row));

  /**
   * The same two arrows, without the mouse.
   *
   * Bound while a review is open and nowhere else, and only ever a *modifier* on the footer's own
   * buttons — an undiscoverable-only route is not a control, which is why this went in after the pane
   * rather than instead of it. Which keys move a walk, and the two cases that must not, are
   * `keyStep`'s: nothing fires while the reviewer is typing, because both filters on this page are
   * text boxes a step away from the list they filter, and a modified key belongs to the browser.
   *
   * Re-bound as the walk moves, so the listener always closes over the queue it is stepping.
   */
  useEffect(() => {
    if (proposal === null) return;

    const onKey = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      const delta = keyStep({
        key: event.key,
        withModifier: event.ctrlKey || event.metaKey || event.altKey,
        typing:
          target !== null
          && (target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName)),
      });
      if (delta === null || stepping) return;

      // Only once it is ours: an arrow that moves the walk must not also scroll the pane it moved.
      event.preventDefault();
      void step(delta);
    };

    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [proposal, queue, stepping]);

  /** Moves the open review one row along the walk. */
  const step = (delta: number) => {
    if (queue === null) return;

    const next = stepQueue(queue, delta);
    const row = currentRow(next);
    if (next === queue || row === null) return;

    return show(row, next);
  };

  /**
   * Ends the walk, and refreshes the list once if it changed anything.
   *
   * Once, on the way out — not per apply. The table is behind a backdrop for the whole walk, so a
   * refresh after each row would refetch the entire overview to update something nobody can see, and
   * with "Hide applied" on it would delete the row being reviewed out from under the queue.
   */
  const closeReview = () => {
    const summary = queue === null ? null : describeQueueRun(queue);

    setProposal(null);
    setQueue(null);
    setStepError(null);

    if (summary !== null) {
      setStatus(summary);
      void load();
    }
  };

  return (
    <div
      className={`tm-page${dragging ? " is-dragging" : ""}`}
      onDragOver={(event) => {
        event.preventDefault();
        setDragging(true);
      }}
      onDragLeave={() => setDragging(false)}
      onDrop={(event) => {
        event.preventDefault();
        setDragging(false);
        void upload(event.dataTransfer.files);
      }}
    >
      <div className="tm-page-head">
        <div>
          <h2 className="tm-page-title">Torrent Matches</h2>
          <p className="tm-sub">
            {overviewSummary(rows === null ? null : counts)}
          </p>
        </div>
        <div className="tm-page-actions">
          <button type="button" className="tm-btn" disabled={busy !== null} onClick={() => fileInput.current?.click()}>
            Add torrents…
          </button>
          {/* The second door into the same state. Clicking a row is the first, and it means finding a
              row to start on — which is the whole friction for someone whose sitting is "go through
              these one at a time" rather than "apply the safe ones". Withdrawn once a review
              is open: the walk is already running, and the list beside it is how a different row is
              picked. */}
          {inReview ? null : (
            <button
              type="button"
              className="tm-btn"
              disabled={busy !== null || visible.length === 0}
              title="Open the first row and walk them one at a time"
              onClick={() => void openRow(visible[0])}
            >
              Review one by one
            </button>
          )}
          <input
            ref={fileInput}
            type="file"
            accept=".torrent"
            multiple
            style={{ display: "none" }}
            onChange={(event) => {
              if (event.target.files) void upload(event.target.files);
              event.target.value = "";
            }}
          />
          {/* Withdrawn for the length of a review, with the table it acts on. It is the *bulk* apply —
              its scope is the ticked rows, or every eligible row on screen when nothing is ticked —
              and neither is visible from inside a review, so it named a count nothing on the page
              accounted for. The ticks survive; closing the review brings both back. */}
          {inReview ? null : (
            <button type="button" className="tm-btn is-primary" disabled={busy !== null || plan.rows.length === 0} onClick={() => setConfirming(true)}>
              {progress ? (
                <>
                  <span className="tm-spinner" aria-hidden="true" />
                  Applying {progress.done}/{progress.total}…
                </>
              ) : (
                plan.label
              )}
            </button>
          )}
        </div>
      </div>

      {/* Ticked rows apply whether the filter shows them or not, so the ones it is hiding are counted
          out loud rather than discovered afterwards. `planApply` words it, because the label and this
          sentence have to describe the same list. */}
      {plan.hidden && !inReview ? <p className="tm-notice">{plan.hidden}</p> : null}

      <div className="tm-controls">
        {/* What the list shows, and therefore what a walk covers — the row filter, the packs and the
            applied. Kept together and ahead of the apply options, because *Include packs* is about
            what a bulk run may touch while *Packs only* is about what is on screen, and two controls
            with the same word in them should at least not read as one pair. */}
        <input
          type="search"
          className="tm-filter"
          value={rowQuery}
          placeholder="Filter rows"
          aria-label="Filter rows by video, torrent or file name"
          onChange={(event) => setRowQuery(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === "Escape") setRowQuery("");
          }}
        />
        <label className="tm-check">
          <input type="checkbox" checked={packsOnly} onChange={() => setPacksOnly((value) => !value)} />
          Packs only
          {counts.packs ? <span className="tm-hint"> ({counts.packs} to apply)</span> : null}
        </label>
        <label className="tm-check">
          <input type="checkbox" checked={hideApplied} onChange={() => setHideApplied((value) => !value)} />
          Hide applied
        </label>
        {rowFilter ? <span className="tm-hint">{rowFilter}</span> : null}

        <span className="tm-controls-sep" aria-hidden="true" />

        <label className="tm-check">
          <input type="checkbox" checked={createNewTags} onChange={() => setCreateNewTags((value) => !value)} />
          Create new tags <span className="tm-hint">(off = only tags your library already has)</span>
        </label>
        <label className="tm-check">
          <input type="checkbox" checked={includePacks} onChange={() => setIncludePacks((value) => !value)} />
          Include packs <span className="tm-hint">(in a bulk apply)</span>
        </label>
        <label className="tm-check">
          <input type="checkbox" checked={importCovers} onChange={() => setImportCovers((value) => !value)} />
          Import covers <span className="tm-hint">(replaces existing artwork)</span>
        </label>
        <button
          type="button"
          className="tm-btn"
          disabled={busy !== null}
          title="Re-read the torrent folder and refresh"
          onClick={() => void load(true)}
        >
          Rescan folder
        </button>
      </div>

      {includePacks ? (
        <div className="tm-warn">
          Pack metadata is the union of every scene in the torrent, so bulk-applying it tags each video
          with the others' content. Reviewing packs individually is usually what you want.
        </div>
      ) : null}

      {progress && importCovers ? (
        <div className="tm-notice">
          Covers are fetched about one a second per image host, so this takes a while on a large
          selection. That pace is deliberate — it keeps the extension looking like someone browsing
          rather than a script, which is the condition it is allowed to fetch covers under. Already
          imported covers are reused and cost no request at all.
        </div>
      ) : null}
      {/* Above the rescan result rather than below it: this is the reason to press the button, and
          `status` is what happened when you did. */}
      {folderNotice ? <div className="tm-warn">{folderNotice}</div> : null}
      {status ? <div className="tm-notice">{status}</div> : null}
      {/* Counted over every row of this torrent rather than over the page, because the progress is the
          pack's and not the view's — *Hide applied* must not make a set look less finished than it is. */}
      {/* Ticked rows the current scope is not showing. The same obligation the tag filter carries: a
          selection is the reviewer's statement and a filter is only a view, so what it hides gets
          counted rather than discovered after the run. */}
      {plan.hidden && !inReview ? <div className="tm-filter-hidden">{plan.hidden}</div> : null}
      {packFocus ? (
        <div className="tm-focus">
          <span className="tm-focus-name" title={packFocus}>{packFocus}</span>
          <span className="tm-hint">{packFocusSummary(rows ?? [], packFocus)}</span>
          <button type="button" className="tm-btn is-small" onClick={() => setPackFocus(null)}>
            Show all rows
          </button>
        </div>
      ) : null}
      {error ? <div className="tm-notice is-error">{error}</div> : null}

      {/* Reviewing narrows the table into a list beside the review rather than throwing the review
          over it. Nothing is lost for good: closing brings the whole table back. What the
          list keeps is what picks the next row — the video's name, the torrent's, what it would add,
          and the walk's own mark. */}
      {proposal ? (
        <div className="tm-split">
          <div className="tm-list">
            {visible.map((row) => {
              const current = reviewing !== null && reviewing === rowKey(row);
              return (
                <button
                  type="button"
                  key={rowKey(row)}
                  className={`tm-lrow${current ? " is-current" : ""}`}
                  aria-current={current ? "true" : undefined}
                  onClick={() => void openRow(row)}
                >
                  {/* The library's own artwork, and never the torrent's: that one is a comparison, it
                      happens in the pane at a size where it means something, and each costs a paced
                      request through the proxy. This one is local. Asked rather than discovered — a
                      video with no artwork used to answer with a 404 the browser logs, per row. */}
                  {row.videoHasImage ? (
                    <img src={`/api/videos/${row.videoId}/image?max=120`} alt="" loading="lazy" title="in library" />
                  ) : (
                    <span className="tm-lrow-thumb" title="no artwork in your library" />
                  )}
                  <span className="tm-lrow-main">
                    {/* The video leads, because that is what the pane's header is named after. */}
                    <span className="tm-lrow-title">{row.videoTitle ?? `Video ${row.videoId}`}</span>
                    <span className="tm-lrow-sub" title={`${row.torrentName} · ${row.fileName}`}>{row.torrentName}</span>
                  </span>
                  {/* What this walk applied. An act, not a status: the row's own state is the server's
                      to decide and does not move until the refresh on close. */}
                  {queue !== null && wasApplied(queue, row) ? (
                    <span className="tm-lmark" title="applied in this walk">✓</span>
                  ) : null}
                  {/* Only the statuses worth reading. "matched" is every row here by default, so a
                      pill saying it on all of them is noise; "applied" and "updated" are news. */}
                  {row.status !== "matched" ? <span className={`tm-pill is-${row.status}`}>{row.status}</span> : null}
                  {row.fanOut > 1 ? (
                    <span className="tm-pill is-pack" title={`Shared across ${row.fanOut} video files`}>×{row.fanOut}</span>
                  ) : null}
                  <span className="tm-lnum" title="tags this torrent would add to this video">
                    {row.tagsToAdd}
                    {row.tagsToCreate ? <span className="tm-new">+{row.tagsToCreate}</span> : null}
                  </span>
                </button>
              );
            })}
          </div>

          <ReviewPane
            // Keyed on the row, so stepping the walk remounts rather than re-renders. The review seeds
            // its selection at mount; swapping the proposal underneath it would carry one video's ticks
            // into the next, and `buildApplyRequest` decides what the server may change.
            key={`${proposal.torrentName}/${proposal.fileName}`}
            proposal={proposal}
            onClose={closeReview}
            // The review stays open and reports the result itself — this caller records the
            // row and nothing else. What that row *becomes*, `applied` or `updated`, is the server's to
            // decide from the torrent's own tag count; re-deriving it here would be a second
            // copy of that rule. The refresh on close asks instead.
            onApplied={() => setQueue((current) => (current === null ? current : markApplied(current)))}
            // Offered from inside the review because that is where a pack is recognised — you are
            // looking at one scene of it. It narrows the list rather than the review: the row under
            // review is in the pack, so the walk re-anchors onto its siblings and carries on.
            onFocusPack={() => setPackFocus(proposal.torrentName)}
            pager={
              queue === null
                ? undefined
                : {
                    position: describeQueuePosition(queue),
                    canPrev: canStep(queue, -1),
                    canNext: canStep(queue, 1),
                    onPrev: () => void step(-1),
                    onNext: () => void step(1),
                    busy: stepping,
                    error: stepError,
                    keyHint: KEY_STEP_HINT,
                  }
            }
          />
        </div>
      ) : (
        <table className="tm-table">
          <thead>
            <tr>
              {/* The sweep is scoped to the rows on screen and says so, which is the rule a filtered
                  list imposes on any control beside it. `describeRowSweep` owns that sentence. */}
              <th className="tm-tick">
                <input
                  type="checkbox"
                  checked={allSelected}
                  disabled={selectable.length === 0}
                  aria-label={describeRowSweep(selectable.length, allSelected)}
                  title={describeRowSweep(selectable.length, allSelected)}
                  onChange={(event) =>
                    setSelected((current) => sweepRows(current, visible, event.target.checked))
                  }
                />
              </th>
              <th>Torrent</th>
              <th>Matched video</th>
              <th className="tm-num">Current tags</th>
              <th className="tm-num">Would add</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {visible.map((row) => (
              <tr
                key={rowKey(row)}
                className="is-clickable"
                onClick={() => void openRow(row)}
              >
                {/* stopPropagation so ticking a row does not also open it for review. */}
                <td className="tm-tick" onClick={(event) => event.stopPropagation()}>
                  <input
                    type="checkbox"
                    checked={selected.has(rowKey(row))}
                    disabled={row.status !== "matched"}
                    aria-label={`Include ${row.torrentName} in a bulk apply`}
                    title={
                      row.status === "matched"
                        ? "Include this row in a bulk apply"
                        : "Nothing left to apply on this row"
                    }
                    onChange={() => toggleRow(row)}
                  />
                </td>
                <td>
                  <div className="tm-name" title={row.torrentName}>{row.torrentName}</div>
                  <div className="tm-hint tm-name" title={row.fileName}>{row.fileName}</div>
                </td>
                <td>
                  <div className="tm-video">
                    {/* Library cover beside the torrent's, so a size match can be eyeballed. */}
                    <div className="tm-video-covers">
                      {/* Hidden and not collapsed, unlike the header covers: this is a fixed 56px
                          slot, and dropping it would slide the torrent's thumbnail into the library
                          cover's place, so a row with no artwork would read as a row whose one
                          thumbnail is the library's. */}
                      {/* Asked, not discovered: a video with no artwork used to answer this request
                          with a 404 the browser logs, once per row. */}
                      {row.videoHasImage ? (
                        <img src={`/api/videos/${row.videoId}/image?max=120`} alt="" loading="lazy" title="in library" />
                      ) : (
                        <span className="tm-thumb" title="no artwork in your library" />
                      )}
                      {/* Only when the host is on the operator's list — the proxy refuses the rest,
                          and the dialog is where allowing a host happens. */}
                      {row.torrentCoverUrl && row.torrentCoverAllowed ? (
                        <CoverImg url={row.torrentCoverUrl} className="tm-thumb" title="from torrent" />
                      ) : null}
                    </div>
                    <span className="tm-name" title={row.videoTitle ?? ""}>{row.videoTitle ?? `Video ${row.videoId}`}</span>
                    {/* stopPropagation so following the link does not also open the review dialog. */}
                    <a
                      className="tm-link"
                      href={`/video/${row.videoId}`}
                      target="_blank"
                      rel="noreferrer"
                      title="Open video page"
                      onClick={(event) => event.stopPropagation()}
                    >
                      ↗
                    </a>
                  </div>
                </td>
                <td className="tm-num">{row.videoTagCount}</td>
                {/* The total first, because that is what the reviewer is deciding about; the created
                    count is a detail of it rather than a second bucket beside it. Showing the two as
                    `existing +new` was what let the column total the whole tag list and never fall
                    when the tags landed. */}
                <td className="tm-num">
                  <span title="tags this torrent would add to this video">{row.tagsToAdd}</span>
                  {row.tagsToCreate ? (
                    <span className="tm-new" title="of those, this many do not exist in your library yet">
                      {" "}
                      {row.tagsToCreate} new
                    </span>
                  ) : null}
                  {/* Shown rather than merely sent. The server has carried a performer number since the
                      batch page existed and nothing ever rendered it, which is how it went on counting
                      the torrent's performers instead of the video's without anyone noticing. A
                      row can now read "0" for tags and still be worth opening. */}
                  {row.performersToAdd ? (
                    <span className="tm-sub" title="performers this torrent would add to this video">
                      {" "}
                      +{row.performersToAdd}p
                    </span>
                  ) : null}
                </td>
                <td><StatusBadge status={row.status} fanOut={row.fanOut} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {rows !== null && visible.length === 0 ? (
        <p className="tm-empty">
          {emptyStateMessage({
            scope,
            onPage: onPage.length,
            indexed: counts.indexed,
            torrents: counts.torrents,
            total: counts.total,
            folderState,
          })}
        </p>
      ) : null}

      <ConfirmDialog
        open={confirming}
        title="Apply torrent metadata"
        message={describeApplyScale(summariseApply(plan.rows, applyMode), applyMode)}
        confirmLabel="Apply"
        destructive={false}
        isPending={busy === "apply"}
        onConfirm={() => void applyBulk()}
        onCancel={() => setConfirming(false)}
      />

    </div>
  );
}
