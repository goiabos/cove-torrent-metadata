import React from "@cove/runtime/react";
import { batchApi, matchApi, writeFolderApi, type Settings } from "./api";
import { describeFolderAdd, describeHostAdd } from "./listEdit";
import {
  FOLDER_PAGE,
  afterRemoval,
  canToggleCap,
  filterTorrents,
  folderCount,
  folderSectionState,
  folderTorrentState,
  planRemoval,
  wipeLabel,
  type FolderTorrent,
  type RemovalPlan,
} from "./writeFolder";
import { TAG_STYLES } from "./naming";
import { ensureStyles } from "./styles";

const { useCallback, useEffect, useMemo, useRef, useState } = React;

/**
 * One of the panel's two list settings — cover hosts, source folders.
 *
 * Shared because both work identically: the server normalises and de-duplicates on write, so the
 * editor sends what was typed and renders whatever came back. Neither holds a copy of the rule that
 * decides what an entry means (see `listEdit.ts`).
 */
function ListEditor({
  label,
  items,
  placeholder,
  empty,
  removeTitle,
  busy,
  onAdd,
  onRemove,
}: {
  label: string;
  items: string[] | null;
  placeholder: string;
  empty: string;
  removeTitle: (item: string) => string;
  busy: boolean;
  onAdd: (value: string) => void;
  onRemove: (item: string) => void;
}) {
  const [draft, setDraft] = useState("");

  const add = () => {
    const typed = draft.trim();
    if (typed === "") return;
    onAdd(typed);
    setDraft("");
  };

  return (
    <div className="tm-panel-field tm-panel-field-block">
      <span className="tm-field-label">{label}</span>
      {items === null ? (
        <span className="tm-hint">Loading…</span>
      ) : items.length === 0 ? (
        <span className="tm-hint">{empty}</span>
      ) : (
        <ul className="tm-hosts">
          {items.map((item) => (
            <li key={item} className="tm-host">
              <code className="tm-code">{item}</code>
              <button
                type="button"
                className="tm-host-remove"
                disabled={busy}
                title={removeTitle(item)}
                onClick={() => onRemove(item)}
              >
                ×
              </button>
            </li>
          ))}
        </ul>
      )}
      <div className="tm-host-add">
        <input
          className="tm-input"
          type="text"
          value={draft}
          placeholder={placeholder}
          disabled={busy || items === null}
          onChange={(event) => setDraft(event.target.value)}
          onKeyDown={(event) => {
            // Enter submits, because a single text field beside a button is a form whether or not it
            // is marked up as one — and this panel sits inside the host's own <form>-less card.
            if (event.key === "Enter") {
              event.preventDefault();
              add();
            }
          }}
        />
        <button type="button" className="tm-btn" disabled={busy || draft.trim() === ""} onClick={add}>
          Add
        </button>
      </div>
    </div>
  );
}

/**
 * The pill for one torrent's state. Two of them where a pack's fraction needs explaining.
 *
 * Reuses the batch page's own colours rather than inventing a second vocabulary: the accent means work
 * waiting there and means it here, teal means applied-but-not-finished in both, and yellow has meant
 * "pack" since the overview had rows.
 */
const STATE_CLASS: Record<string, string> = {
  unreadable: "tm-pill is-bad",
  "no-video": "tm-pill",
  absent: "tm-pill",
  "to-apply": "tm-pill is-matched",
  partial: "tm-pill is-updated",
  applied: "tm-pill is-applied",
};

/**
 * What is in the one folder this extension writes, and the way to remove any of it.
 *
 * Reads the folder rather than the batch overview, and has to: the overview lists one row per *video
 * file*, so a pack is seventy rows and a torrent nothing matched is none at all — which is exactly the
 * pile a user wants to clear. It also carries no filenames, and a filename is what a removal names.
 *
 * Every decision here — what the filter admits, what the cap shows, what the count line then claims,
 * what a torrent's state is called, what the bulk button says it will take, and what the confirm says —
 * lives in `writeFolder.ts` under vitest. What is left in this component is fetching, the two pieces of
 * state a user can change, and markup.
 */
