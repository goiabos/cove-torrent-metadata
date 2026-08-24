# Tests

653 tests covering parsing, tag classification, performer matching, the match/apply services, the
batch eligibility rules, cover import, the index's concurrency, and the endpoints — their JSON
contract and their authorization metadata.

The number above is the tally at the bottom of this file, and the two are the same number by
construction — they have disagreed before, which is why it is worth checking both when one moves.

    dotnet test tests/Cove.TorrentMetadata.Tests.csproj

**No test needs a torrent.** Every fixture is built in code, so a fresh clone with no tracker data runs
the whole suite green. That is a hard rule, not a convenience — see below.

`CoverImportTests` stubs the image host at `HttpMessageHandler` level and fakes `IBlobService`,
which is the only way to reach a path the rest of the suite cannot: `new TorrentApplyService(db)`
leaves both dependencies null and the fetch returns immediately. Both cover bugs that shipped are in
there as named regressions.

It also covers the host allowlist, and there the stub earns its keep twice over. The
`Requests` list is what lets a test assert that **no request was made at all** — for an SSRF the
request is the harm, so a discarded response is not a pass — and returning a `302` by hand is the
only way to exercise redirect checking, since the real handler would follow the hop before the
service ever saw it. The declared scope reaching the service is pinned separately, in
`ManifestTests`: everything here builds its own allowlist, so nothing else would notice the
registration being dropped.

`CoverProxyTests` maps the extension onto a `UseTestServer` host and stubs the image host into the
**registered** cover client, because the endpoint an `<img>` points at is where the interesting half
lives: the status codes the browser reacts to, the `Retry-After` the batch page's retry rides
on, and the `Cache-Control` that must be on a served cover and never on a refusal. Driving
`CoverProxyService` alone would assert none of those. It is also the only place the User-Agent is
checked on *preview* traffic, which is the whole point of the change — a browser `<img>` sent the
browser's own.

`BencodeReaderTests` asserts the reader's rejection surface one guard at a time, because
`BencodeReader` is the only thing standing between a hostile file in the watched folder and the
rest of the extension, and a single "malformed input is rejected" test proves almost none of it. The
depth cap is asserted from **both** sides — a cap tight enough to reject a real nested torrent is the
same defect wearing the other face. The Latin-1 fallback is asserted as behaviour rather than as a
guard: a mis-encoded filename has to round-trip to something matchable, since matching falls back to
basename. Note that `long.MinValue` is deliberately *not* parseable — digits accumulate positively
and are negated after, so its magnitude is one past `long.MaxValue` and the `checked` multiply
rejects it. Widening the accumulator to admit it would give up the overflow guard for a number no
torrent contains.

`TorrentReleaseTests` also carries the index-concurrency section, which uses real `Thread`s and
a `Barrier` rather than `Parallel.For` — the thread pool is free to run iterations serially, which
would make a race test pass by never racing.

`UploadEndpointTests` maps the extension's endpoints onto a `UseTestServer` host and posts real
multipart requests, because every rule guarding the upload — the extension check, the size cap,
base-name-only writes, the parse-check that deletes what it cannot read — lives in the endpoint
lambda rather than in a service, and the JSON it returns is a contract with `src/Cove.TorrentMetadata/ui/src/api.ts`. It
builds its torrents in-code, so it never silently skips itself. It got that treatment first; the rest of the suite followed.

`EndpointContractTests` covers the other six endpoints. Two things, for two different
reasons:

- **Response shapes**, asserted as an exact property-name set. `src/Cove.TorrentMetadata/ui/src/api.ts` hand-types every
  interface against C# property names and nothing checks they agree, so a rename is a silent
  frontend break — and not a 404, because unmapped `/api/*` paths return the SPA `index.html` and
  the caller dies on `Unexpected token '<'`. Exact rather than at-least, so a C# property the UI
  has never been told about also surfaces.
