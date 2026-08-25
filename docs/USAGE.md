# Using Torrent Metadata

Everything the extension does, in the detail the README deliberately skips. Read the
[README](../README.md) first — this assumes you know what the extension is for.

## Where it appears

After installing, the extension surfaces in three places:

- **The Torrent Matches page** — the batch view over every torrent the extension has indexed.
  Menu entries are opt-in in Cove, so the page ships toggled **off**: turn it on under
  **Settings → Interface → Navigation**, where you can also hide content types you don't use.
  The page is always reachable at `/torrent-metadata` regardless.
- **The video page** — **Operations → Match from torrent** on any video opens a drop zone, and a
  dropped `.torrent` opens the review dialog pinned to that exact file.
- **Settings → Extensions → Torrent Metadata** — torrent folders, cover hosts, and how new tags
  are named. The batch page's gear icon links straight to it, and the panel itself carries the
  menu-visibility pointer above.

## What it reads

Luminance writes a tracker-injected `metadata` dictionary into every torrent it serves — `taglist`,
`title`, `cover url` and `description`. It is added server-side at upload, so all four keys are present
regardless of which torrent client made the file, which is what makes the tag list dependable.

Those four keys are core Luminance rather than any one site's patch, so any current Luminance
deployment should work. The tag *conventions* are another matter: the classifier and the performer
matcher were written against one tracker's corpus, and other sites are untested.

The `description` is freeform uploader-authored BBCode and is *not* parsed for structure.

Torrents come from the folders you name in settings (read-only — nothing is ever written into
them), plus the extension's own folder under Cove's data directory
(`<COVE_HOME>/torrent-metadata/`), which is where anything dropped on the extension's pages is
saved.

## How matching works

Videos are identified by **exact file size**, which needs no fuzzy search. Size is *not* unique,
though, and the difference matters: measured across 3,218 torrents and 139,141 video files, **2.32%
of sizes are shared**, with 20 real collisions in a library of 875. Where two torrents describe one
size the lowest fan-out wins — a single-scene torrent's metadata is about that video, while a pack's
is the union across its whole release — and one comparer answers it everywhere, so the row a page
shows and the entry an apply writes cannot disagree. (`docs/DESIGN-DECISIONS.md` has the full
argument; the "unique in practice" an early draft claimed was an artefact of a 54-torrent sample.)

Every video file inside a torrent is indexed, so a pack matches each of its scenes independently.

- **Video page** → always opens a drop zone. Dropping a `.torrent` pins the proposal to that exact
  file, so a megapack sitting in the watched folder can never win over the scene torrent you chose.
- **Torrent Matches page** → lists every indexed torrent against the library, with bulk apply.
  Opening a row puts the review beside the list rather than over it, so you can walk the rows one at
  a time — *Review one by one* starts at the top — and see what is left while you do. Filter the list
  by video, torrent or file name; long tag lists get a filter of their own. Tick rows to apply only
  those — a ticked pack is applied because ticking it says so, where a bulk run holds packs back.

## Tag handling

Roughly a ninth of a typical tag list is not descriptive at all — resolution, codec, release date,
studio domain, performer age. Those are routed to typed fields or dropped rather than becoming tags.

Dots are usually word separators (`big.red.barn`), but not always: `h.265`, `lanternbay.com`,
`2018.03.20`, `sammy.j` and `2.man.crew` all break a blanket rewrite, so classification happens
before any normalisation.

Performer names are **not** detected by shape — `oil.slick` and `first.frost` look exactly like names.
They are found by matching against performers your library already knows, and accepted tags record
their original dotted spelling as a `TagAlias` so later torrents resolve exactly instead of guessing.

Tags the extension *creates* are stamped with a custom field (`torrent-metadata.source`), making them a filterable
set — the closest thing to an undo.

## Cover art is off until you say where from

A cover URL arrives inside the `.torrent`, and fetching it makes *your server* request a URL the
torrent chose. So covers are only ever fetched from hosts you have named, redirects included, and
the list ships **empty** — no cover imports until you add your tracker's image hosts.

