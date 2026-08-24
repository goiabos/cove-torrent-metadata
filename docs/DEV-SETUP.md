# Development setup

Reproducing the environment this extension was built in: Ubuntu 24.04 on WSL2, .NET 10, Cove from
source, PostgreSQL in Docker, against a restored copy of a real library.

Every step below cost a debug cycle at least once. The failure modes are recorded with the fix.

## Why Linux, not Windows

Cove's CI runs **every gating job on `ubuntu-latest`**, no project targets `net10.0-windows`, and the
build already branches for non-Windows (`Cove.Api.csproj` picks `pwsh` vs `powershell`). Linux is the
reference platform.

```
sudo apt install -y dotnet-sdk-10.0     # Ubuntu 24.04 ships 10.0.110 in noble-updates
```

Keep the repo on the WSL native filesystem (`~/...`). A repo under `/mnt/c/` crawls — cross-filesystem
I/O makes MSBuild and npm painfully slow.

## PostgreSQL in Docker, not managed

Cove's managed PostgreSQL is broken on a clean Debian/Ubuntu host; `docs/POSTGRES_LINUX_ISSUES.md`
has the reproductions.
More importantly: **installing system PostgreSQL permanently masks that bug**, because Cove prefers a
system `pg_ctl` whenever one exists. A container is invisible to that detection.

```bash
docker run -d --name cove-dev-db \
  -e POSTGRES_DB=cove -e POSTGRES_USER=cove -e POSTGRES_PASSWORD=cove \
  -p 5433:5432 -v cove-dev-pgdata:/var/lib/postgresql \
  pgvector/pgvector:pg18
```

