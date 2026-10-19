# MapLock and UI Atlas map-list loading RCA

Measured locally on 2026-10-19. This investigation changes no product code.

Follow-up: the approved performance repair is now implemented. See [shared catalog implementation and verification](2026-10-19-shared-catalog-performance.md).

## Finding

Both launchers should have comparable map-list performance when running the shared catalog implementation against the same library. They do in the matched deployment measurements below. The existing local UI Atlas Debug output predates that implementation, however, and still loads maps in-process. MapLock's current Debug output uses the newer process boundary. The shared-catalog migration added substantial serialization and transfer work to an already expensive list operation.

Rebuilding UI Atlas would align its behavior with MapLock. It would also give UI Atlas the same performance regression; rebuilding alone is not a performance fix.

## Measurements

All measurements use the same current library, `C:\Users\danie\AppData\Local\UiAtlas\Core`, returning nine map records. No imports, map builds, recording changes, or source-data replacements were performed.

The table measures the deployed `AtlasMapsLibrarySource.LoadAsync` implementations in an isolated reflection harness, including backend launch/transfer/decoding where applicable. Each runs twice in its own harness process, sequentially without competing benchmark processes. These are loader timings outside the Visual Studio debugger, not total F5 startup or WPF rendering timings. Normal OS cache, JIT, and GC variation remains.

| Deployed loader | Implementation | First load | Second load |
| --- | --- | ---: | ---: |
| UI Atlas existing Debug output | Previous in-process loader | 5.30 s | 4.30 s |
| MapLock current Debug output | Shared catalog process | 7.33 s | 8.30 s |
| UI Atlas published Release | Shared catalog process | 7.31 s | 8.12 s |
| MapLock published Release | Shared catalog process | 7.10 s | 7.94 s |

Building the presentation groups after those loads took 0.28–0.56 seconds. The matching Release results do not show a substantial MapLock-specific penalty. Debug optimization is therefore not a sufficient explanation for the observed difference.

The current catalog response for those nine records is **116,508,010 UTF-8 bytes**, approximately **116.5 MB / 111.1 MiB**. This is a local child-process pipe transfer, not a network download.

A separate phase probe against MapLock's staged backend measured:

- `LocalArtifactCatalog.ListMaps`: **3.02 seconds**.
- Opening each of the six maps with an Atlas workspace: **9–25 milliseconds each**.
- Loading each workspace's drafts: **60–277 milliseconds each**.
- Complete `AtlasCatalogCommand.LoadMaps`, including a fresh summary pass, workspace reads, and protobuf/base64 encoding: **4.47 seconds**.
- JSON serialization of the returned map array: **0.54 seconds**.

These phase measurements overlap and must not be added together: the complete `LoadMaps` measurement repeats the preceding summary/workspace operations. They also exclude subsequent shell-side JSON/protobuf parsing.

## Why the local Debug outputs differ

The running MapLock executable was observed at:

`C:\stuff\dev\successors\UIMaps.Core\uimaps-application\src\Launcher\bin\Debug\net10.0-windows10.0.19041.0\MapLock.exe`

Its shell assembly was built at 16:25:39 local time. Its staged backend recognizes the shared `catalog` command.

The existing UI Atlas Debug executable's assembly was built at 15:29:33 and its shell assembly at 15:14:20. Inspection of the compiled assembly confirms that it contains `UiAtlas.Launcher.AtlasMapsLibrarySource`, which directly calls `LocalArtifactCatalog.ListMaps`, `AtlasMapWorkspace.Open`, and `UiMapService.GetDraft` inside `Task.Run`. It does not serialize the library through a child process. Its tools copy rejects `catalog` as an unknown command.

No UI Atlas process was running when process inventory was taken, so the investigation establishes the state of its default Debug deployment rather than claiming to identify the exact executable used in a previous observation.

Current source in both hosts uses `UiAtlas.Shell.Catalog.AtlasMapsLibrarySource`. MapLock compiles the canonical shell sources through `UIMaps.SharedShell.csproj`; both hosts resolve the same default catalog root. No process/user/machine overrides for `UI-ATLAS_DATA_HOME` or `UIMAPS_ATLAS_EXE` were present in the investigation environment.

