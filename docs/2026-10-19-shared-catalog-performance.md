# One shared Atlas catalog with faster map-list loading

Implemented in the UI Atlas and UIMaps.Core launchers on 2026-10-19.

Both hosts use the canonical `AtlasCatalogClient` and `AtlasMapsLibrarySource`, backed by the same Atlas CLI implementation and catalog location. The default remains `%LOCALAPPDATA%\UiAtlas\Core`; an explicit `UI-ATLAS_DATA_HOME` override must be the same in both processes to select an alternative shared library. Each launcher packages its matching Atlas tools, while map and recording authority stays in the shared data directory.

## Loading behavior

- The new `maps-summary` operation returns display-only projections. It preserves application identity, rows, status, warnings, exact entity/source identity sets for deduplicated counts, and surface/grid ancestry. It omits assertions, matchers, evidence, relationships, dispositions, interactions, and other full-map content unnecessary for the list.
- Summaries are marked `SummaryOnly`; the shell records `IsSummary`. Projected revisions intentionally omit immutable/hash authority markers. They must not be used for publication, review, or localization.
- `map-list-cache-v1.json` is a disposable index in the shared catalog root. Both apps reuse it. Per-map stamps include the active generation, index, projection metadata, authority database, and committed WAL changes. Added/removed maps change the catalog version. Archive/restore, review, publish, and rebuild invalidate the affected entries. Malformed JSON cache contents are rebuilt from authority.
- Known-version requests return `NotModified` without map payloads. The shell reuses the existing records and presentation objects, preserving expansion state. Returning focus checks for changes without rebuilding an unchanged list.
- Reading the list no longer calculates semantic node counts by scanning every projection database. Legacy rows read validated metadata only; modern maps use their Atlas authority.
- JSON/protobuf decoding and presentation preparation run off the UI thread. A cancelled presentation load can retry the same cached records.
- `map-detail` reads a selected map from authority and verifies its catalog entry/generation. Diagnostics selects a Ready entry from summaries, then requests the full revision for localization. Opening/editing a map continues to use Atlas's existing viewer and authority.
- The existing full `maps` API and CLI behavior remain available. Recording handoff compares lightweight draft projections, including their ETags, instead of transferring every full draft twice.

The cache is an optimization, not another map store. Removing it loses no maps or recordings. Cache writes are atomic and an unavailable cache does not prevent authoritative loading. Existing catalog path/link checks remain enforced.

## Measurements on the current library

Same root and nine map records as the RCA. Measurements invoke each deployed .NET loader outside Visual Studio's debugger. They include child-process launch, catalog response, and shell decoding; they exclude window startup/rendering. Both rebuilt Debug windows were also checked directly and showed **six applications and nine maps**.

| Deployment | Cached catalog load | Unchanged refresh |
| --- | ---: | ---: |
| UI Atlas Debug | 0.664 s | 0.269 s |
| MapLock Debug | 0.975 s | 0.336 s |
| UI Atlas Release | 0.602 s | 0.242 s |
| MapLock Release | 0.675 s | 0.344 s |

The previous shared loaders took approximately **7–8 seconds**. Preparing presentation groups after changed responses took another approximately 0.14–0.26 seconds in this run. The real view model skips this work on unchanged refreshes; the diagnostic harness times it separately regardless.

The map-list response dropped from **116,508,010 bytes** to **6,225,432 bytes** (94.7% smaller). An unchanged response is **289 bytes**. Identity sets are retained so counts remain exact across recordings; this is not a lossy count approximation.

The first cache population took **4.37 seconds**. This cost occurs when a cache is unavailable; later changes regenerate affected entries. First use of freshly rebuilt deployments and runs concurrent with build/UI startup also varied substantially (up to approximately six seconds observed), so the table is a cached-library, warmed-runtime comparison, not a guarantee for cold process startup.

## Verification

- Both Launcher projects built in Debug and Release with zero warnings/errors in their final builds. Launcher remains the default startup project in both root solutions and the UIMaps application solution.
- UI Atlas and MapLock Debug windows each displayed the same six applications and nine maps. Both remain open for inspection.
- The real nine-map library's summary presentation was compared with full-map presentation: application metadata, counts, statuses, surface rows, map rows, warnings and review counts matched.
- 28 catalog/storage tests passed, including shared cache reuse, unchanged responses, malformed-cache recovery, archive/restore, review/publication, generation changes, deletion, stale detail rejection, and committed WAL invalidation before checkpoint.
- 75 MapLock application tests passed, including the real staged Atlas-process handoff and summary/detail behavior. After adding two view-model lifecycle tests, all 12 view-model tests passed, covering unchanged presentation retention and cancellation/retry.
- Three Atlas shell tests and eight Ambient Diagnostics tests passed.
- Both staged Atlas CLIs returned help successfully.

During verification an old WPF generated-file cache required a rebuild, and an already-open Atlas viewer held files needed for staging. The viewer was closed normally and the final MapLock build completed successfully; no forced termination of that viewer was used.

Diagnostic logs are retained under `ui-atlas/.codex-out/catalog-fast-*` and `UIMaps.Core/artifacts/catalog-fast-*`. The local timing/parity harness is `.codex-out/catalog-probe/Probe.csproj`.

See [the original RCA](2026-10-19-maplock-map-list-performance-rca.md) for the before-change evidence.