function FolderList({
  folder,
  busy,
  onBusy,
  onNotice,
  onError,
}: {
  folder: string | null;
  busy: boolean;
  onBusy: (busy: boolean) => void;
  onNotice: (message: string) => void;
  onError: (message: string) => void;
}) {
  const [torrents, setTorrents] = useState<FolderTorrent[] | null>(null);
  // How many the listing is about to hold, from the stat sweep behind `/folder-state` — 8 ms against
  // the second or more this listing costs, so it is what the wait can be labelled with. Null
  // until it answers, and null for good if it fails: a count is a nicety and the listing is the point.
  const [expected, setExpected] = useState<number | null>(null);
  const [query, setQuery] = useState("");
  const [capped, setCapped] = useState(true);
  const [plan, setPlan] = useState<RemovalPlan | null>(null);
  // A listing failure is this section's own state rather than the panel's shared notice —
  // that line is for what the user's own click just did, and this can fail on its own with nobody
  // having clicked anything.
  const [listingError, setListingError] = useState<string | null>(null);

  // Stamps every call so a response that is no longer wanted is dropped rather than applied. Without
  // it, a listing that overlaps another — two calls in flight at once — lets whichever answers last
  // win even when it is the older of the two, which is exactly how a removal already reflected locally
  // could be undone by a listing that started before the removal did.
  const generation = useRef(0);

  // Deliberately empty deps: this closes over nothing but its own setters, which React guarantees are
  // stable, so its identity never changes across a re-render — unlike before, when it depended on the
  // `onError` prop the parent recreated on every render, which defeated the parse-once goal by
  // re-firing the effect below on every parent render, including the one `confirm()` causes via
  // `onBusy(true)`.
  const load = useCallback(() => {
    const requested = ++generation.current;
    setListingError(null);
    return writeFolderApi
      .list()
      .then((listing) => {
        if (requested === generation.current) setTorrents(listing.torrents);
      })
      .catch((failure: Error) => {
        if (requested === generation.current) setListingError(failure.message);
      });
  }, []);

  useEffect(() => {
    void load();
    // Fired beside the listing rather than before it: it is there to label the wait, so making the
    // listing wait for it would be the wrong way round.
    batchApi
      .folderState()
      .then((state) => setExpected(state.folders.find((folder) => folder.writable)?.files ?? null))
      .catch(() => undefined);
  }, [load]);

  const section = folderSectionState(folder, torrents, listingError, expected);
  // `section.kind === "list"` is what the render below actually branches on, but it narrows only
  // `section`, not this separate `torrents` variable — so callers below read this instead of
  // `torrents.length` directly.
  const inFolder = torrents?.length ?? 0;
  const matches = useMemo(() => filterTorrents(torrents ?? [], query), [torrents, query]);
  const shown = capped ? matches.slice(0, FOLDER_PAGE) : matches;
  const bulk = wipeLabel(matches.length, query);

  const confirm = async () => {
    if (plan === null) return;
    onBusy(true);
    setPlan(null);
    try {
      const result = await writeFolderApi.remove(plan.files);

      // Dropped from the list rather than re-read from the folder. The re-read parses every file to
      // reflect one of them leaving, and `afterRemoval` decides when that is actually owed — which is
      // when something was refused, because then the list disagreed with the folder.
      const next = afterRemoval(torrents, plan.files, result.refused);
      if (next === null) await load();
      else setTorrents(next);
      onNotice(
        result.refused.length === 0
          ? `${result.removed} removed. ${result.torrents} torrents indexed.`
          : `${result.removed} removed, ${result.refused.length} refused: ${result.refused.join("; ")}`,
      );
    } catch (failure) {
      onError((failure as Error).message);
    } finally {
      onBusy(false);
    }
  };

  return (
    <div className="tm-panel-field tm-panel-field-block">
      <span className="tm-field-label">In the extension's folder</span>

      {section.kind === "not-configured" ? (
        <span className="tm-hint">Not configured.</span>
      ) : section.kind === "error" ? (
        <div className="tm-notice is-error">
          <span>{section.message}</span>{" "}
          <button type="button" className="tm-linkbtn" onClick={() => void load()}>
            Retry
          </button>
        </div>
      ) : section.kind === "loading" ? (
        <span className="tm-hint">{section.label}</span>
      ) : section.kind === "empty" ? (
        // The hint above already names the path and says they are kept, so this says only that the
        // folder is empty. Two sentences competing to explain one folder is how a panel stops being
        // read at all.
        <span className="tm-hint">Nothing here yet.</span>
      ) : (
        <>
          {/* Offered only once there is enough to make finding one a problem. Below that it is a
              control asking the user to solve a problem they do not have. */}
          {inFolder > FOLDER_PAGE ? (
            <input
              // Not a bare `tm-input`: this one is a direct child of a column flex container, where
              // that class's `flex: 1 1 220px` sizes the *height* rather than the width, and the
              // filter rendered as a text box seven lines tall. `tm-list-filter` sizes it on the axis
              // this context actually lays out.
              className="tm-input tm-list-filter"
              type="text"
              value={query}
              placeholder="Filter by name…"
              aria-label="Filter torrents by name"
              disabled={busy}
              onChange={(event) => {
                setQuery(event.target.value);
                // A new filter is a new list, so it starts capped again rather than dumping every
                // match of a broad search into the panel.
                setCapped(true);
              }}
            />
          ) : null}

          <ul className={`tm-torrents tm-scroll${capped ? "" : " is-open"}`}>
            {shown.map((torrent) => {
              const state = folderTorrentState(torrent);
              return (
                <li key={torrent.file} className="tm-torrent">
                  <span className="tm-torrent-main">
                    <span className="tm-torrent-file" title={torrent.file}>
                      {torrent.file}
                    </span>
                    <span className="tm-torrent-meta">
                      {torrent.name ?? "Will not parse"}
                      {torrent.videoFiles > 0
                        ? ` · ${torrent.videoFiles} video ${torrent.videoFiles === 1 ? "file" : "files"}`
                        : ""}
                    </span>
                  </span>
                  {state.isPack ? <span className="tm-pill is-pack">pack</span> : null}
                  <span className={STATE_CLASS[state.kind]}>{state.label}</span>
                  <button
                    type="button"
                    className="tm-host-remove"
                    disabled={busy}
                    title={`Remove ${torrent.file}`}
                    onClick={() => setPlan(planRemoval([torrent], inFolder))}
                  >
                    ×
                  </button>
                </li>
              );
            })}
          </ul>

          <div className="tm-foot-line">
            <span>{folderCount(shown.length, matches.length, query)}</span>
            {canToggleCap(matches.length, capped) ? (
              <button type="button" className="tm-linkbtn" disabled={busy} onClick={() => setCapped(!capped)}>
                {capped ? "Show all" : "Show fewer"}
              </button>
            ) : null}
            {bulk !== null ? (
              <button
                type="button"
                className="tm-btn is-danger tm-spacer"
                disabled={busy}
                // Built from `matches`, not `shown`: it takes every one the filter admits, which is
                // exactly what its own label says it will.
                onClick={() => setPlan(planRemoval(matches, inFolder))}
              >
                {bulk}
              </button>
            ) : null}
          </div>
        </>
      )}

      {plan !== null ? (
        <div className="tm-backdrop" onClick={(event) => event.target === event.currentTarget && setPlan(null)}>
          <div className="tm-modal is-compact" role="dialog" aria-modal="true">
            <div className="tm-head">
              <div className="tm-head-main">
                <h3 className="tm-title">{plan.title}</h3>
              </div>
            </div>
            <div className="tm-body tm-confirm-lines">
              {plan.lines.map((line) => (
                <p key={line}>{line}</p>
              ))}
            </div>
            <div className="tm-foot">
              <button type="button" className="tm-btn" onClick={() => setPlan(null)}>
                Keep {plan.files.length === 1 ? "it" : "them"}
              </button>
              <button type="button" className="tm-btn is-danger" onClick={() => void confirm()}>
                {plan.confirmLabel}
              </button>
            </div>
          </div>
        </div>
      ) : null}
    </div>
  );
}

