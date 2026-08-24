# Contributing

Thanks for looking. This is a [Cove](https://github.com/yourcove/cove) extension that reads the
metadata block Luminance-based trackers inject into their `.torrent` files and offers it as a
*reviewable suggestion* for videos already in the library.

Two things are worth knowing before you write any code, because both cost people an afternoon:
**you cannot build without a Cove checkout beside this repo**, and **`npm run build` does not
typecheck**. Both are covered below.

Beyond that, most of what this document contains is not process. It is the set of decisions that a
reasonable-looking simplification will undo *without a single test going red*. They are written down
because the code cannot state them itself.

---

## You need a Cove checkout next to this repo

Not a convenience — a hard requirement, and the most likely first issue anyone files.

The extension compiles against `Cove.Sdk` **and** `Cove.Data`: it reads and writes host entities
through `CoveContext` and uses `RelationNameResolver`. `Cove.Data` is published to no NuGet feed, so
there is no package-only build to fall back on. That is a property of Cove, not of this repo.

`Directory.Build.props` probes for the checkout — a sibling directory named `cove` first, then a Cove
checkout one level up with this repo inside it. With neither present the build stops and says so,
rather than failing on a missing project reference.

```
cove/                    <- github.com/yourcove/cove
cove-torrent-metadata/   <- this repo
```

You also need the **.NET 10 SDK** and **Node 22+**.

```bash
cd src/Cove.TorrentMetadata/ui && npm install && npm run build   # bundles ui/src into dist-ui/main.js
dotnet build -c Release                 # runs the npm build too when node_modules exists

# or point at a checkout somewhere else (trailing slash required)
dotnet build -c Release -p:CoveSrc=/path/to/cove/src/
```

`Directory.Build.props` and `Directory.Build.targets` also exist to stop MSBuild walking further up
the filesystem. Neither imports a parent, and deleting either hands the build to whatever happens to
sit above the checkout.

## The five gates

Run all five before you call a change done. CI runs them too, so a miss is a red PR rather than a
surprise later.

```bash
dotnet build -c Release
dotnet test tests/Cove.TorrentMetadata.Tests.csproj
cd src/Cove.TorrentMetadata/ui && npm run typecheck   # REQUIRED after any frontend change — see below
cd src/Cove.TorrentMetadata/ui && npm test            # the frontend's own suite, and not the same gate
bash scripts/package.sh --verify
```

`scripts/package.sh` builds the publish output and checks it: no host-provided assembly may ship, no
debug symbols, nothing identifying in the output. Each of those checks exists because it was wrong
once. `--verify` stops before writing the ZIP.

**`npm test` is a separate gate from `npm run typecheck`, not a thorough version of it.** The type
gate sees shapes; the suite sees decisions. Every non-presentational rule the browser applies lives
in a plain module under `src/Cove.TorrentMetadata/ui/src/` — what a proposal offers, how the review
queue steps, what a cover request may do, what the rescan line claims — and those modules import
neither React nor `@cove/runtime/*` precisely so they can be tested without a DOM. A change that
alters what an apply writes to someone's library can typecheck perfectly.

### CI compiles with `-warnaserror` and your local build does not

This is the one divergence between the two, and it is deliberate rather than an oversight: a C#
warning is a **build failure** on CI and a note on your machine, so a change that builds clean here
can turn the Tests step red.

The gate lives in `Directory.Build.props` as `TreatWarningsAsErrors`, scoped to this repository's own
projects. It cannot be a plain `-warnaserror` on the CI command line, because that applies to every
project MSBuild builds — including Cove's own, which is a `ProjectReference` into a checkout since
`Cove.Data` is published to no NuGet feed. Cove does not compile warning-free, so the unscoped form
failed CI on somebody else's warning for 29 commits while the suite beside it was green.

`WarningsNotAsErrors` exempts `NU1901;NU1902;NU1903;NU1904`. Those are NuGet's audit warnings, which
come from the advisory database rather than from this tree — without the exemption CI turns red on a
day nobody committed, for a package nobody here chose. `ci.yml` carries the reasoning at the step.

### `npm run build` does not typecheck

This is the single most contributor-hostile trap in the repo and it is invisible from the outside.

`src/Cove.TorrentMetadata/ui/build.mjs` is **esbuild alone**. esbuild strips types without checking them, so a frontend that
contradicts its own contract with the backend builds perfectly clean and ships. `npm run typecheck`
(`tsc --noEmit`) is the only type gate, and it is wired into neither `dotnet build` nor
`scripts/package.sh`. Run it by hand after touching anything under `src/Cove.TorrentMetadata/ui/`.

The failure it catches has a distinctive shape, because the two sides are hand-typed against each
other: `src/Cove.TorrentMetadata/ui/src/api.ts` declares interfaces matching C# property names, and nothing enforces the
agreement. Rename a property on one side and the other does not 404 — unmapped `/api/*` paths return
the host's SPA `index.html` with HTTP 200, so the symptom is
`Unexpected token '<', "<!DOCTYPE "... is not valid JSON`. `EndpointContractTests` pins the response
shapes as exact property-name sets for that reason.

The endpoint base in `src/Cove.TorrentMetadata/ui/src/api.ts` must likewise match the endpoint constants in
`TorrentMetadataExtension.cs`.

## No `.torrent` is ever committed, and no test may depend on one

Not one, not stripped down, not renamed, not "just for a test".

Torrents are the tracker's data. The tag lists, titles and filenames are the most identifying
artefact in this project, and git history cannot be un-published once it exists. Stripping a torrent
down to "file size plus tags" is not a middle ground — it keeps the identifying half, discards the
generic half, and invents a format nothing else parses.

This is enforced rather than trusted: `.gitignore` blocks `*.torrent`, and CI fails the build if
`git ls-files -- '*.torrent'` ever returns anything, because `git add -f` walks straight through a
`.gitignore`. If you did not know the rule, you meet it as a red build with no explanation — hence
this section.

**So no test may be pinned to a specific torrent, or to a corpus existing at all.** A fresh clone
with no data of any kind runs the whole suite green. Build fixtures in code instead:

- `TorrentRelease` is directly constructible — `Name` is the only required property, everything else
  has a default. Most tests need nothing more.
- `UploadEndpointTests` has a small bencode writer (`SingleFileTorrent`, `MultiFileTorrent`) for the
  cases that genuinely need real bytes over a real HTTP pipeline.

Invent the data rather than transcribing a real tag list. A fixture where every tag drives one
assertion reads better than forty realistic ones anyway.

This is not theoretical: a match-service test was once pinned to one file on the author's disk, and
**twelve of its fifteen tests silently returned** on any other machine — reporting green while doing
nothing at all.

A small number of tests are excluded from the default run because they are empirical claims about
what a real tracker emits rather than regression cover. `tests/README.md` explains what they are,
why they cannot be built from invented input, and how to point them at data you supply yourself.

## The decisions a refactor can silently undo

Every one of these survives a green test suite if you reverse it. `docs/DESIGN-DECISIONS.md` has the
full arguments; this is the short list of what not to "simplify".

**Classification happens before normalisation.** Dots in a tag are usually word separators
(`big.red.barn`) but not always: `h.265`, `some.studio.com`, `2018.03.20`, `sammy.j` and
`2.man.crew` each break a blanket dot-to-space rewrite. Protected shapes are matched first and
only the remainder is normalised. Collapsing the two passes into one looks like an obvious tidy-up
and produces a classifier that is wrong in ways no unit test notices — this was a real bug, caught by
cross-checking the C# classifier against an independent implementation.

Relatedly, resolution, codec, container, dates, studio domains and performer ages are routed to their
real fields or dropped, never turned into tags. `1080p` describes the file, not the scene, and a
library that accumulates such tags stops being able to filter on anything meaningful.

**Performers come from the library, never from the shape of a string.** `angela.frost` and
`big.red.barn` are the same shape; nothing in the text tells you which is a person. Candidates
are resolved against performers the library already knows, and anything unresolved stays a tag or is
dropped. Any pattern-based "does this look like a name" heuristic produces constant false positives,
and no amount of tuning fixes it — it quietly fills a library with junk that is tedious to undo.

**Studios are linked, never created.** The tag list carries a bare lowercase domain fragment.
Creating a studio from it litters a curated library with lowercase near-duplicates, so a studio the
library does not already have is skipped, deliberately.

**Cover import is independent of the scalar overwrite flag.** It was gated behind that flag once,
with the result that the review dialog said "will replace" and then did nothing. That is a named
regression test now; do not re-couple them.

**Matching is on exact video file size, per video file.** Not on the name: torrent presentations
differ per uploader, so a name match is a heuristic that fails silently, while a byte count either
agrees or does not and survives the user renaming their files. Indexing per *video file inside* the
torrent is what lets each scene of a pack match independently. Sizes are not perfectly unique at
scale, so `TorrentIndex.Find` breaks ties by preferring the lowest fan-out — the most single-scene
source. That tiebreak is load-bearing, not defensive; do not delete it as redundant.

**A torrent no dialect recognises still parses.** It has matchable video files and simply proposes
nothing. Rejecting it outright would silently drop files out of the watched folder. Recognition is
also *structural* — the dialect checks that the metadata entry is a dictionary, not merely that the
key exists — so a file whose metadata is a string is never claimed and then read as though it held
the expected keys.

**Restraint is the product.** Nothing is written that the reviewer did not tick: fields are filled
only where empty unless a field is explicitly ticked, tags and performers are only ever *added*,
bulk apply never writes scalar fields at all, and packs are excluded from bulk apply by default
because a pack's tag list is the union across every scene it contains.

**Never hand-roll blob cleanup when replacing a cover.** The host registers a save-changes
interceptor on `CoveContext` that deletes the previous value of any modified `*BlobId` property once
the save completes. Deleting it next to the assignment is not merely redundant — the host's reference
counter opens its own scope and counts rows in the database, so before the save it still sees the old
reference and retains the blob. A test pins that guarantee.

### The cover fetch, which is where the sharp edges are

A cover URL arrives *inside* an untrusted file and is fetched **server-side**, from inside whatever
network the Cove host sits in. Everything below follows from that.

**No UI element ever points at an external host.** Not an `<img src>`, not a `fetch`, not a
stylesheet — every cover goes through the extension's own `GET …/cover?url=` endpoint. This is the
rule with the shortest path back to being broken, because reinstating it is one attribute:
`src={row.torrentCoverUrl}` renders correctly, reviews cleanly, and quietly makes the *browser* fetch
the image — no allowlist, no identifying User-Agent, no pacing, no cache, and the request goes out
before the operator has named the host. `api.ts` exports `coverUrl()`, and there is deliberately no
second way to display a cover.

**The host allowlist ships empty, and a refusal must say why.** Empty allows nothing, which is the
fail-safe direction, but it also means covers are off on every fresh install — so every path out of
the fetch returns a reason as well as a null blob, names the refused host, and words the
*unconfigured* case differently from the *rejected* one. Returning a silent null turns the shipped
default into a feature that merely looks broken.

**The pacing numbers are commitments, not tuning parameters.** Caching, per-host rate limiting and a
unique identifiable User-Agent are promises to the operators whose servers answer these requests,
and the exact values are part of the promise. `docs/DESIGN-DECISIONS.md` records them under *What
the cover fetch promises the tracker*. Weakening one is not a refactor. The product token in the
User-Agent is what a block rule matches, so renaming it takes away an operator's ability to block
this extension specifically — which is exactly the control the token exists to give them.

Two consequences that read as bugs if you do not know them: a *preview* waits far less time at the
rate limiter than an import does, because a pending `<img>` holds one of the browser's ~6 connections
per origin — over the ceiling it answers `429` with `Retry-After` and the page retries, which sends
fewer requests rather than more. And the preview cache is memory-only on purpose: a previewed cover
is by definition an image no video references, and the host deletes unreferenced blobs, so the blob
store is the wrong place to hold one.

## Pull requests

1. Open an issue describing the defect or the change first, **one defect per issue**. Bundling is how
   a fix becomes unreviewable.
2. Branch, and keep the change **focused**. Unrelated work goes in a separate PR.
3. **A behaviour change gets a test.** Treat `main` as releasable — "tests in a follow-up" is not
   accepted, for the same reason Cove does not accept it.
4. Run the five gates. Fill in the PR template.

### Commit messages

Imperative subject, no type prefix — matching Cove's own style:

```
Serve extension assets from the registered install directory
```

The body explains the **mechanism and the consequence**, not just what changed. "Fix cover bug" is
not a commit message; the paragraph explaining *why the wrong blob survived the save* is. A finding
that constrains the design belongs in `docs/`, not only in a commit message that nobody reads twice.

### One more identity trap

`extension.json` is the single source of truth for `id`, `name` and `version`, and `CoveExtensionBase`
reads them from it. **Never override `Id` in C#.** The install directory is named for the manifest
id and the host resolves asset URLs from the code `Id`, so a divergence 404s the UI bundle — which
fails the frontend's whole reconcile pass and silently withdraws *every* installed extension's UI,
not just this one.

## Licence

This project is licensed under the [GNU AGPL v3](LICENSE). By contributing, you agree that your
contributions are licensed under the same terms.