## Expensive path introduced by the shared catalog

1. `AtlasCatalogClient.RequestAsync` launches a new Atlas CLI process for each refresh and waits for its complete response.
2. `AtlasCatalogCommand.LoadMaps` first obtains all map summaries, then opens each workspace and loads its library entries, sessions, full drafts, and full revisions when present.
3. Each protobuf message is serialized and converted to base64. All of it is then wrapped in JSON and written through stdout.
4. The shell reads the complete response, deserializes JSON, decodes base64, and reconstructs all protobuf objects before showing the map list.
5. The view model traverses map entities again to derive presentation counts, statuses, and surfaces.

The separate Atlas process preserves the intended dependency boundary between Atlas and MapLock. Sending whole maps for a list page is the avoidable cost.

Source evidence:

- `src/UiAtlas.Shell/Catalog/AtlasCatalogClient.cs:45`: child-process request, complete stdout read, JSON deserialization.
- `src/UiAtlas.Core.Cli/AtlasCatalogCommand.cs:83`: full library materialization; lines 106–108 load sessions/drafts/revisions; line 138 encodes protobuf as base64.
- `src/UiAtlas.Shell/Catalog/AtlasMapsLibrarySource.cs:10`: complete response decoded into full map records.
- `src/UiAtlas.Core.Storage/LocalArtifactCatalog.cs:235`: summary read for every map database.
- `src/UiAtlas.Core.Storage/SqliteGraphStore.cs:99` and `:276`: counts and semantic-layer control counting through SQLite JSON property inspection.
- `src/UiAtlas.Shell/Core/MapsLibraryViewModel.cs:159` and `:342`: full load followed by presentation grouping.

There are two additional responsiveness costs in the shared implementation:

- `MapsView.xaml.cs:59` refreshes on every owner-window activation. `IsBusy` prevents concurrent refreshes, but a later focus return starts another complete load even if no map changed.
- The shared async client/source preserve the WPF synchronization context. JSON deserialization, protobuf decoding, and presentation work after their awaits can execute on the UI thread. This can cause visible pauses; the exact UI-thread pause under the user's Visual Studio debugger was not measured here.

## Recommended fix

1. Add a versioned, lightweight catalog-list response containing identities, paths/generation, application/version, status, timestamps, counts, and warning/review summaries. Preserve the separate Atlas backend and the shared source used by both launchers.
2. Load full draft/revision/evidence data only for operations that actually need it, such as opening a selected map, detailed review, or diagnostics. Audit other callers of the existing full `maps` response before changing the contract, including recording handoff and diagnostics.
3. Persist or cache list summaries when Atlas publishes a new map generation. Invalidate them on rebuild, review/readiness changes, archive/restore, import, deletion, and changes made by the other launcher. Support legacy projection entries without scanning all node JSON on every refresh. Preserve existing path/schema checks and generation checks for mutations.
4. Refresh incrementally when catalog state changes; keep the current list visible during refresh. An unchanged focus return should not reread and transfer all maps. Perform remaining decoding/presentation preparation off the UI thread.
5. Rebuild/stage both launchers together and verify fresh deployment compatibility. Compare both Debug and Release against this same library, measure response size and first/repeated load latency, and check that changes made in either launcher appear in the other.

## Reproduction artifacts

Ignored diagnostic harness: `.codex-out/catalog-probe/Probe.csproj` and `Program.cs`. Its `inspect` mode examines deployed loader IL; `load` invokes the actual loader and presentation grouping; `catalog` measures summary/workspace/serialization phases. It has no product-project references and disables repository build imports so it does not rebuild applications or stop running launchers.

The earlier exploratory CLI-only benchmark and JSON measurements are in `.codex-out/Measure-CatalogLoad.ps1` and `.codex-out/catalog-load-timings.json`. Its PowerShell response parsing was expensive; those initial 10–17 second child-process readings are not used as the controlled comparison above. The later loader harness calls the actual deployed .NET implementation directly.

No performance fix was applied in this RCA.