/**
 * The extension's panel on Cove's Settings page.
 *
 * Renders no heading of its own: the host wraps a panel in a `SectionCard` titled with the manifest's
 * panel label, so a second title would appear twice.
 *
 * The naming style lives here rather than in the review dialog because changing it mid-review used to
 * destroy the review — the dialog re-fetched, and the callers implemented that by unmounting it, so
 * every tick the reviewer had made was lost. A setting chosen before a review starts cannot do
 * that to anyone.
 *
 * The cover-host list lives here because it is the only place it can be *managed*: the dialog's notice
 * appends the one host in front of the user, which left a list nobody could see, correct or shrink
 *. That notice stays — it is the shortcut for the moment a user discovers they need it, not the
 * management surface.
 *
 * Source folders live here because torrents already live in the operator's own folders and copying a
 * collection into ours is the wrong ask. They are read-only sources: the extension's own folder
 * is shown beside them but cannot be edited, because it is where uploads land and moving it is not the
 * operator's decision to make.
 *
 * A settings panel carries no permission in the host's manifest — unlike a page — so what gates this
 * is the tab it is contributed to, which the host shows only to a user who may write system settings,
 * while `/settings` stays behind `videos:scrape`. Those are two different permissions, and a load
 * failure is shown rather than swallowed for exactly that reason: without it, a user who may open the
 * tab but not call the endpoint gets an empty box with no explanation.
 */
