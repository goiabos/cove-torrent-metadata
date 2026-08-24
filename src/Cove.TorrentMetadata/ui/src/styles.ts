/**
 * Styles for the extension's UI, injected once on first render.
 *
 * Everything is expressed through Cove's theme variables so the extension follows whatever theme the
 * user has active, including ones added after this was written. Hard-coded fallbacks exist only so the
 * dialog stays legible if a theme omits a variable.
 */

const STYLE_ELEMENT_ID = "torrent-metadata-styles";

const css = `
.tm-backdrop {
  position: fixed; inset: 0; z-index: 9999;
  background: var(--color-overlay, rgba(0,0,0,.6));
  display: flex; align-items: center; justify-content: center; padding: 24px;
}
.tm-modal {
  background: var(--color-card, #1e2028);
  color: var(--color-foreground, #e8eaf0);
  border: 1px solid var(--color-border, #2a2d38);
  border-radius: 10px;
  width: min(920px, 100%); max-height: 88vh;
  display: flex; flex-direction: column;
}
.tm-head {
  padding: 16px 20px; border-bottom: 1px solid var(--color-border, #2a2d38);
  display: flex; align-items: flex-start; gap: 16px;
}
.tm-head-main { min-width: 0; flex: 1; }
/* The exit, at the top of the frame. The footer carries the same request, and in the modal that is
   enough — but the pane is as tall as the viewport allows and stands taller than it before its sticky
   position engages, so its footer opens below the fold and a review is entered with no visible way
   out. Both buttons call requestClose, so neither is a second copy of the rule. */
.tm-close {
  flex: none; font: inherit; font-size: 1rem; line-height: 1; cursor: pointer;
  padding: 4px 10px; border-radius: 6px;
  background: none; color: var(--color-secondary, #9ea3b0);
  border: 1px solid var(--color-border, #2a2d38);
}
.tm-close:hover:not(:disabled) { color: var(--color-foreground, #e8eaf0); }
.tm-close:disabled { opacity: .5; cursor: default; }
/* The drop zone's only artwork, and the reason it survives: the review dialog used to carry one of
   these *as well as* the library cover in its comparison — the same image twice in one window. */
.tm-head-cover {
  width: 132px; aspect-ratio: 16 / 9; object-fit: cover; border-radius: 4px; flex: none;
  background: var(--color-surface, #1a1c23); border: 1px solid var(--color-border, #2a2d38);
}
.tm-foot {
  padding: 14px 20px; border-top: 1px solid var(--color-border, #2a2d38);
  display: flex; gap: 10px; align-items: center; justify-content: flex-end;
}
.tm-body { padding: 4px 20px 16px; overflow-y: auto; }
.tm-title { margin: 0 0 4px; font-size: 1.05rem; font-weight: 600; }
.tm-sub { margin: 0; font-size: .82rem; color: var(--color-secondary, #9ea3b0); }
/* The matched file, under the torrent name — the same place and the same clipping the list
   uses for it, so a review beside that list reads the same way. The full name is in the
   title attribute, because the part that identifies a scene is usually the end of it. */
.tm-sub-file {
  margin-top: .1rem; font-size: .74rem; color: var(--color-muted, #6b7085);
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.tm-warn {
  margin: 12px 0 0; padding: 10px 12px; border-radius: 6px; font-size: .82rem;
  background: var(--color-surface, #1a1c23); border: 1px solid var(--color-accent, #4f8ff7);
}
.tm-cover-warn { margin: 0 0 10px; }
.tm-code {
  padding: 1px 5px; border-radius: 4px; font-size: .95em;
  background: var(--color-card, #1e2028); border: 1px solid var(--color-border, #2a2d38);
}
.tm-allow {
  display: block; margin-top: 8px; padding: 5px 10px; border-radius: 6px; cursor: pointer;
  font: inherit; font-size: .82rem;
  background: var(--color-accent, #4f8ff7); color: var(--color-on-accent, #0b0d12); border: 0;
}
.tm-allow:disabled { opacity: .6; cursor: default; }
.tm-allow-error { margin-top: 6px; color: var(--color-danger, #e2586a); }
.tm-section { margin-top: 18px; }
/* The first section sits directly under the header's own border, so it needs less air above it than
   one separating two lists. */
.tm-section.is-first { margin-top: 10px; }
/* A section whose content is shorter than its own heading — performers average 1.9 — puts the two on
   one line instead of spending a heading plus its margins on two rows. */
.tm-section.is-tight { margin-top: 14px; display: flex; align-items: baseline; gap: 12px; }
.tm-section.is-tight .tm-section-head { margin-bottom: 0; flex: none; }
.tm-section.is-tight .tm-chips { flex: 1; min-width: 0; }
.tm-section-head {
  display: flex; align-items: baseline; gap: 10px; margin-bottom: 8px;
  font-size: .72rem; letter-spacing: .08em; text-transform: uppercase;
  color: var(--color-muted, #6b7085);
}
.tm-section-head .tm-hint { text-transform: none; letter-spacing: 0; font-size: .75rem; }
/* Pushed to the end of the header row, so the controls read as belonging to this section rather
   than trailing its label. */
.tm-section-actions { margin-left: auto; display: flex; gap: 6px; }
.tm-btn.is-small { padding: 3px 9px; font-size: .75rem; }
.tm-row { display: flex; align-items: center; gap: 8px; padding: 3px 0; font-size: .87rem; cursor: pointer; }
.tm-row.is-applied { opacity: .5; cursor: default; }
.tm-chips { display: flex; flex-wrap: wrap; gap: 4px 14px; }
.tm-chips .tm-row { width: calc(33.333% - 10px); min-width: 190px; }
/* Rows that should take their own width rather than a column of a grid — a short inline list. */
.tm-chips.is-auto .tm-row { width: auto; min-width: 0; }
/* The torrent's own spelling, shown only where normalising did something unexpected to it — for a
   tag about to be created, that string is what gets written and seeded as an alias. */
.tm-source {
  font-size: .7rem; font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  color: var(--color-muted, #6b7085); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.tm-badge {
  font-size: .66rem; padding: 1px 6px; border-radius: 999px; white-space: nowrap;
  border: 1px solid var(--color-border, #2a2d38); color: var(--color-secondary, #9ea3b0);
}
.tm-badge.is-new { border-color: var(--color-accent, #4f8ff7); color: var(--color-accent, #4f8ff7); }
.tm-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.tm-field { display: flex; align-items: flex-start; gap: 8px; padding: 5px 0; font-size: .87rem; cursor: pointer; }
.tm-field > input { margin-top: 3px; }
.tm-field-body { min-width: 0; flex: 1; }
.tm-field-label {
  font-size: .7rem; text-transform: uppercase; letter-spacing: .06em;
  color: var(--color-muted, #6b7085);
}
.tm-value { overflow-wrap: anywhere; }
.tm-value.is-current { color: var(--color-secondary, #9ea3b0); }
.tm-value.is-current::before { content: "current: "; color: var(--color-muted, #6b7085); }
.tm-value.is-proposed::before { content: "torrent: "; color: var(--color-muted, #6b7085); }
/* A field that only fills a gap has no second value to stack, so its label and value share a line.
   The "torrent: " prefix goes with them: it distinguished two values, and there is only one.
   A replacing field keeps the stacked form — the shape says the decision is heavier. */
.tm-field.is-fill { align-items: center; padding: 3px 0; }
.tm-field.is-fill .tm-field-body { display: flex; align-items: baseline; gap: 10px; }
.tm-field.is-fill .tm-field-label { flex: none; min-width: 46px; }
.tm-field.is-fill .tm-value.is-proposed::before { content: none; }
.tm-field.is-fill .tm-value { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
/* The two states where the studio is not a tick. Neither carries a checkbox, so neither is a
   label — is-choose asks which, is-inert only reports. Both keep the fill field's shared-line shape
   so the block reads as one list rather than three kinds of row. */
.tm-field.is-choose, .tm-field.is-inert { cursor: default; padding: 4px 0; }
.tm-field.is-inert .tm-field-body,
.tm-field.is-choose .tm-field-body { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.tm-field.is-choose .tm-field-label, .tm-field.is-inert .tm-field-label { flex: none; }
.tm-value.is-quiet { color: var(--color-muted, #6b7085); }
.tm-choice { display: flex; border: 1px solid var(--color-border, #2a2d38); border-radius: 6px; overflow: hidden; }
.tm-choice-opt {
  padding: 4px 10px; font: inherit; font-size: .8rem; cursor: pointer;
  background: var(--color-surface, #1a1c23); color: var(--color-secondary, #9ea3b0);
  border: 0; border-right: 1px solid var(--color-border, #2a2d38);
  display: flex; align-items: baseline; gap: 6px;
}
.tm-choice-opt:last-child { border-right: 0; }
.tm-choice-opt:disabled { cursor: default; opacity: .6; }
/* The two studios are drawn identically when neither is picked: the extension has no view on which is
   right, and styling one as the default would be the guess the studio rule removed, in CSS. */
.tm-choice-opt.is-on { background: var(--color-accent, #4f8ff7); color: #fff; }
.tm-choice-dom { font-size: .72rem; opacity: .72; }
.tm-choice-opt.is-on .tm-choice-dom { opacity: .8; }
.tm-btn {
  padding: 7px 14px; border-radius: 6px; cursor: pointer; font: inherit; font-size: .85rem;
  border: 1px solid var(--color-border, #2a2d38);
  background: var(--color-surface, #1a1c23); color: var(--color-foreground, #e8eaf0);
}
.tm-btn.is-primary {
  background: var(--color-accent, #4f8ff7); border-color: var(--color-accent, #4f8ff7); color: #fff;
}
.tm-btn:disabled { opacity: .5; cursor: default; }
.tm-select {
  font: inherit; font-size: .8rem; padding: 4px 8px; border-radius: 6px;
  background: var(--color-input, rgba(0,0,0,.25)); color: var(--color-foreground, #e8eaf0);
  border: 1px solid var(--color-border, #2a2d38);
}
.tm-toolbar {
  display: flex; align-items: center; gap: 8px; margin-top: 12px;
  font-size: .78rem; color: var(--color-secondary, #9ea3b0);
}
.tm-spinner {
  display: inline-block; width: 13px; height: 13px; vertical-align: -2px;
  border: 2px solid currentColor; border-right-color: transparent; border-radius: 50%;
  animation: tm-spin .7s linear infinite;
}
.tm-btn .tm-spinner { margin-right: 7px; }
@keyframes tm-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .tm-spinner { animation-duration: 2.4s; } }
.tm-link { color: var(--color-accent, #4f8ff7); text-decoration: none; white-space: nowrap; }
.tm-link:hover { text-decoration: underline; }
.tm-status { margin-right: auto; font-size: .82rem; color: var(--color-secondary, #9ea3b0); }
.tm-status.is-error { color: #f87171; }
/* Walking the matched rows without returning to the page between each one. It sits at the far
   left of the footer, opposite the actions: it moves between reviews, they act on this one. */
.tm-pager { display: flex; align-items: center; gap: 8px; margin-right: 14px; }
.tm-pager-count {
  font-size: .8rem; color: var(--color-secondary, #9ea3b0); font-variant-numeric: tabular-nums;
  display: inline-flex; align-items: center; gap: 6px; white-space: nowrap;
}
/* The frame is held while the next proposal is fetched, rather than blanked and rebuilt: the index
   has already moved, so dimming says "this is the row you are leaving". */
.tm-body.is-stepping { opacity: .45; pointer-events: none; }
.tm-hint { color: var(--color-muted, #6b7085); font-size: .78rem; }

/* Two groups in one strip: what the list shows, then what a bulk apply would do. */
.tm-controls-sep {
  width: 1px; align-self: stretch; margin: -2px 4px;
  background: var(--color-border, #2a2d38);
}
/* The pack being worked as a set. Its figures are the pack's, not the page's. */
.tm-focus {
  display: flex; align-items: center; gap: 12px; flex-wrap: wrap;
  margin: 10px 0; padding: 9px 12px; border-radius: 6px; font-size: .84rem;
  background: var(--color-surface, #1a1c23);
  border: 1px solid var(--color-accent, #4f8ff7);
}
.tm-focus-name {
  font-weight: 600; max-width: 46ch;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.tm-focus .tm-btn { margin-left: auto; }
/* Inside the pack warning, on its own line: the sentence is the reason to press it. */
.tm-warn-action { display: block; margin-top: 8px; }

/* The keys that do what the footer's arrows do. A hint, not a control — the control is the button it
   is describing, and this is only rendered where something has actually bound them. */
.tm-keys {
  padding: 6px 20px; border-top: 1px solid var(--color-border, #2a2d38);
  color: var(--color-muted, #6b7085); font-size: .72rem;
}

/* --- filters: over the tag list at 200 rows, over the row list whenever it is long --- */
.tm-filter-row { display: flex; align-items: center; gap: 10px; margin-bottom: 8px; }
.tm-filter {
  flex: 1; min-width: 0; padding: 5px 9px; border-radius: 6px; font: inherit; font-size: .82rem;
  background: var(--color-surface, #1a1c23); color: var(--color-foreground, #e8eaf0);
  border: 1px solid var(--color-border, #2a2d38);
}
.tm-filter::placeholder { color: var(--color-muted, #6b7085); }
.tm-filter:focus-visible { outline: 2px solid var(--color-accent, #4f8ff7); outline-offset: 1px; }
/* The ticks the filter is hiding. Its own line rather than a hint beside the count: it is the one
   thing on screen that an apply would act on and the list cannot show. */
.tm-filter-hidden {
  margin: 0 0 8px; padding: 6px 10px; border-radius: 6px; font-size: .78rem;
  color: var(--color-foreground, #e8eaf0);
  background: var(--color-surface, #1a1c23);
  border: 1px solid var(--color-accent, #4f8ff7);
}
.tm-controls .tm-filter { flex: none; width: 220px; }

/* --- the split: the list beside the review rather than the review over it --- */
.tm-split { display: flex; gap: 16px; align-items: flex-start; margin-top: 12px; }
.tm-list {
  width: 320px; flex: none; border-radius: 8px; overflow: hidden;
  border: 1px solid var(--color-border, #2a2d38);
}
/* Sticky rather than a fixed frame: the host's chrome height is not ours to know, so the list scrolls
   with the page and this stays put beside it, bounded by the viewport it can see. */
/* Offset by the host's own sticky navbar rather than by the viewport. top: 12px put our header —
   the review's title and the close button beside it — underneath Cove's navbar, which is
   sticky top-0 z-50 and 48px tall, so the one control that leaves the review was covered by the
   host's chrome the moment the page scrolled. 4rem/5rem is not a measurement of that chrome: it is
   the offset Cove's own settings sidebar uses for the same reason (lg:top-16,
   lg:max-h-[calc(100vh-5rem)]), so if the navbar ever changes height the host's sidebar and this
   are wrong together rather than this one alone. */
.tm-pane {
  flex: 1; min-width: 0;
  position: sticky; top: 4rem; max-height: calc(100vh - 5rem);
  display: flex; flex-direction: column;
  background: var(--color-card, #1e2028);
  border: 1px solid var(--color-border, #2a2d38);
  border-radius: 10px;
}
.tm-pane .tm-body { min-height: 0; }
/* One DOM, two presentations. Below this there is no room for both, so the same pane becomes a sheet
   over the page — a media query rather than a third component or a width measured in JavaScript. */
@media (max-width: 1100px) {
  .tm-split { display: block; }
  .tm-list { width: auto; }
  .tm-pane {
    position: fixed; inset: 8px; z-index: 9998; max-height: none;
    box-shadow: 0 18px 48px rgba(0, 0, 0, .55);
  }
}
.tm-lrow {
  display: flex; gap: 9px; align-items: center; width: 100%; text-align: left;
  padding: 7px 10px; font: inherit; cursor: pointer;
  color: var(--color-secondary, #9ea3b0);
  background: var(--color-surface, #1a1c23);
  border: 0; border-bottom: 1px solid var(--color-border, #2a2d38);
  border-left: 2px solid transparent;
}
.tm-lrow:last-child { border-bottom: 0; }
.tm-lrow:hover { background: var(--color-card-hover, #252830); }
.tm-lrow.is-current {
  background: var(--color-card, #1e2028);
  border-left-color: var(--color-accent, #4f8ff7);
  color: var(--color-foreground, #e8eaf0);
}
.tm-lrow-main { flex: 1; min-width: 0; }
/* The video first, because that is what the pane's own header is named after — a list beside a review
   should say what the review says. */
.tm-lrow-title { display: block; font-size: .84rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.tm-lrow-sub {
  display: block; font-size: .72rem; color: var(--color-muted, #6b7085);
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
/* The library's own artwork and never the torrent's: that one is a comparison, it happens in the pane
   at a size where it means something, and every one of them costs a paced request through the proxy
  . This one is local. */
.tm-lrow-thumb, .tm-lrow img {
  width: 40px; height: 23px; object-fit: cover; border-radius: 3px; flex: none;
  background: var(--color-surface, #1a1c23); border: 1px solid var(--color-border, #2a2d38);
}
/* What this walk applied, which is an act rather than a status — the row's own state does not move
   until the refresh on close asks the server. */
.tm-lmark { color: #2dd4bf; font-size: .8rem; flex: none; }
.tm-lnum { font-variant-numeric: tabular-nums; font-size: .76rem; flex: none; }
.tm-lnum .tm-new { margin-left: 3px; }

/* --- batch page --- */
.tm-page { padding: 20px 24px 48px; color: var(--color-foreground, #e8eaf0); }
.tm-page.is-dragging { outline: 2px dashed var(--color-accent, #4f8ff7); outline-offset: -12px; }
.tm-page-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 16px; }
.tm-page-title { margin: 0 0 4px; font-size: 1.35rem; font-weight: 600; }
.tm-page-actions { display: flex; gap: 8px; align-items: center; }
.tm-controls {
  display: flex; flex-wrap: wrap; gap: 8px 18px; align-items: center;
  margin: 16px 0; padding: 12px 14px; border-radius: 8px;
  background: var(--color-surface, #1a1c23); border: 1px solid var(--color-border, #2a2d38);
  font-size: .84rem;
}
.tm-check { display: flex; align-items: center; gap: 6px; cursor: pointer; }
.tm-notice {
  margin: 10px 0; padding: 9px 12px; border-radius: 6px; font-size: .84rem;
  background: var(--color-surface, #1a1c23); border: 1px solid var(--color-border, #2a2d38);
}
.tm-notice.is-error { border-color: #f87171; color: #f87171; }
/* The receipt for an apply that has happened, at the top of the review it changed. Its own colour
   rather than the accent, which throughout this UI means "work waiting" — this is the opposite. */
.tm-applied {
  margin: 10px 0 0; padding: 9px 12px; border-radius: 6px; font-size: .84rem;
  background: var(--color-surface, #1a1c23); border: 1px solid #2dd4bf; color: var(--color-foreground, #e8eaf0);
}
.tm-table { width: 100%; border-collapse: collapse; font-size: .86rem; }
.tm-table th {
  text-align: left; padding: 8px 10px; font-size: .7rem; letter-spacing: .07em; text-transform: uppercase;
  color: var(--color-muted, #6b7085); border-bottom: 1px solid var(--color-border, #2a2d38);
}
.tm-table td { padding: 8px 10px; border-bottom: 1px solid var(--color-border, #2a2d38); vertical-align: top; }
.tm-table td .tm-name { max-width: 34ch; }
.tm-table tr.is-clickable { cursor: pointer; }
.tm-table tr.is-clickable:hover td { background: var(--color-card-hover, #252830); }
.tm-num { text-align: right; white-space: nowrap; }
/* The selection column. Narrow, and its own click target rather than part of the row's. */
.tm-table th.tm-tick, .tm-table td.tm-tick { width: 1%; padding-right: 0; cursor: default; }
.tm-new { color: var(--color-accent, #4f8ff7); }
/* Secondary to the tag count it sits beside: a performer number is worth seeing, but tags are what
   the column is mostly about and what the reviewer scans for. */
.tm-sub { color: var(--color-muted, #6b7085); }
.tm-video { display: flex; align-items: center; gap: 8px; }
.tm-video img {
  width: 64px; height: 36px; object-fit: cover; border-radius: 3px; flex: none;
  background: var(--color-surface, #1a1c23);
}
.tm-pill-group { display: inline-flex; gap: 5px; }
.tm-pill {
  font-size: .68rem; padding: 2px 7px; border-radius: 999px; white-space: nowrap;
  border: 1px solid var(--color-border, #2a2d38); color: var(--color-secondary, #9ea3b0);
}
.tm-pill.is-matched { border-color: var(--color-accent, #4f8ff7); color: var(--color-accent, #4f8ff7); }
.tm-pill.is-applied { opacity: .65; }
/* Applied, but with tags still on offer. Neither the accent of work waiting nor the dimming of work
   finished — a row worth a second look rather than a second bulk run. Its own colour rather than the
   pack yellow beside it, or a pack with updates would wear the same pill twice. */
.tm-pill.is-updated { border-color: #2dd4bf; color: #2dd4bf; }
.tm-pill.is-pack { border-color: #eab308; color: #eab308; }
.tm-modal.is-compact { width: min(560px, 100%); }
.tm-drop {
  margin: 14px 0; padding: 34px 20px; border-radius: 8px; cursor: pointer;
  border: 2px dashed var(--color-border, #2a2d38); background: var(--color-surface, #1a1c23);
  display: flex; flex-direction: column; align-items: center; gap: 6px; text-align: center;
}
.tm-drop.is-dragging { border-color: var(--color-accent, #4f8ff7); }
.tm-alt {
  display: flex; align-items: center; justify-content: space-between; gap: 12px;
  padding: 10px 12px; border-radius: 6px; margin-top: 4px;
  background: var(--color-surface, #1a1c23); border: 1px solid var(--color-border, #2a2d38);
  font-size: .84rem;
}
.tm-alt .tm-name { max-width: 34ch; }
/* The cover decision, at the size of a decision: two thumbnails, the checkbox, and the way to the
   full comparison. The comparison itself was the first 380px of the body for one yes/no. */
.tm-cover-strip {
  display: flex; align-items: center; gap: 10px; flex-wrap: wrap;
  padding: 8px 10px; border-radius: 6px; font-size: .84rem;
  background: var(--color-surface, #1a1c23); border: 1px solid var(--color-border, #2a2d38);
}
.tm-strip-thumb {
  width: 64px; height: 36px; object-fit: cover; border-radius: 3px; flex: none;
  background: var(--color-card, #1e2028); border: 1px solid var(--color-border, #2a2d38);
}
.tm-strip-thumb.is-blank { border-style: dashed; }
.tm-strip-action { margin-left: auto; }
/* Inert content, counted and available rather than listed. Never anything that can be applied. */
.tm-disclose {
  display: flex; align-items: center; gap: 8px; width: 100%; margin-top: 10px;
  padding: 6px 10px; border-radius: 6px; cursor: pointer; text-align: left;
  font: inherit; font-size: .8rem;
  background: var(--color-surface, #1a1c23); border: 1px solid var(--color-border, #2a2d38);
  color: var(--color-secondary, #9ea3b0);
}
.tm-disclose .tm-caret { color: var(--color-muted, #6b7085); }
.tm-disclose .tm-hint { margin-left: auto; }
/* Two equal columns of the full body width: the whole point is judging the artwork before taking it,
   so vertical space is worth spending. Falls back to one column when the modal is narrow. */
.tm-cover-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
@media (max-width: 640px) { .tm-cover-grid { grid-template-columns: 1fr; } }
.tm-cover { margin: 0; display: flex; flex-direction: column; gap: 4px; min-width: 0; }
.tm-cover img {
  width: 100%; max-height: 340px; object-fit: contain; border-radius: 4px;
  background: var(--color-surface, #1a1c23); border: 1px solid var(--color-border, #2a2d38);
}
.tm-cover figcaption { font-size: .72rem; color: var(--color-muted, #6b7085); }
/* Stands in for the cover until the host is allowed. A blank frame rather than nothing, so the two
   columns stay side by side and the notice above reads as "not yet" instead of "missing". */
.tm-cover-blank {
  width: 100%; min-height: 120px; border-radius: 4px; display: flex;
  align-items: center; justify-content: center; font-size: .75rem;
  color: var(--color-muted, #6b7085);
  background: var(--color-surface, #1a1c23); border: 1px dashed var(--color-border, #2a2d38);
}
.tm-video-covers { display: flex; gap: 4px; }
/* One slot, whatever is in it — the library's own image, or a torrent cover that has landed, is
   still queued, or will not come. The row's height must not depend on which. */
.tm-video-covers img, .tm-video-covers .tm-thumb {
  width: 56px; aspect-ratio: 16 / 9; object-fit: cover; border-radius: 3px; flex: none;
  background: var(--color-surface, #1a1c23);
}
.tm-empty { margin-top: 24px; color: var(--color-muted, #6b7085); font-size: .88rem; }
/* The settings panel. Deliberately plain: the host already wraps it in a titled SectionCard, so this
   supplies the controls and nothing around them. */
.tm-panel { display: flex; flex-direction: column; gap: 8px; }
.tm-panel-field { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
/* The cover-host list stacks instead, so a long host does not push its Remove button off the row. */
.tm-panel-field-block { display: flex; flex-direction: column; align-items: stretch; gap: 6px; margin-top: 14px; }
/* A *column* flex container stretches its items across the line, but an item still refuses to go
   below its own min-content width — and a torrent filename cannot wrap, so the folder listing grew to
   the longest name in it, pushed past the settings card and scrolled the whole page sideways. Capping
   the item is what actually holds it; min-width: 0 does not, because the item is being expanded
   rather than failing to shrink. Both lists in this panel are uls, and both carry unbreakable
   strings — filenames here, paths in the two ListEditors. */
.tm-panel-field-block > ul { max-width: 100%; }
.tm-hosts { list-style: none; margin: 0; padding: 0; display: flex; flex-wrap: wrap; gap: 6px; }
.tm-host {
  display: flex; align-items: center; gap: 6px; padding: 3px 4px 3px 6px; border-radius: 6px;
  background: var(--color-surface, #1a1c23); border: 1px solid var(--color-border, #2a2d38);
}
/* A source folder is an absolute path with no break opportunity in it, so the pill is trimmed rather
   than allowed to set the list's width. The full value stays in the DOM for copying and in the
   remove button's title. */
.tm-host { min-width: 0; max-width: 100%; }
.tm-host .tm-code {
  background: none; border: 0; padding: 0; font-size: .82rem;
  min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.tm-host-remove {
  font: inherit; line-height: 1; padding: 2px 6px; border-radius: 4px; cursor: pointer;
  background: none; border: 0; color: var(--color-muted, #6b7085);
}
.tm-host-remove:hover:not(:disabled) { color: var(--color-danger, #e2586a); }
.tm-host-remove:disabled { opacity: .5; cursor: default; }
.tm-host-add { display: flex; gap: 8px; align-items: center; }
.tm-input {
  flex: 1 1 220px; min-width: 0; max-width: 320px; padding: 5px 8px; border-radius: 6px;
  font: inherit; font-size: .82rem;
  background: var(--color-surface, #1a1c23); color: inherit;
  border: 1px solid var(--color-border, #2a2d38);
}
.tm-input:disabled { opacity: .6; }
/* The one .tm-input that is not inside a row. .tm-panel-field-block lays its children out in a
   *column*, so .tm-input's flex: 1 1 220px sets a 220px **height** — the folder filter rendered
   as a text box seven lines tall and 320px wide. This puts the size back on the axis it was written
   for; the basis belongs to .tm-host-add, which is a row. */
.tm-list-filter { flex: none; width: 100%; max-width: 320px; }

/* --- the extension's own folder, listed so anything in it can be removed --- */
.tm-torrents { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; }
.tm-torrent { display: flex; align-items: center; gap: 10px; padding: 7px 8px; border-radius: 6px; }
.tm-torrent + .tm-torrent { border-top: 1px solid var(--color-border, #2a2d38); border-radius: 0; }
.tm-torrent:hover { background: var(--color-card-hover, #252830); }
.tm-torrent-main { min-width: 0; flex: 1; display: flex; flex-direction: column; }
/* The name on disk, in the face a filename belongs in: it is what the user dragged in, and the
   identity a remove request names. */
.tm-torrent-file {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: .82rem;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
/* The release name and the file count. A different string from the one above, and the one the batch
   page keys its rows on, so both are shown rather than either standing in for the other. */
.tm-torrent-meta {
  font-size: .72rem; color: var(--color-muted, #6b7085);
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.tm-torrent .tm-pill { flex: none; font-variant-numeric: tabular-nums; }
/* A file that will not parse: the only entry here that can never do anything useful. */
.tm-pill.is-bad { border-color: var(--color-danger, #e2586a); color: var(--color-danger, #e2586a); }
.tm-scroll { max-height: 232px; overflow-y: auto; margin: 0 -4px; padding: 0 4px; }
.tm-scroll.is-open { max-height: 420px; }
.tm-foot-line {
  display: flex; align-items: center; gap: 10px; flex-wrap: wrap;
  margin-top: 9px; padding-top: 9px; border-top: 1px solid var(--color-border, #2a2d38);
  font-size: .76rem; color: var(--color-muted, #6b7085); font-variant-numeric: tabular-nums;
}
.tm-linkbtn {
  font: inherit; font-size: .76rem; padding: 0; cursor: pointer;
  background: none; border: 0; color: var(--color-accent, #4f8ff7); text-decoration: underline;
}
.tm-linkbtn:disabled { opacity: .5; cursor: default; }
/* Pushed to the end of the footer: the count states what the button will take, and the button is the
   thing that takes it. */
.tm-spacer { margin-left: auto; }
.tm-btn.is-danger { border-color: var(--color-danger, #e2586a); color: var(--color-danger, #e2586a); }
.tm-confirm-lines { display: flex; flex-direction: column; gap: 9px; font-size: .85rem; }
.tm-confirm-lines p { margin: 0; }

/* --- cover frames ---
   Last in the sheet on purpose. Each of the three places a torrent cover appears sizes its own slot,
   and these say what the slot looks like while the image is not in it — so at equal specificity the
   state wins over the slot's resting border, rather than the other way round. */

/* A cover that is coming: the frame it will occupy, with a soft sheen falling down it. A spinner
   would say "working"; this says "an image belongs here", which is the truer statement — and it holds
   the row's height, so nothing jumps when the cover lands. The sheen is the theme's own hover
   colour rather than a white wash, so it reads on a light theme as well as a dark one. */
.tm-skeleton { position: relative; overflow: hidden; background: var(--color-surface, #1a1c23); }
.tm-skeleton::after {
  content: ""; position: absolute; inset: 0;
  background: linear-gradient(
    180deg,
    transparent 0%,
    var(--color-card-hover, #252830) 42%,
    var(--color-card-hover, #252830) 58%,
    transparent 100%);
  transform: translateY(-100%);
  animation: tm-sheen 1.5s ease-in-out infinite;
}
@keyframes tm-sheen { to { transform: translateY(100%); } }
/* Reduced motion still gets a distinct "coming" frame — a static wash rather than a held shimmer,
   because a sheen frozen mid-sweep reads as a rendering fault. */
@media (prefers-reduced-motion: reduce) {
  .tm-skeleton::after { animation: none; transform: none; opacity: .4; }
}

/* A cover that is *not* coming. Dashed rather than shimmering, so the two states are told apart at a
   glance instead of by waiting to see whether anything happens. */
.tm-cover-failed {
  display: flex; align-items: center; justify-content: center;
  font-size: .75rem; line-height: 1.3; text-align: center; color: var(--color-muted, #6b7085);
  background: var(--color-surface, #1a1c23);
  border: 1px dashed var(--color-border, #2a2d38);
}

/* The comparison's frame, which unlike the two thumbnails has no fixed height of its own: an image at
   its natural ratio, or a box tall enough to be read as a frame rather than a rule. */
.tm-cover .tm-cover-shot { width: 100%; border-radius: 4px; }
.tm-cover div.tm-cover-shot { min-height: 120px; }
`;

export function ensureStyles(): void {
  if (typeof document === "undefined" || document.getElementById(STYLE_ELEMENT_ID)) return;
  const style = document.createElement("style");
  style.id = STYLE_ELEMENT_ID;
  style.textContent = css;
  document.head.append(style);
}
