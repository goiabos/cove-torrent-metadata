import React from "@cove/runtime/react";
import { batchApi, matchApi, type TorrentMatchProposal } from "./api";
import { createIngestGuard } from "./ingestGuard";
import { isTorrentFile, NOT_A_TORRENT } from "./upload";

const { useCallback, useEffect, useRef, useState } = React;

interface TorrentDropZoneProps {
  videoId: number;
  onClose: () => void;
  /** Hands back a proposal once one has been produced, so the caller can open the review dialog. */
  onProposal: (proposal: TorrentMatchProposal) => void;
}

/**
 * The entry point for attaching a torrent to a single video.
 *
 * This is shown *always*, never only when nothing matches. Auto-matching from the watched folder is
 * wrong here: a video pulled out of a megapack will match that pack by file size, so silently using it
 * would hand the user the pack's union metadata precisely when they went and found the individual
 * scene's torrent. The dropped file wins; anything already indexed is offered as a clearly-labelled
 * second choice.
 */
export function TorrentDropZone({ videoId, onClose, onProposal }: TorrentDropZoneProps) {
  const [dragging, setDragging] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null);
  const [indexed, setIndexed] = useState<TorrentMatchProposal | null>(null);
  const fileInput = useRef<HTMLInputElement | null>(null);
  // A ref, not `busy` state: `ingest` is memoized on `[onProposal, videoId]` and would otherwise read
  // a stale `busy` from whichever render it was last recreated on. See `ingestGuard.ts` for why a
  // second drop must be refused rather than queued.
  const ingestGuard = useRef(createIngestGuard()).current;

  // Look up what the folder would have matched — shown as an alternative, never applied automatically.
  useEffect(() => {
    let cancelled = false;
    matchApi
      .match(videoId)
      .then((proposal) => !cancelled && setIndexed(proposal))
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [videoId]);

  const ingest = useCallback(
    async (files: FileList | File[]) => {
      // Refuses a drop that lands while one is still in flight, rather than queueing it: see
      // `ingestGuard.ts` for why. Checked before anything else, so a second drop while busy is
      // ignored whether or not it would itself have been a `.torrent` file.
      if (!ingestGuard.start()) return;

      const torrents = [...files].filter(isTorrentFile);
      if (torrents.length === 0) {
        setMessage({ text: NOT_A_TORRENT, error: true });
        ingestGuard.finish();
        return;
      }

      setBusy(true);
      setMessage({ text: "Reading…", error: false });
      try {
        const result = await batchApi.upload(torrents);
        if (result.saved === 0 || result.added.length === 0) {
          setMessage({ text: result.rejected.join("; ") || "Nothing could be read.", error: true });
          setBusy(false);
          ingestGuard.finish();
          return;
        }

        // Pin the proposal to the torrent that was just dropped, so the lookup cannot search by size and
        // land on a pack that also claims this video — the exact thing this dialog exists to avoid.
        //
        // The torrent, and not a file inside it: `added` lists one entry per video file, so `added[0]` is
        // a pack's first-listed scene rather than anything to do with this video, and naming it handed the
        // reviewer that scene's file under the pack's union metadata. The server knows this video's
        // byte count and we do not, so let it choose.
        //
        // Several torrents at once is still a guess. Prefer the least shared one, the tiebreak the
        // automatic lookup already uses: a single-scene torrent describes this video, a pack describes a
        // release this video is somewhere inside.
        const chosen = result.added.reduce((best, entry) => (entry.fanOut < best.fanOut ? entry : best));
        const proposal = await matchApi.match(videoId, chosen.torrentName);
        onProposal(proposal);
        // No `ingestGuard.finish()` here: `onProposal` unmounts this component (`main.tsx`), so there
        // is nothing left on screen for the guard to protect. Same shape as the `busy` comment on
        // `useIndexed` below — this branch stays open only on the ones that keep the dialog up.
      } catch (error) {
        setMessage({ text: (error as Error).message, error: true });
        setBusy(false);
        ingestGuard.finish();
      }
    },
    [ingestGuard, onProposal, videoId],
  );

  const useIndexed = () => {
    if (!indexed) return;
    // Goes through the same guard as `ingest`: without it, a drop landing in the gap between this
    // click and the re-render that disables the button below would still reach `onProposal` and stack
    // a second dialog, the same family of bug as a second drop landing mid-read.
    if (!ingestGuard.start()) return;
    // Nothing clears `busy` again: `onProposal` unmounts this tree, so a write after it lands on a
    // component that is gone. `ingest` above is the same shape — it clears `busy` only on the
    // branches that stay on screen, and falls off the end of the successful one.
    setBusy(true);
    onProposal(indexed);
  };

  return (
    <div className="tm-backdrop" onClick={(event) => event.target === event.currentTarget && onClose()}>
      <div className="tm-modal is-compact" role="dialog" aria-modal="true">
        <div className="tm-head">
          <div className="tm-head-main">
            <h3 className="tm-title">Match from torrent</h3>
            {/* States the ordering, because the ordering is the surprise. `ingest` uploads before it
                fetches a proposal, so the file is on disk and indexed before this dialog shows a single
                tag — and closing without applying leaves it there, matching this video from then on.
                Said here rather than only in Settings: nobody reads Settings before dragging a file
               . Removing one is the folder list on that page. */}
            <p className="tm-sub">
              Drop the .torrent for this exact release. It is saved to the extension's torrent folder
              before this review opens, and stays there whether or not you apply anything.
            </p>
          </div>
          <img
            className="tm-head-cover"
            src={`/api/videos/${videoId}/image?max=240`}
            alt=""
            loading="lazy"
            // Collapsed rather than hidden: this is a fixed 132px box beside the title, and a video
            // with no artwork should give that width back to the text rather than hold an empty slot.
            onError={(event) => { (event.currentTarget as HTMLImageElement).style.display = "none"; }}
          />
        </div>

        <div className="tm-body">
          <div
            className={`tm-drop${dragging ? " is-dragging" : ""}`}
            onDragOver={(event) => {
              event.preventDefault();
              setDragging(true);
            }}
            onDragLeave={() => setDragging(false)}
            onDrop={(event) => {
              event.preventDefault();
              setDragging(false);
              void ingest(event.dataTransfer.files);
            }}
            onClick={() => fileInput.current?.click()}
          >
            <strong>Drop a .torrent here</strong>
            <span className="tm-hint">or click to choose a file</span>
            <input
              ref={fileInput}
              type="file"
              accept=".torrent"
              multiple={false}
              style={{ display: "none" }}
              onChange={(event) => {
                if (event.target.files) void ingest(event.target.files);
                event.target.value = "";
              }}
            />
          </div>

          {indexed ? (
            <div className="tm-alt">
              <div>
                <div className="tm-field-label">Already in your torrent folder</div>
                <div className="tm-name" title={indexed.torrentName}>{indexed.torrentName}</div>
                {indexed.fanOut > 1 ? (
                  <div className="tm-hint">
                    Shared across {indexed.fanOut} videos — its tags describe the whole set, so a
                    scene-specific torrent is usually the better source.
                  </div>
                ) : null}
              </div>
              <button type="button" className="tm-btn" disabled={busy} onClick={useIndexed}>
                Use this instead
              </button>
            </div>
          ) : null}

          {message ? (
            <div className={`tm-notice${message.error ? " is-error" : ""}`}>{message.text}</div>
          ) : null}
        </div>

        <div className="tm-foot">
          <button type="button" className="tm-btn" onClick={onClose} disabled={busy}>Cancel</button>
        </div>
      </div>
    </div>
  );
}
