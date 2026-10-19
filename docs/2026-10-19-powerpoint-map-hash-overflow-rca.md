# PowerPoint map finalization: graph hash overflow

## Report and cause

The retained bundle is `20261019-083838-powerpnt-b6598185.mlrec` (78,873,933
bytes, 50 observations). The manifest records a partial capture; this is separate
from the subsequent map-build crash. Raw observation JSON totals 210,904,103
uncompressed bytes. The bundle remains available for recovery.

The reported stack ends in `GraphSemantics.ComputeHash`, called while projecting
the UI Maps workspace. It serializes all nodes and edges into one JSON string,
then encodes that string back into UTF-8 for SHA-256. This creates graph-sized
buffers and requires a single string/transcoding allocation. The reported
`JsonReaderHelper.TranscodeHelper` overflow occurs in that conversion, before
hashing completes. It is not a PowerPoint control-coordinate failure.

Two supporting defects make this worse: local node/edge limits are checked only
after projection has calculated the hash, and `OverflowException` is absent
from the recorder's recoverable-error filter, allowing it to terminate the app.

## Fix plan and boundaries

1. Serialize canonical JSON directly into a SHA-256 stream. Preserve property
   order, indentation, escaping, enum/null handling, and collection order so
   existing semantic hashes remain compatible. Do not materialize whole-graph
   JSON, UTF-16 strings, or UTF-8 byte arrays for hashing.
2. Check the configured node/edge limits before hashing a build projection.
   Preserve settings and the existing generation on failure.
3. Treat overflow as a recoverable recorder/CLI failure, keeping the retained
   recording and the existing map. Do not catch fatal memory/stack failures.
4. Test old/new hash equality, sensitivity to content/order, bounded hashing
   allocation, limit-failure preservation, and the recorder error boundary.
5. Retry the original bundle through the normal catalog rebuild path; verify
   stored/reopened map integrity and unchanged recording hash. Document actual
   graph size, test results, and any remaining capture limitations.

This work changes map finalization only. It does not drop observations, reduce
recording fidelity, alter Raw Data Streams, raise user limits, or mark partial
capture evidence complete.

## Implementation and validation

Implemented streaming serialization to a SHA-256 `CryptoStream` in
`GraphSemantics.ComputeHash`. The anonymous `{ nodes, edges }` envelope and
`JsonDefaults.Options` are unchanged. Build projections enforce their configured
counts before calculating the hash. Recorder and top-level CLI error filters
now include `OverflowException`.

- **93 tests passed:** 80 hash/settings/graph-pipeline tests and 13 workspace
  tests. Six new hash tests cover byte-for-byte compatibility with the prior
  algorithm, empty/Unicode/nested data, content/order sensitivity, bounded
  allocations, and recovery classification without catching fatal failures.
- The allocation regression hashes more than 40 MB of JSON with less than 8 MB
  of managed allocation after warmup. It would fail the former full-copy path.
- Existing workspace tests verify that a rejected build leaves its previous
  database and active generation intact.
- Normal development **build 208** succeeded with zero warnings and errors.

The original catalog `map build` command completed successfully in **124.55 s**:

| Recovered evidence | Result |
| --- | ---: |
| Retained frames / bundles | 50 / 1 |
| Graph nodes | 167,842 |
| Graph edges | 182,151 |
| Canonical JSON bytes hashed | 1,088,295,800 |
| Saved SQLite database bytes | 1,076,011,008 |
| Raw Data Streams nodes | 145,911 |
| Raw World nodes | 13,670 |
| Semantic World nodes | 8,260 |

The canonical graph is larger than one GB: the old whole-string conversion
would require over two GB just for UTF-16 character storage, in addition to
its UTF-8 buffer and the resident graph. This corroborates the reported
transcoding overflow. Streaming completes without that conversion.

Reopening the authoritative workspace, reprojecting it, and running full graph
integrity validation succeeded. Its semantic hash matches the saved database,
and database node/edge counts match the reopened projection. Session history
now references the recovered recording. No map limit settings were changed
(they remain 1,000,000 nodes and 1,000,000 edges).

The deployed build 208 CLI's `map validate` command also returned `valid` on
recheck. The Build DLL in the CLI and viewer matches the built DLL (SHA-256
`4f82d34f90c46f9cb8e601d35fa5aaf12f8b82a4ef726a1bb2759c85d37b64b0`).
The repeated stack trace names the former string-returning serializer overload;
the deployed implementation uses its stream overload.

Semantic SHA-256:
`1d846760290d621b7e32b42935aed38eb75d3247e40d80a05306000cecb11370`.

Original bundle SHA-256, unchanged before/after recovery:
`1c65b97485036d332fc4477a4ab8c615eaeb310a87982bf27fcda6c045dec500`.

[Verification receipt](assets/2026-10-19-powerpoint-map-hash/verified-map.json)
and [recovery timing](assets/2026-10-19-powerpoint-map-hash/recovery-result.json).

The normal build's editor was opened on the recovered catalog map. Semantic
World and Raw Data Streams both loaded. Raw Data Streams reports 94 native
window observations and 145,817 source entries; main-window frame 48 loads
its saved image and Structure Overlay. The editor is left on that view.
[Recovered editor](assets/2026-10-19-powerpoint-map-hash/recovered-editor.png).

The recording's original `partial` outcome remains unchanged. Successful map
recovery does not imply that every provider supplied complete capture evidence.
Existing export/legacy-import size ceilings are outside this hashing repair;
the recovered map uses the authoritative UI Maps workspace and its projection.

## Reproduction

```powershell
dotnet test tests/UiAtlas.Core.Tests/UiAtlas.Core.Tests.csproj --filter 'FullyQualifiedName~GraphSemanticsTests|FullyQualifiedName~MapBuildSettingsTests|FullyQualifiedName~GraphPipelineTests'
dotnet test tests/UiAtlas.Core.Mapping.Tests/UiAtlas.Core.Mapping.Tests.csproj --filter 'FullyQualifiedName~AtlasWorkspaceTests'
dotnet build src/UiAtlas.Core.Cli/UiAtlas.Core.Cli.csproj
& './src/UiAtlas.Core.Cli/bin/Debug/net10.0-windows10.0.19041.0/ui-atlas.exe' map build 20261019-083838-powerpnt-b6598185
```

Tests and recovery used an isolated build under
`%USERPROFILE%\.codex\tmp\uiatlas-powerpoint-hash-20261019`; the normal build
was then deployed to the development CLI/viewer directory.
