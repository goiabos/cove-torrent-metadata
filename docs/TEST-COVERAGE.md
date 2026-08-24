# Test coverage — where the suite actually stands

**Re-measured 2026-08-24** with
`dotnet test tests/Cove.TorrentMetadata.Tests.csproj --collect:"XPlat Code Coverage"`, against Cove
`875260c` (the `v1.3.0` commit, which is the pin). Numbers below are read out of the resulting
Cobertura report, not estimated.

**653 C# tests and 350 vitest, 0 failures, 0 skipped. 94.6% of lines and 90.6% of branches in
`Cove.TorrentMetadata`.**

> **The gate for the C# count is not this file.** It is stated in the workflow rules and derived in
> `tests/README.md`, and nowhere else — a third copy of a moving number drifts, and this
> document proved it by sitting at *120 tests, 88.8%* for two months while the suite grew fivefold.
> Treat the figure above as dated: it is true at the date in the line above it and makes no claim
> about today.

The tests are good ones — named for behaviour, asserting database state rather than return values,
and carrying the reason the rule exists in a comment. What follows is *where* the remaining 5.4%
sits, which is the only interesting part of a number like this.

## Per-file

| File | Lines | Branches |
|---|---|---|
| `CoverRateLimitHandler.cs` | 106/149 — **71.1%** | 59.3% |
| `CoverCache.cs` | 76/95 — **80.0%** | 83.3% |
| `CoverAddressPolicy.cs` | 57/70 — **81.4%** | 97.8% |
| `AppliedTorrentBaseline.cs` | 28/32 — **87.5%** | 94.4% |
| `CoverProxyService.cs` | 51/58 — **87.9%** | 80.6% |
| `CoverResolver.cs` | 84/94 — **89.4%** | 88.6% |
| `WriteFolderService.cs` | 138/153 — 90.2% | 84.6% |
| `FolderSignature.cs` | 30/33 — 90.9% | 100.0% |
| `SourceFolderSetting.cs` | 30/33 — 90.9% | 85.0% |
| `CoverRateLimiter.cs` | 156/165 — 94.5% | 91.4% |
| `TorrentMetadataExtension.cs` | 563/595 — 94.6% | 77.5% |
| `CoverHostAllowlist.cs` | 111/116 — 95.7% | 91.5% |
| `CoverFetcher.cs` | 72/75 — 96.0% | 93.2% |
| `TorrentIndex.cs` | 105/109 — 96.3% | 85.7% |
| `TagNameStyle.cs` | 34/35 — 97.1% | 91.7% |
| `TagClassifier.cs` | 53/54 — 98.1% | 92.3% |
| `TorrentApplyService.cs` | 425/432 — 98.4% | 92.5% |
| `StudioMatcher.cs` | 61/62 — 98.4% | 100.0% |
| `TorrentBatchService.cs` | 369/373 — 98.9% | 96.0% |
| `BencodeReader.cs` | 145/145 — 100.0% | 93.9% |
| `BencodeTorrent.cs` | 52/52 — 100.0% | 92.3% |
| `CoverPreviewCache.cs` | 42/42 — 100.0% | 91.7% |
| `CoverUserAgentHandler.cs` | 13/13 — 100.0% | 83.3% |
| `LibraryFiles.cs` | 10/10 — 100.0% | — |
| `PerformerMatcher.cs` | 49/49 — 100.0% | 100.0% |
| `TorrentDialect.cs` | 16/16 — 100.0% | — |
| `TorrentFileWalk.cs` | 34/34 — 100.0% | 100.0% |
| `TorrentMatchService.cs` | 218/218 — 100.0% | 100.0% |
| `TorrentMetadataSettings.cs` | 65/65 — 100.0% | 100.0% |
| `TorrentRelease.cs` | 45/45 — 100.0% | 91.7% |
| **Total** | **3238/3422 — 94.6%** | **90.6%** |

**The shape has inverted since this was first written, and that is the finding.** The 2026-08-14
measurement said the domain core was covered to the point of diminishing returns while the edges —
the host contract, the endpoints, settings persistence, the parser's hostile-input guards and the
whole frontend — carried the holes. Every one of those is now covered: `TorrentMetadataExtension.cs`
went from 62.2% to 94.4%, `TorrentMetadataSettings.cs` from 29.4% to 100%, `BencodeReader.cs` to
100%, and the frontend has 350 vitest over the modules that hold its decisions.

## What is not covered, and why

**The cover subsystem's failure paths**, as the table says. `CoverRateLimitHandler` and `CoverCache`
are the two weakest files in the tree, and both are weak in the same place: the branches that only
run when a remote host misbehaves — a refusal, a retry, an eviction under pressure. `CoverCache`'s
LRU eviction had no test at all until an eviction that spent two megabytes to reclaim a hundred
bytes survived a review pass, which is the argument for closing these rather than averaging them
away.

**Component state, deliberately.** `ReviewBody`, `MatchDialog`, `ReviewPane`, `SettingsPanel` and
the rest of `ui/src/*.tsx` have no tests. Pinning them needs jsdom and a stand-in for
`@cove/runtime/react`, and that stand-in would be a second definition of the host runtime contract
living in this repo. What bounds the risk is how little those components decide: every rule they
apply — what a proposal means, how the queue steps, what a filter admits, what an empty state says,
whether a close is granted — lives in a plain module beside them with its own tests, and the split
is checked by the shells holding no state of their own.

**`main.tsx` cannot be tested here at all.** It imports `@cove/runtime/react` and
`@cove/runtime/react-dom-client`, which resolve through the host's import map and have no
implementation in this repo, so no test can import it whatever it exports. Code worth covering moves
out of it rather than being reached through it.

**The two corpus canaries** in `TorrentReleaseTests` are excluded from a default run and are not
regression cover; `tests/README.md` explains what they are for and how to run them.

**A few genuinely unreachable lines** — a null branch in `TorrentApplyService.TryStoreCoverAsync`
and one in `ApplyPerformersAsync` — are left alone on purpose. Chasing them would mean contorting
the code to reach a state the callers cannot produce.
