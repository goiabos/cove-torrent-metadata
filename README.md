# Torrent Metadata

A [Cove](https://github.com/yourcove/cove) extension that reads the metadata a Luminance-based tracker
embeds in its `.torrent` files and offers it as a reviewable suggestion for videos already in your
library.

Nothing is written without review. A torrent is treated as a *suggestion*, never an authority: fields
are filled only where empty unless you explicitly ask to overwrite, and tags and performers are only
ever added.

## What it reads

Luminance writes a tracker-injected `metadata` dictionary into every torrent it serves — `taglist`,
`title`, `cover url` and `description`. It is added server-side at upload, so all four keys are present
regardless of which torrent client made the file, which is what makes the tag list dependable.

Those four keys are core Luminance rather than any one site's patch, so any current Luminance
deployment should work. The tag *conventions* are another matter: the classifier and the performer
matcher were written against one tracker's corpus, and other sites are untested.

The `description` is freeform uploader-authored BBCode and is *not* parsed for structure.

## How matching works

Videos are identified by **exact file size**, which needs no fuzzy search. Size is *not* unique,
though, and the difference matters: measured across 3,218 torrents and 139,141 video files, **2.32%
of sizes are shared**, with 20 real collisions in a library of 875. Where two torrents describe one
size the lowest fan-out wins — a single-scene torrent's metadata is about that video, while a pack's
is the union across its whole release — and `TorrentEntryPreference` is the one comparer that answers
it everywhere, so the row a page shows and the entry an apply writes cannot disagree. See
`docs/DESIGN-DECISIONS.md`; the "unique in practice" this used to claim was an artefact of an early
54-torrent sample.

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
gets its own id, so Cove can take them back off again:

```bash
# what one run added, without changing anything
curl -X POST /api/ai-data/purge -d '{"sourceRunId":"<run>","kinds":["tagApplication"],"dryRun":true}'
```

Drop `dryRun` to apply it, or select on `"sourceKey":"torrent-metadata"` to undo everything this
extension has ever applied. Cove removes the tag from the video only where nothing else put it there
too, so a tag you had already applied by hand stays. Tags remain ordinary tags meanwhile — you can
delete a single wrong one from a video without any of this.

Two caveats. That endpoint lives under Cove's **AI data** screen and needs `aidata.clear`, which is a
different permission again. And it undoes tags only: covers and filled-in fields are the rows above.

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

## Building

Requires the .NET 10 SDK, Node 22+ for the frontend, and **a Cove checkout on disk**.

The checkout is not a convenience. This extension compiles against `Cove.Sdk` *and* `Cove.Data` —
it reads and writes host entities through `CoveContext`, plus `RelationNameResolver` — and
`Cove.Data` is published to no NuGet feed, so there is no package-only build to fall back on. That
is a property of Cove, not of this repo — it applies to anything compiling against `Cove.Data` — and
the design here takes it as given rather than working around it.

`Directory.Build.props` probes for the checkout — a sibling directory named `cove` first, then a
Cove checkout one level up with this repo inside it. With neither present the build stops and says
so rather than failing on a missing project reference.

```
cove/       <- github.com/yourcove/cove
cove-torrent-metadata/   <- this repo
```

```bash
cd src/Cove.TorrentMetadata/ui && npm install && npm run build   # bundles ui/src into dist-ui/main.js
dotnet build -c Release                 # runs the npm build too when node_modules exists

# or point at a checkout somewhere else (trailing slash required)
dotnet build -c Release -p:CoveSrc=/path/to/cove/src/
```

**Install from `artifacts/extension/`, not from `bin/Release/net10.0/`.** Run
`bash scripts/package.sh --verify`, then copy `artifacts/extension/` to
`<COVE_HOME>/extensions/io.github.goiabos.torrent-metadata/`.

The build output is not the same thing as the package, and copying it is how the worst failure in
this project's history reaches a running host: the manifest's `jsBundle` can name an asset the
directory does not hold, and the host resolves asset URLs from the code `Id`, so a 404 there fails
the frontend's whole reconcile pass and silently withdraws **every** extension's UI — not just this
one. `package.sh` is what checks the output actually holds what the manifest promises, alongside
refusing host-provided assemblies, debug symbols and anything identifying. `--verify` runs every
check and stops before writing the ZIP.

`scripts/package.sh` builds the release ZIP. It refuses to package unless the checkout's version is
exactly the `minCoveVersion` in `extension.json` — an artifact that declares a compatibility floor
should have been compiled against it — and `.github/actions/cove-checkout/action.yml` pins that
same revision by SHA rather than tracking `main`. Bump the two together;
`ALLOW_COVE_VERSION_DRIFT=1` downgrades the check to a warning for local experiments.

Torrents are read from `<COVE_HOME>/torrent-metadata/`.

## Documentation

| Doc | What's in it |
|---|---|
| [docs/DESIGN-DECISIONS.md](docs/DESIGN-DECISIONS.md) | Why the extension is shaped this way — read before changing behaviour |
| [docs/DEV-SETUP.md](docs/DEV-SETUP.md) | Reproducing the dev environment (WSL, Docker Postgres, restored library), and running the extension against it |
| [docs/TEST-COVERAGE.md](docs/TEST-COVERAGE.md) | Where the suite actually stands, measured rather than estimated |
| [docs/POSTGRES_LINUX_ISSUES.md](docs/POSTGRES_LINUX_ISSUES.md) | Three Linux/Docker PostgreSQL defects, with reproductions |

## Licence

**GNU AGPL v3** — see [LICENSE](LICENSE). Copyright the Torrent Metadata contributors.

The licence is not really a free choice here. This extension compiles against `Cove.Sdk` *and*
`Cove.Data`, and it runs inside the host's process and its `AssemblyLoadContext`, reading and writing
host entities through `CoveContext`. Cove is AGPL-3.0, so matching it is the answer that raises no
question — a permissive licence would still be combinable, but only after an argument nobody needs to
have about an extension that cannot run outside an AGPL host anyway.

There are no per-file licence headers. The notice below and `LICENSE` are what a reader actually
looks at.

> This program is free software: you can redistribute it and/or modify it under the terms of the GNU
> Affero General Public License as published by the Free Software Foundation, either version 3 of the
> License, or (at your option) any later version.
>
> This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
> even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
> Affero General Public License for more details.
>
> You should have received a copy of the GNU Affero General Public License along with this program.
> If not, see <https://www.gnu.org/licenses/>.

Contributions are accepted under the same terms — see [CONTRIBUTING.md](CONTRIBUTING.md).

## Status

**Early — working, not yet releasable.** Built and exercised end to end against a real 875-video
library, with a test suite in `tests/` that needs no torrent. Not yet packaged for the
extension registry.

Building needs a Cove checkout beside this one, and will for as long as `Cove.Data` ships on no
NuGet feed — see [Building](#building). That is decided rather than outstanding; the release
workflow clones Cove itself, at a pinned revision.

Run them with `dotnet test tests/Cove.TorrentMetadata.Tests.csproj`.

The frontend has its own suite — `cd src/Cove.TorrentMetadata/ui && npm test`, vitest over the
modules that hold every decision the browser makes: which fields are offered, what starts ticked,
exactly what an apply request contains, and how much a bulk apply would actually write.

CI runs both suites, the frontend typecheck and the packaging checks on every push and pull request,
and the release workflow runs them again before packaging — so a tag cannot publish over a red suite.

See the tracker for everything outstanding, including the pre-release checklist. The structural
blockers are cleared: how the repo consumes Cove, the tests being copies rather than a project, and
CI having no test gate.

**Not published anywhere yet.**