The review dialog names the host it would need and offers to add it, so the usual path is one click
the first time. Nothing else in a proposal depends on it: a refused cover never costs you the tags
you just approved, and the reason is reported rather than swallowed.

Every cover request identifies itself as `TorrentMetadata/<version> (+repository URL)`, so an image host that wants to
rate-limit or block this extension can do so without blocking your whole Cove instance.

Each cover URL is downloaded **once** and remembered across restarts, so applying a pack's metadata
to all 200 of its scenes fetches one image, not 200 — and a cover that fails is not re-requested for
a while rather than once per scene.

What is left is paced: roughly one request a second per image host after a small burst, one at a
time, honouring `Retry-After`, backing off when a host is unhappy and stopping entirely after five
failures in a row. A large bulk import with covers therefore takes minutes rather than seconds, on
purpose.

## Undoing a bulk apply

The confirm dialog says how much a run would write: how many videos, how many tag links, how many of
those tags do not exist yet, how many covers would be replaced, and how many of the rows are packs —
whose tag list describes a whole release rather than one scene. The tag count is the one to read.
A few hundred videos is tens of thousands of tag links.

What can be taken back afterwards, in the order you would want it:

| What it wrote | Getting it back |
|---|---|
| Every tag it put on a video | Purge them by source — see below. Each one is stamped, so yours are untouched. |
| Tags it created | Filter on `torrent-metadata.source` and delete the set. |
| Title, date, studio, URL | Nothing to undo — a bulk apply only fills fields that were empty. |
| A replaced cover | A copy of the **generated** directory, taken beforehand. A database restore will not do it. |

Every tag this extension puts on a video is recorded as coming from `torrent-metadata`, and each run
gets its own id, so Cove can take them back off again — the **AI Data** screen under Settings →
Data Sources & Data shows what each run wrote, or from the command line:

```bash
# what one run added, without changing anything
curl -X POST /api/ai-data/purge -d '{"sourceRunId":"<run>","kinds":["tagApplication"],"dryRun":true}'
```

Drop `dryRun` to apply it, or select on `"sourceKey":"torrent-metadata"` to undo everything this
extension has ever applied. Cove removes the tag from the video only where nothing else put it there
too, so a tag you had already applied by hand stays. Tags remain ordinary tags meanwhile — you can
delete a single wrong one from a video without any of this.

Two caveats. That endpoint needs `aidata.clear`, which is a different permission again. And it
undoes tags only: covers and filled-in fields are the rows above.

That last row is worth knowing before ticking "Import covers". Cove's *Backups and upgrades* guide
covers the mechanics, but its database backup does not include the generated directory, which is
where the image files themselves live — so restoring the database restores a reference to a cover
file that is no longer on disk. The guide describes generated assets as rebuildable, which holds for
thumbnails and previews and does not hold for artwork that came from the internet.

One practical catch: a backup needs the `system.backup` permission and a restore needs
`system.restore`. This extension's pages need `videos.scrape`, which implies neither. If Settings →
Operations is not yours to open, ask whoever administers the instance *before* a large apply rather
than after.

## What uninstalling leaves behind

Removing the extension removes the extension. It does not remove your data, and two things in
particular outlive it:

| What survives | Where it is | Why |
|---|---|---|
| Every torrent you dropped on the extension's pages | A `torrent-metadata` folder under Cove's data directory | They are your files. Nothing here deletes a torrent you did not ask it to. |
| Your settings — cover hosts, source folders, tag naming style | Cove's `extension_data` table | So an upgrade or a reinstall keeps them. |

Both are deliberate, and the first is the one to plan for. A folder of a few thousand torrents is a
few hundred megabytes, and once the extension is gone, so is the settings screen that could list and
delete them — the files are then only reachable on the server itself. **If you want them gone, empty
the folder before you uninstall**, from Settings → Torrent Metadata, which has a filter and a bulk
remove.

Tags, performers, studios and covers already applied to your videos are untouched by an uninstall.
They are ordinary Cove data at that point, and *Undoing a bulk apply* above is how you take them back
— which needs the extension only for the provenance it already stamped, not for it to still be
installed.

Uninstall is not a reset, either: reinstalling picks the settings back up. There is no supported way
to clear them from the UI.