- **Permission metadata**, read off the endpoint rather than driven over HTTP.
  `RequireCovePermission` only attaches `CovePermissionRequirementMetadata`; enforcement is the
  host's, in `ExtensionManager`, which this test server does not run — so an expect-403 test would
  be unpassable here, not merely weak. It is worth asserting because **the host treats missing
  metadata as allow**: an extension endpoint with no Cove authorization metadata gets a log warning
  and is then served anonymously for backward compatibility. Dropping the convention from `/apply`
  opens a library write to anyone, quietly.

Its host keeps **one SQLite connection open for its lifetime**. An in-memory database dies with its
connection, so registering `CoveContext` by connection string gives every request an empty one —
six of the thirteen tests pass against nothing if that is done — the vacuous pass this suite exists
to avoid — and it was checked by making it happen.

`SettingsTests` covers the tag-name-style setting: parsed from the wire, written to the
host's key-value store, read back at startup. Its two failure modes are deliberately asymmetric —
a failed **read** keeps the defaults, because defaults are a usable answer and a settings problem
must never stop the extension loading; a failed **write** changes nothing and throws, because the
user asked for something and did not get it. An earlier version of the write assigned first and swallowed
nothing, so a failing store showed the new style all session and reverted on restart while telling
the caller it had failed.

The restart itself lives in `EndpointContractTests` rather than here: `TorrentMetadataExtension` exposes no
settings accessor, and adding one so a test could read it would be cutting a hole for the test.
`PUT /settings` on one host, `GET /settings` on a second holding the same store, which is also what
covers `SetStore`.

`ManifestTests` pins the manifest and action declarations. Most of it compares a literal to
a literal and is **documentation-shaped rather than defect-finding**: each value is a one-word change
that builds clean and then breaks something with no error — a route with a leading slash resolving against another
host, an icon outside the host's two-entry `ICON_MAP` rendering nothing, a `context-menu` action type
that is accepted and never displayed, a deleted `SuppressSuccessAlert` putting a wrong "queued" alert
over the review dialog. Their value is in the **names**, which carry the consequence; a red
`Names_the_page_route_without_a_leading_slash` explains itself in a way `Route_is_correct` never
would.

Two things it needs that are not obvious:

- **A manifest has to be applied first.** `Manifest` throws when the host has not injected one, and
  injection is through `IManifestAware.ApplyManifest`, an *explicit* interface implementation. `Id`
  reads `Manifest.Id` and both `GetUIManifest()` and `GetActions()` route through `Id`, so all three
  throw on a bare `new TorrentMetadataExtension()`.
- **The `Id` check reads the shipped `extension.json` off disk**, not a fixture, because the hazard
  is a divergence between two artifacts. A fixture catches only a C# override and is blind to an edit
  of `extension.json` — the likelier one, since that file is opened at every release to bump
  `version`, three lines under `id`. It is safe to read because it is committed and always copied to
  the output, unlike `dist-ui/`, which is gitignored: asserting against the built JS bundle would
  fail on a fresh clone.

## They used to be copies

These files once existed twice: here for safekeeping, and in `src/Cove.Tests/` in a Cove
checkout, which is where they actually built. Keeping that working meant modifying two files Cove
owns — `Cove.Tests.csproj` and `Cove.slnx` — and never staging either, and the two copies could
drift with nothing to detect it.

None of that was necessary. The fixtures are self-contained: every file builds its own `CoveContext`
on in-memory SQLite through a private `CreateContext()`, and `UploadEndpointTests` drives a raw
`WebHostBuilder` rather than Cove's host. The only thing Cove's test project ever supplied was
xunit, which `Cove.TorrentMetadata.Tests.csproj` now provides as a global `Using` — which is why these files
still carry no `using Xunit;`.

## No torrent is ever committed, so no test may depend on one

Torrents are the tracker's data. The tag lists, titles and filenames are the most identifying artefact in
the project, and git history cannot be un-published. `.gitignore` blocks `*.torrent` and
`ci.yml` fails the build if one is ever tracked, because `git add -f` walks through a `.gitignore`.