export function TorrentMetadataSettings() {
  // The stylesheet is injected by whichever of our components mounts first, and on the Settings page
  // none of the others ever does — the panel is the only thing of ours on it. Without this the host
  // renders our markup with none of our classes defined.
  ensureStyles();

  const [style, setStyle] = useState<string | null>(null);
  const [hosts, setHosts] = useState<string[] | null>(null);
  const [folders, setFolders] = useState<string[] | null>(null);
  const [writeFolder, setWriteFolder] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const receive = (settings: Settings) => {
    setStyle(settings.tagNameStyle);
    setHosts(settings.coverHosts);
    setFolders(settings.sourceFolders);
    setWriteFolder(settings.writeFolder);
  };

  useEffect(() => {
    let cancelled = false;
    matchApi
      .getSettings()
      .then((settings) => !cancelled && receive(settings))
      .catch((failure: Error) => !cancelled && setError(failure.message));
    return () => {
      cancelled = true;
    };
  }, []);

  const change = async (next: string) => {
    // Optimistic, and restored on failure: leaving the select showing the old value while the request
    // is in flight reads as the click not having registered.
    const previous = style;
    setStyle(next);
    setBusy(true);
    setError(null);
    try {
      await matchApi.setTagNameStyle(next);
    } catch (failure) {
      setStyle(previous);
      setError((failure as Error).message);
    } finally {
      setBusy(false);
    }
  };

  // Never optimistic, unlike the style: the server rewrites what it is given — a scheme or a trailing
  // separator is cut, a duplicate collapses, an unusable entry is dropped — so showing the typed value
  // would show something that is not what was stored. The response is the list.
  const write = async (
    save: () => Promise<Settings>,
    describe: (saved: Settings) => string,
  ) => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const saved = await save();
      receive(saved);
      setNotice(describe(saved));
    } catch (failure) {
      // Nothing to roll back — the state still holds what the server last confirmed, which is exactly
      // what is still stored when a write fails.
      setError((failure as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="tm-panel">
      <label className="tm-panel-field">
        <span className="tm-field-label">New tags are named</span>
        <select
          className="tm-select"
          value={style ?? ""}
          disabled={busy || style === null}
          onChange={(event) => void change(event.target.value)}
        >
          {/* Held until the current value is known, so the select never shows a style that is not the
              one in effect. */}
          {style === null ? <option value="">Loading…</option> : null}
          {TAG_STYLES.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label} — {option.example}
            </option>
          ))}
        </select>
      </label>
      <p className="tm-hint">
        Applies to tags this extension <em>creates</em>. A tag that already exists keeps the library's
        own spelling, whichever style is chosen here.
      </p>

      <ListEditor
        label="Torrent folders"
        items={folders}
        placeholder="/srv/torrents"
        empty="None yet — only the extension's own folder below is read."
        removeTitle={(folder) => `Stop reading torrents from ${folder}`}
        busy={busy}
        onAdd={(value) =>
          void write(
            () => matchApi.setSourceFolders([...(folders ?? []), value]),
            (saved) => describeFolderAdd(folders ?? [], saved.sourceFolders),
          )
        }
        onRemove={(folder) =>
          void write(
            () => matchApi.setSourceFolders((folders ?? []).filter((existing) => existing !== folder)),
            () => `${folder} removed. Rescan to drop its torrents from the index.`,
          )
        }
      />
      <p className="tm-hint">
        Read only — nothing is ever written into them, and removing one leaves its files alone. Must be
        absolute paths on the machine running Cove. Changes take effect on the next{" "}
        <strong>Rescan folder</strong>, not immediately.
        {writeFolder ? (
          <>
            {" "}
            Torrents dropped on the extension's own pages are saved to <code className="tm-code">{writeFolder}</code>,
            which is always read and cannot be moved. They are kept until you remove them below —
            including ones you dropped on a video and then decided against, which are saved before the
            review opens. {/* The last clause rather than a fourth sentence: uninstalling is the one
            way to lose this screen while keeping the files, so it is the one time "kept until you
            remove them below" stops being the whole answer. The README carries the rest. */}
            Uninstalling the extension does not remove them, and this is the only screen that can.
          </>
        ) : null}
      </p>

      <FolderList
        folder={writeFolder}
        busy={busy}
        onBusy={setBusy}
        onNotice={(message) => {
          setError(null);
          setNotice(message);
        }}
        onError={(message) => {
          setNotice(null);
          setError(message);
        }}
      />

      <ListEditor
        label="Cover hosts"
        items={hosts}
        placeholder="images.example.com"
        // The shipped default. Worded as "not set up yet" rather than "none allowed", because an empty
        // list is why covers do not import on a fresh install and that has to be actionable.
        empty="None yet — covers are not fetched until a host is listed here."
        removeTitle={(host) => `Stop fetching covers from ${host}`}
        busy={busy}
        onAdd={(value) =>
          void write(
            () => matchApi.setCoverHosts([...(hosts ?? []), value]),
            (saved) => describeHostAdd(hosts ?? [], saved.coverHosts),
          )
        }
        onRemove={(host) =>
          void write(
            () => matchApi.setCoverHosts((hosts ?? []).filter((existing) => existing !== host)),
            () => `${host} removed. Covers already imported are kept.`,
          )
        }
      />
      <p className="tm-hint">
        Covers are only ever requested from these hosts, because the URL comes out of the torrent
        itself. An entry means that host alone — write <code>*.host.example</code> to include its
        subdomains. Removing one stops future fetches; covers already imported are kept.
      </p>

      {notice ? <div className="tm-notice">{notice}</div> : null}
      {error ? <div className="tm-notice is-error">{error}</div> : null}
    </div>
  );
}
