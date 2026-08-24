# Linux / Docker PostgreSQL setup — three defects

Found while setting up a from-source development environment on **Ubuntu 24.04 (WSL2, x86_64)** with
no pre-existing PostgreSQL installation. None of the three is WSL-specific; all reproduce on bare-metal
Debian/Ubuntu.

They share a root cause at the process level: **no automated test exercises managed PostgreSQL setup.**
CI's perf job runs against a `pgvector/pgvector` *service container* via `COVE_PERF_PG_*`, and the unit
tests use SQLite/InMemory, so `PostgresManagerService.StartAsync` never executes in CI. Anyone who
already had PostgreSQL installed silently took the working system-`pg_ctl` branch.

| # | Defect | Severity | Status |
|---|---|---|---|
| 1 | Pinned pgvector version deleted from PGDG pool | Blocks first run | **Fix drafted** |
| 2 | Flattened `.deb` layout breaks `sharedir` relocation | Blocks first run | Diagnosed, not fixed |
| 3 | `docker-compose.yml` uses pre-18 mount path | Blocks first run | Diagnosed, not fixed |

---

## 1. `PgvectorVersion` pin has rotted out of the PGDG pool

**Symptom** — first run on a Debian-family host with no system PostgreSQL:

```
Could not download postgresql-18-pgvector 0.8.2 for noble/amd64.
Configure Cove with an external pgvector-enabled PostgreSQL connection string or use the Docker package.
```

**Cause** — `src/Cove.Data/PostgresManagerService.cs` pins `PgvectorVersion = "0.8.2"`. PGDG's apt pool
carries only current builds and drops superseded ones, so a hardcoded version becomes a 404 as soon as
upstream moves on. The pin was last touched in `e088b61` (2026-05-24); it had rotted by 2026-08-13.

**Evidence** — for `postgresql-18-pgvector` / `noble` / `amd64` the pool now offers **0.8.4, 0.8.5,
0.8.6** only:

```
$ curl -sI ".../pgvector/postgresql-18-pgvector_0.8.6-1.pgdg24.04+1_amd64.deb"  -> 200
$ curl -sI ".../pgvector/postgresql-18-pgvector_0.8.2-1.pgdg24.04+1_amd64.deb"  -> 404
```

The PostgreSQL server, client, libpq and liburing packages all downloaded successfully; only pgvector
404'd.

**Fix drafted** — `TryDownloadDebPackageAsync` already accepts a candidate list, so the change is to try
several releases newest-first, keeping the existing constant last so pinned mirrors and the embedded
payload still resolve:

```csharp
private static readonly string[] PgvectorVersionCandidates = ["0.8.6", "0.8.5", "0.8.4", PgvectorVersion];
```

This survives one rotation instead of zero. It does not remove the underlying fragility — a periodic
CI check that the configured versions still resolve would.

---

## 2. Flattened `.deb` extraction defeats PostgreSQL's `sharedir` relocation

**Symptom** — with defect 1 worked around, `initdb` fails:

```
running bootstrap script ... FATAL: could not open directory
  "/usr/share/postgresql/18/timezonesets": No such file or directory
HINT: This may indicate an incomplete PostgreSQL installation, or that the file
  ".../pgsql/bin/postgres" has been moved away from its proper location.
initdb: removing contents of data directory ".../pgdata"
```

…even though the files are present at `$COVE_HOME/pgsql/share/timezonesets`.

**Cause** — PostgreSQL relocates its paths in `make_relative_path` (`src/port/path.c`) by taking the
compiled-in `bindir`/`sharedir` pair, computing the tail of `bindir` after their common prefix, and
requiring the *running* binary's directory to end with that same tail. Debian builds with:

```
bindir   = /usr/lib/postgresql/18/bin
sharedir = /usr/share/postgresql/18
```

Common prefix `/usr/`, so the required tail is `lib/postgresql/18/bin`. `InstallLinuxPostgresAsync`
extracts into a **flattened** layout — `$COVE_HOME/pgsql/bin` and `$COVE_HOME/pgsql/share` — which does
not end with that tail, so the match fails and `sharedir` falls back to the compiled-in absolute path.
`pkglibdir` is unaffected because its relative form is simply `../lib`.

**Evidence** — `pg_config` from the extracted tree, showing two of three relocated correctly:

```
$ $COVE_HOME/pgsql/bin/pg_config --sharedir --bindir --pkglibdir
/usr/share/postgresql/18                  <-- NOT relocated
/home/<user>/cove-testbed/pgsql/bin       <-- relocated
/home/<user>/cove-testbed/pgsql/lib       <-- relocated
```

`InitDbAsync` does pass `-L "{PgShareDir}"`, which resolves `initdb`'s *own* input files — but the
`postgres --boot` child it spawns computes its own `sharedir` and dies on `timezonesets`. So the `-L`
flag masks half the problem and cannot fix it alone.