A contributor brings their own corpus. It follows that a test may not be pinned to a specific
torrent — `TorrentMatchServiceTests` was pinned to a single file out of one, and **twelve of its
fifteen tests silently returned** on any machine without it, reporting green while doing nothing
. Its fixture is now invented in code, with each tag chosen to drive one assertion.

Naming the file here would have been the same mistake in prose: a real torrent's name is as
identifying as its tag list, and this file is published.

### The two corpus canaries

`Every_sample_torrent_carries_the_tracker_metadata_block` and `No_content_tag_in_the_corpus_keeps_a_dot`
are the exception, and they are not regression tests. They are empirical claims about what a real tracker
actually emits — building their input would make them circular — so their value is as a canary on
the tracker's format.

They are `[Trait("Category", "Corpus")]` and **excluded from a default run** by
`VSTestTestCaseFilter` in the csproj. Excluded means absent from the results, which cannot be
mistaken for a pass; xunit v2 has no working dynamic skip (`Assert.Skip` is v3). Point them at a
corpus explicitly:

    TORRENT_CORPUS_DIR=/path/to/your/torrents dotnet test tests/Cove.TorrentMetadata.Tests.csproj --filter "Category=Corpus"

That path is a placeholder, deliberately: there is no corpus location to document here, because there
is no corpus in this repo and never will be. Point it at whatever folder of `.torrent` files you have.

Asked for without `TORRENT_CORPUS_DIR`, they fail with an explanation rather than passing. Both hold
against the 54-torrent sample and the 3218-torrent export they were written against.

## What the SQLite fixture cannot reach

Every fixture here is in-memory SQLite, and SQLite compiles in a ceiling of **32,766 parameters per
statement**. EF has no array-parameter form for it, so a `Contains` over a set becomes one parameter
per element and the statement dies at that width. Cove runs PostgreSQL, where Npgsql renders the same
expression as `= ANY(@p)` — one array parameter, no ceiling — so **none of this is reachable by a
user**. It is a property of the test database, and what it decides is which tests are writable here.

Where the ceiling actually sits, measured rather than reasoned about:

- **The torrent index does not contribute to it at all.** It used to: `ListAsync` filtered library
  files by `sizes.Contains(file.Size)` in SQL, one parameter per distinct indexed size — 139,141 on
  the real folder — so the overview could not be driven past roughly 32k indexed files. Moving
  that intersection into memory for unrelated performance reasons took the ceiling with it.
  **60,000 indexed entries against a one-video library passes today**, so a corpus-scale index test
  is writable now and was not when this section was first written.
- **The library side still has one**, at `TorrentBatchService.LoadAsync`'s
  `videoIds.Contains(video.Id)`. A library of 40,000 matched videos fails there with
  `SQLite Error 1: 'too many SQL variables'`. That is about 37× the 878 video files of the measured
  library.
- **The host reaches it before we do, while seeding.** Adding 40,000 `VideoFile` rows in one
  `SaveChangesAsync` throws from Cove's own `CoveContext.CollectAffectedVideoMetricIds` before any
  extension code runs, because it collects the affected ids through a single `IN` list. Chunking the
  seed at 2,000 rows works
  around it and takes about fourteen minutes, which is the practical reason no test goes to that
  scale, rather than the ceiling itself.

So the accurate statement is not that the overview cannot be driven at corpus scale. It can, on the
axis that matters — the torrent folder, which is the side that grows. What cannot be reached is a
library an order of magnitude larger than any this extension has been pointed at, and reaching it
would cost a quarter of an hour of seeding before the first assertion.

## On the count

The number at the top of this file is what `dotnet test` reports, with the two corpus canaries
excluded from the default run. It has been wrong twice, both times because something derived it a
second way and then went stale — a substring filter that swept in another project's tests, and a
second copy in a second document. So it is stated in one place, derived in one place, and both move
together.
