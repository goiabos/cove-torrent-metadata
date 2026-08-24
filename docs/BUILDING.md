# Building, testing and packaging

For contributors and anyone building from source. Installing a release needs none of this — see
the [README](../README.md).

## Building from source

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

## Installing what you built

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

## Testing

The suite in `tests/` needs no torrent and no tracker data — every fixture is built in code, so a
fresh clone runs it green:

```bash
dotnet test tests/Cove.TorrentMetadata.Tests.csproj
```

The frontend has its own suite — `cd src/Cove.TorrentMetadata/ui && npm test`, vitest over the
modules that hold every decision the browser makes: which fields are offered, what starts ticked,
exactly what an apply request contains, and how much a bulk apply would actually write. After any
frontend change, also run `npm run typecheck` — the esbuild bundle strips types without checking
them, so the build alone proves nothing about the contract.

CI runs both suites, the frontend typecheck and the packaging checks on every push and pull request,
and the release workflow runs them again before packaging — so a tag cannot publish over a red suite.

More detail for contributors: [DEV-SETUP.md](DEV-SETUP.md) reproduces the dev environment,
[TEST-COVERAGE.md](TEST-COVERAGE.md) is a measured statement of where the suite stands, and
[DESIGN-DECISIONS.md](DESIGN-DECISIONS.md) is the required reading before changing behaviour.

## Why AGPL

The licence is not really a free choice here. This extension compiles against `Cove.Sdk` *and*
`Cove.Data`, and it runs inside the host's process and its `AssemblyLoadContext`, reading and writing
host entities through `CoveContext`. Cove is AGPL-3.0, so matching it is the answer that raises no
question — a permissive licence would still be combinable, but only after an argument nobody needs to
have about an extension that cannot run outside an AGPL host anyway.

There are no per-file licence headers. The notice below and [LICENSE](../LICENSE) are what a reader
actually looks at.

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
