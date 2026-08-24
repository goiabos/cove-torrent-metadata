/**
 * Extension bundle entry point.
 *
 * Cove loads this as an ESM module and reads its default export: `components` are React components a
 * page or override can render, `actionHandlers` are functions entity actions invoke. React and the
 * authenticated API client arrive through the host's import map, so this bundle never carries its own
 * copy — see build.mjs.
 */

import React from "@cove/runtime/react";
import { createRoot, type Root } from "@cove/runtime/react-dom-client";
import { type TorrentMatchProposal } from "./api";
import { MatchDialog } from "./MatchDialog";
import { TorrentMetadataSettings } from "./SettingsPanel";
import { TorrentBatchPage } from "./TorrentBatchPage";
import { TorrentDropZone } from "./TorrentDropZone";
import { firstEntityId, type ActionPayload } from "./payload";
import { ensureStyles } from "./styles";

/**
 * Mounts a React tree into a detached container so a dialog can be opened from an imperative action
 * handler, which has no host component to render into.
 */
function mountOverlay(render: (unmount: () => void) => React.ReactElement): void {
  ensureStyles();
  const container = document.createElement("div");
  document.body.append(container);

  let root: Root | null = createRoot(container);
  const unmount = () => {
    // Deferred so unmounting from inside an event handler cannot tear the tree down mid-render.
    const current = root;
    root = null;
    queueMicrotask(() => {
      current?.unmount();
      container.remove();
    });
  };

  root.render(render(unmount));
}

export async function openTorrentMatchDialog(_action: unknown, payload: ActionPayload): Promise<void> {
  const videoId = firstEntityId(payload);
  if (videoId === null) {
    window.alert("This action needs a video.");
    return;
  }

  await openForVideo(videoId);
}

/**
 * Opens the review dialog for a video, or the drop zone when nothing describes it yet.
 *
 * Split out and given a plain `number` so the null check above narrows for good: a hoisted function
 * declaration would not keep the narrowing, and threading the id explicitly is clearer than asserting.
 */
async function openForVideo(videoId: number): Promise<void> {
  // Whether anything was written.
  //
  // Cove is never told the video changed. An extension has no way to say so: the host's query client
  // is module-local to its own entry point and the extension runtime exports only `extensionFetch`,
  // and this tree is a detached root with no provider above it. The host's own invalidation is behind
  // the same early return as the success toast, which a dialog-opening action must suppress, so it
  // never runs for us at all — and it would not help if it did, since the handler resolves the moment
  // the drop zone is on screen, long before any of this. So the page behind the dialog is stale, and
  // a reload is the only cure we have: an extension cannot tell the host that data changed.
  //
  // It happens on the way out rather than on a timer. The timer version destroyed the page 800 ms
  // after the apply, which is why the summary was never readable and why a dialog that cleared its
  // own busy state would only have opened a window to click Apply into a page about to vanish.
  let applied = false;

  const closeWith = (unmount: () => void) => () => {
    unmount();
    if (applied) window.location.reload();
  };

  // Mounted once and never remounted. The dialog used to ask to be re-fetched when the naming style
  // changed, and this implemented that by unmounting and reopening — which threw away the reviewer's
  // selection every time. The style is a setting now and nothing else in the dialog needs the server
  // to recompute a proposal, so the reopen path is gone with it.
  const openDialog = (current: TorrentMatchProposal) => {
    mountOverlay((unmount) => (
      <MatchDialog
        proposal={current}
        onClose={closeWith(unmount)}
        onApplied={() => { applied = true; }}
      />
    ));
  };

  // Always the drop zone first. Auto-matching would let a megapack in the watched folder win over the
  // scene-specific torrent the user went and found, which is the case this dialog exists to serve.
  mountOverlay((unmount) => (
    <TorrentDropZone
      videoId={videoId}
      onClose={unmount}
      onProposal={(proposal) => {
        unmount();
        openDialog(proposal);
      }}
    />
  ));
}

export default {
  components: { TorrentBatchPage, TorrentMetadataSettings },
  actionHandlers: { openTorrentMatchDialog },
};
