## What and why

<!-- The mechanism and the consequence, not just the change. Link the issue. -->

## Checks

- [ ] `dotnet test tests/Cove.TorrentMetadata.Tests.csproj` passes, and a behaviour change has a test
- [ ] `cd src/Cove.TorrentMetadata/ui && npm run typecheck` is clean — required after any frontend change; **`npm run build` does not typecheck**
- [ ] `cd src/Cove.TorrentMetadata/ui && npm test` passes — a separate gate from the typecheck, which sees shapes rather than decisions
- [ ] `bash scripts/package.sh --verify` passes
- [ ] No new C# warning — CI compiles with `-warnaserror` and your local build does not, so a warning is a red PR (see CONTRIBUTING)
- [ ] No `.torrent` file is committed, and no test depends on one