**Mount `/var/lib/postgresql`, not `/var/lib/postgresql/data`.** PostgreSQL 18 images reject a mount at
the legacy path — it trips on the mount point itself, so even an empty directory fails. (Cove's own
`docker-compose.yml` still uses the old path; that's defect B3.)

Run Cove against it:

```bash
COVE_HOME=~/cove-testbed \
COVE__Postgres__Managed=false \
COVE__Postgres__ConnectionString="Host=127.0.0.1;Port=5433;Database=cove13;Username=cove;Password=cove" \
dotnet run --project src/Cove.Api
```

**`cove13`, not `cove`.** The extension targets Cove 1.3+ only, and the 1.3 upgrade is one-way — so
the database you point a 1.3 checkout at is a copy, made once and kept:

```bash
docker exec cove-dev-db psql -U cove -d postgres -c "CREATE DATABASE cove13 OWNER cove"
docker exec cove-dev-db bash -c 'pg_dump -U cove -d cove --no-owner --no-acl | psql -U cove -d cove13 -q'
```

Two things measured on 2026-08-20 make this less frightening than it sounds, and both are worth
knowing before an accident rather than after:

- **1.3 does not migrate a database just because it opened it.** It logs `Database has 1 pending
  migration(s)` and refuses to touch it until someone approves the migration through
  `POST /api/database/migrate`. Pointing 1.3 at the wrong database is recoverable; approving the
  migration is not.
- **It will refuse outright on a library with name conflicts** — and a real one has them. The
  testbed library reported 51 unresolved groups (102 claims) and could not be migrated until they
  were resolved in the latest 1.2.x. `dotnet ef database update` cannot bypass this either; the
  migration carries its own SQL guard.

WSL in `nat` mode has its own network namespace, so a Cove on Windows and a Cove in WSL **cannot**
collide on ports. (In `networkingMode=mirrored` they would.)

## Client tools are needed separately

Restore, backup and the migration button all shell out to `pg_dump` / `psql`. With external PostgreSQL
they must be on `PATH`:

```bash
sudo apt install -y postgresql-client-18   # from the PGDG repo
```

Install the **client** package only — verified not to ship `pg_ctl`, so it won't trigger Cove's
system-PostgreSQL detection and mask defect B2. Client 18 is required; `pg_dump` refuses to dump a
newer server.

## Restoring a library backup

Cove's plain `pg_dump --clean` output emits DROPs in *creation* order, so a primary key is dropped
before the foreign keys depending on it. Restoring into a populated database fails. Cove's own restore
path resets the schema first; doing it by hand needs the same:

```bash
docker exec cove-dev-db psql -U cove -d cove -v ON_ERROR_STOP=1 \
  -c 'DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;'
docker exec -i cove-dev-db psql -U cove -d cove -v ON_ERROR_STOP=1 -q < backup.sql
```

**`docker exec` needs `-i` to forward stdin.** Without it psql receives nothing, does nothing, and
prints nothing — it looks like success.

A backup from an older Cove will show "Database Update Required" on first load. That gate is
intentional (Cove never auto-migrates a non-empty database). After migrating, the testbed schema is
**newer than the source install** — don't restore it back.

## Media paths and blobs do not come with the database

A restored database references the *source machine's* paths and blob store. Two separate fixes:

**1. Paths.** Cove normalises to forward slashes, so only the drive root differs. Ten columns hold a
path: `folders.Path` and `files.Path` are authoritative, and `MinPath`/`MaxPath` on videos, images,
audios and text_documents are denormalised copies. **Miss one and playback fails while the library
still looks fine**, because the list pages read the denormalised columns and the player reads
`files.Path`.

Run it with **`docker exec -i`** — the same stdin trap as a restore. Without `-i` psql reads nothing,
updates nothing, prints nothing and exits 0, so the rewrite looks applied and is not:

```bash
docker exec -i cove-dev-db psql -U cove -d cove -v ON_ERROR_STOP=1 <<'SQL'
… the statements below …
SQL
```

```sql
-- Any drive letter, lowercased for the mount point: '/mnt/E' does not exist, '/mnt/e' does.
UPDATE folders        SET "Path"    = '/mnt/' || lower(left("Path",1))    || substring("Path"    from 3) WHERE "Path"    ~ '^[A-Za-z]:/';
UPDATE files          SET "Path"    = '/mnt/' || lower(left("Path",1))    || substring("Path"    from 3) WHERE "Path"    ~ '^[A-Za-z]:/';
UPDATE videos         SET "MinPath" = '/mnt/' || lower(left("MinPath",1)) || substring("MinPath" from 3) WHERE "MinPath" ~ '^[A-Za-z]:/';
UPDATE videos         SET "MaxPath" = '/mnt/' || lower(left("MaxPath",1)) || substring("MaxPath" from 3) WHERE "MaxPath" ~ '^[A-Za-z]:/';
UPDATE images         SET "MinPath" = '/mnt/' || lower(left("MinPath",1)) || substring("MinPath" from 3) WHERE "MinPath" ~ '^[A-Za-z]:/';
UPDATE images         SET "MaxPath" = '/mnt/' || lower(left("MaxPath",1)) || substring("MaxPath" from 3) WHERE "MaxPath" ~ '^[A-Za-z]:/';
UPDATE audios         SET "MinPath" = '/mnt/' || lower(left("MinPath",1)) || substring("MinPath" from 3) WHERE "MinPath" ~ '^[A-Za-z]:/';
UPDATE audios         SET "MaxPath" = '/mnt/' || lower(left("MaxPath",1)) || substring("MaxPath" from 3) WHERE "MaxPath" ~ '^[A-Za-z]:/';
UPDATE text_documents SET "MinPath" = '/mnt/' || lower(left("MinPath",1)) || substring("MinPath" from 3) WHERE "MinPath" ~ '^[A-Za-z]:/';
UPDATE text_documents SET "MaxPath" = '/mnt/' || lower(left("MaxPath",1)) || substring("MaxPath" from 3) WHERE "MaxPath" ~ '^[A-Za-z]:/';
```

Drvfs is case-insensitive below the mount point, so only the drive letter needs lowercasing — a
library folder cased differently on disk still resolves.

Confirm it took. `UPDATE n` lines scrolling past are not confirmation — a silent no-op prints
nothing at all, which is easy to read as "no errors":

```bash
# Must report 0. No -i needed here: -c passes the query as an argument, not on stdin.
docker exec cove-dev-db psql -U cove -d cove -c \
  "SELECT count(*) FILTER (WHERE \"Path\" ~ '^[A-Za-z]:/') AS windows_paths FROM files;"
```

Then spot-check that a rewritten path is a file that actually exists, which catches a drive that is
not mounted as well as a bad rewrite:

```bash
docker exec cove-dev-db psql -U cove -d cove -t -A -c 'SELECT "MinPath" FROM videos LIMIT 5;' \
  | while IFS= read -r p; do [ -f "$p" ] && echo "OK   $p" || echo "MISS $p"; done
```

> **This rewrite is the first thing a database restore throws away.** Every snapshot in
> `~/cove-testbed` was dumped *before* the rewrite, so restoring any of them — through Cove's restore
> button or by piping a `.sql` into `psql` — silently puts the drive-letter paths back. Nothing logs
> it as an error; the symptom is that the library browses normally and no video plays, exactly as it
> did the day the backup was first restored. Re-run the block above after **every** restore, or
> re-dump a snapshot once paths are fixed so the rollback point is a working one.

**2. Blobs.** Covers and performer images live on disk at `generatedPath/blobs`, sharded by id prefix.
The database holds only `ImageBlobId`. Copy them:

```bash
cp -r /mnt/c/Users/<user>/AppData/Local/cove/generated/blobs ~/cove-testbed/generated/blobs
```

`generated/` also holds `previews` (1.8 G), `thumbnails`, `screenshots`, `vtt` — not needed for covers.

Media streams over WSL's drvfs bridge: fine for playing one file, **do not run a library rescan or bulk
thumbnail generation**. Note `covePaths` in the restored config still holds Windows paths, so a scan
would misbehave until those are remapped too.

**Leaving "Library Paths" empty is the safe testbed state, and it does not stop playback.** Cove's
serving path does no library-root check at all: `StreamService` composes `ParentFolder.Path +
Basename` and calls `File.Exists`, trusting the stored path verbatim (`Cove.Api/Services/StreamService.cs`
around the `filePath` resolution; the transcode, caption, audio and text paths are identical).
An empty list disables *ingestion* — `ResolveScanTargets` yields zero targets, so an accidental scan
over drvfs cannot happen — and it also disables downloads (`ResolveLibraryRoot` throws) and the
folder picker (403 `OUTSIDE_LIBRARY`), while the setup wizard offers itself on load until dismissed.
None of that matters for exercising this extension, which matches on file size read from the database.
So: an empty list is a *feature* here, and "Library Paths is empty" is never the explanation for a
video that will not play — check the stored paths instead.

## Running the extension

### Which Cove to install into

The testbed, always — a Cove checkout run from source against `~/cove-testbed`. Never a Cove holding
a library you care about, for three separate reasons:

- **Bulk apply has no undo.** It is additive only, but reversing it is manual across tags,
  performers, URLs and remote ids on every match.
- **The extension is pinned to one host revision.** It compiles against `Cove.Data` internals and
  declares `minCoveVersion` in `extension.json`; that number must equal the version the checkout
  computes to (`scripts/package.sh` enforces exactly this). A packaged Cove on any other build either
  refuses to load the extension or fails at runtime on a missing member.
- **The clean-install test needs a clean Cove.** Step 3 of the release procedure installs the ZIP
  into a fresh `COVE_HOME` with an empty database. A real library is neither clean nor expendable, and
  using it defeats the point of the test.

### Per-session loop

```bash
docker start cove-dev-db                       # the container persists; it just is not running

cd <this repo>
bash scripts/package.sh --verify               # build + verify, no ZIP → artifacts/extension/

COVE_HOME=~/cove-testbed
mkdir -p "$COVE_HOME/extensions/io.github.goiabos.torrent-metadata"
cp -r artifacts/extension/. "$COVE_HOME/extensions/io.github.goiabos.torrent-metadata/"

cd ../cove
COVE_HOME=~/cove-testbed \
COVE__Postgres__Managed=false \
COVE__Postgres__ConnectionString="Host=127.0.0.1;Port=5433;Database=cove13;Username=cove;Password=cove" \
dotnet run --project src/Cove.Api
```

**Install from `artifacts/extension/`, not from `bin/Release/net10.0/`.** `package.sh` does a
`rm -rf artifacts` then `dotnet publish`, so the output holds only what this build produced; `bin/`
accumulates, and after an earlier rename it kept a stale assembly from the old name beside the current
entry DLL. The verify pass also fails on host-provided assemblies, shipped symbols and identity
leaks — none of which a bare `dotnet build` checks.

Torrents go in `~/cove-testbed/torrent-metadata/`, and the watch folder is named for the manifest id,
so it moved with that rename like everything else.

### Iterating

| Changed | What it needs |
|---|---|
| any `.cs` | republish, re-copy, **restart the API** |
| the frontend only | republish, re-copy, hard-refresh — no restart; the bundle URL is cache-busted by file mtime |
| the frontend at all | `cd src/Cove.TorrentMetadata/ui && npm run typecheck && npm test` — `npm run build` is esbuild alone and typechecks nothing, and the type gate cannot see what the browser decides to send (`src/review.ts`) |

Extension pages won't appear in the nav if `config.interface.menuItems` is customised: a saved menu
hides pages added afterwards. Navigate directly to `/torrent-metadata` to confirm the page renders; a
missing menu entry is not a failure signal.

Confirm from the log which id actually loaded, since a stale install directory loads happily:

```
[…] [Cove.Plugins.ExtensionManager] Extension io.github.goiabos.torrent-metadata (Torrent Metadata v0.8.0) initialized
```

### Moving a testbed off the old identity

The extension was renamed as a **clean break** — no dual-key reads, no migration code. Nothing
was ever released, so the only install that needs moving is a testbed, by hand:

```bash
COVE_HOME=~/cove-testbed
# 1. Uninstall the old extension through the API, then remove what uninstall leaves behind: the
#    install directory is named for the *old* manifest id, so it is a different directory entirely.
ls "$COVE_HOME/extensions"          # the stale one is whichever is not the current id
rm -rf "$COVE_HOME/extensions/<old-id>"
# 2. The watch folder moved with the name.
mv "$COVE_HOME/<old-watch-folder>" "$COVE_HOME/torrent-metadata"
```

Three things do *not* follow automatically, because every key changed:

- **Settings.** Uninstall does not delete `extension_data`, and the new id reads a different key, so
  the tag-name style reverts to its default. Set it again in the dialog.
- **Import status.** `VideoRemoteId.Endpoint` moved from `emp` to `torrent-metadata`, so already
  imported videos look unimported on the batch page. Re-applying is idempotent per video, but it will
  re-propose. To keep the old state instead:
  `UPDATE "VideoRemoteIds" SET "Endpoint" = 'torrent-metadata' WHERE "Endpoint" = 'emp';`
- **Provenance stamps.** Tags created by the old build carry `emp.source = emp`; the new key is
  `torrent-metadata.source = torrent-metadata`. The old custom-field definition and its values stay
  in the database until deleted, and the "undo the import" filter no longer finds them.

A fresh `COVE_HOME` avoids all three and is the recommended path.

## Snapshots before destructive experiments

Bulk apply has no undo. It is additive only (tags, performers, URL, remote id — never fields, never
overwrite), but reversing it is manual.

```bash
docker exec cove-dev-db pg_dump -U cove -d cove \
  --format=plain --clean --if-exists --no-owner --no-privileges > ~/cove-testbed/pre-batch.sql
```