**Suggested fix** — preserve the Debian layout on extraction so relocation resolves naturally: place
binaries at `<root>/lib/postgresql/<major>/bin` and share files at `<root>/share/postgresql/<major>`.
The existing `_binDirOverride` field already supports pointing `BinDir` at a non-default location. A
post-extraction assertion that `pg_config --sharedir` falls inside the instance home would turn a
confusing `initdb` failure into a clear one.

**Reproduce** — on a Debian-family host with **no** system PostgreSQL 18 (`pg_ctl` absent from `PATH`
and from `/usr/lib/postgresql/18/bin`):

```bash
rm -rf "$COVE_HOME/pgsql" "$COVE_HOME/pgdata"
COVE_HOME=/tmp/cove-probe dotnet run --project src/Cove.Api
```

Note that installing system PostgreSQL to work around this *masks the bug permanently*, because
`StartAsync` prefers the system `pg_ctl` branch whenever one is present.

**Scope** — the embedded release payload (`cove.pgvector/`) contains **pgvector only, not the server**,
so a released `linux-x64` build on a host without system PostgreSQL still calls `DownloadPostgresAsync`
and hits this. That matches the documented Linux experience in
`docs/user/getting-started/install-linux.mdx`:

> *"Otherwise, on a Debian- or Ubuntu-family distribution, it downloads the managed PostgreSQL
> components into the instance home."*

This should be confirmed against an actual release artifact — the above is traced from source, not
observed on a published build.

---

## 3. `docker-compose.yml` mounts the pre-18 data path

**Symptom** — `docker compose up` fails immediately on a fresh checkout; the `db` container exits 1:

```
Error: in 18+, these Docker images are configured to store database data in a
       format which is compatible with "pg_ctlcluster" ...
       Counter to that, there appears to be PostgreSQL data in:
         /var/lib/postgresql/data (unused mount/volume)
```

**Cause** — `docker/docker-compose.yml` pairs the PostgreSQL 18 image with the pre-18 mount point:

```yaml
db:
  image: pgvector/pgvector:pg18
  volumes:
    - ${COVE_DATA_DIR:-./cove-data}/pgdata:/var/lib/postgresql/data   # line 73
```

Since PostgreSQL 18, the official images store data in a major-version-specific subdirectory and the
entrypoint **rejects a mount at the legacy path** — it trips on the mount point itself, not on its
contents, so an empty directory fails just the same.

**Fix** — mount one level up and let the image manage the subdirectory:

```yaml
    - ${COVE_DATA_DIR:-./cove-data}/pgdata:/var/lib/postgresql
```

Existing installs need `pg_upgrade` or a dump/restore rather than a straight path swap, so this wants
a migration note in the release notes.

**Reproduce** — verified directly against the shipped configuration:

```bash
docker run -d --name probe -e POSTGRES_DB=cove -e POSTGRES_USER=cove -e POSTGRES_PASSWORD=cove \
  -v /tmp/probe-pgdata:/var/lib/postgresql/data pgvector/pgvector:pg18
docker inspect -f '{{.State.Status}} exit={{.State.ExitCode}}' probe   # -> exited exit=1
```

**Not affected** — `docker-compose.allinone.yml` mounts `/var/lib/postgresql/cove-data` and the
all-in-one image sets `PGDATA` to the same path, running its own `initdb` from `docker/s6/postgres-run.sh`
rather than the docker-library entrypoint, so the 18+ check never applies.

---

## The wider question: keep managed PostgreSQL at all?

Defects 1 and 2 both sit in a code path nothing tests and few users reach. Worth deciding deliberately:

**Keep and test it.** Auto-provisioning is a real differentiator for a self-hosted app — "download one
binary, run it" is a much lower barrier than "install PostgreSQL, create a database, configure a
connection string." If it stays, it needs a CI job on a clean Debian container that runs a real first
start with no system PostgreSQL present. Both defects here would have been caught by that single job.

**Or drop it and standardise on Docker.** Removes a large, platform-specific, network-dependent surface
(`PostgresManagerService` is ~1200 lines) in exchange for making Docker or a user-provided PostgreSQL
mandatory. Defect 3 shows the Docker path needs attention regardless.

Recommendation: whichever is chosen, add the clean-container CI job first. The current state — a
documented, shipped path that no test covers — is the costly option, because breakage surfaces as a
confusing first-run failure for exactly the users least equipped to diagnose it.

---

## Environment

```
Ubuntu 24.04.4 LTS (WSL2, networkingMode=nat), x86_64
.NET SDK 10.0.110 (Ubuntu noble-updates package)
Cove 1.1.1-dev.179
No system PostgreSQL installed
Verified working substitute: pgvector/pgvector:pg18 container -> PostgreSQL 18.4, pgvector 0.8.6
```
