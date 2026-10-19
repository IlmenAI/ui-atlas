# UI Atlas on the UI Maps native foundation

> **Current consolidation, 2026-10-19:** [Phase 4.1: UI Atlas and MapLock integration](../../UIMaps.Core/docs/UIMaps%20-%20Phase%204.1%20-%20Atlas%20and%20MapLock%20Integration.md) documents the implemented baseline. In particular, the later [shared Atlas library](2026-10-19-shared-atlas-library.md) replaces this rollout's separate-catalog/snapshot-import design. [Phase 4.2: Workflow Recorder](../../UIMaps.Core/docs/UIMaps%20-%20Phase%204.2%20-%20Workflow%20Recorder.md) specifies the private recorder to build on that baseline. These cross-repository specification links resolve in the combined checkout; standalone Atlas remains independent of private source.

Local rollout: 2026-10-14. UI Atlas remains the existing repository, on
`codex/atlas-uimaps-foundation`, descended from
`3735554abfb5979420e5b6acf07e95a84b66e8eb`. No remote push, tag, release, or
history replacement is part of this rollout.

## Public product and private product

The public deployment exposes the UI mapping recorder, CLI, and UI Knowledge
Graph Editor. It includes selected native Recorder, Protocol, Perception and Map
source under `vendor/uimaps`, with exact revisions and file hashes in
`provenance/uimaps-foundation.json`. A source build needs no private checkout.

The complete MapLock shell, workflow recording/authoring/runtime, localization,
live business-data acquisition services and product integrations remain private.
Historical Atlas projects remain in Git for compatibility; MCP, Firebird,
migration tools and consumer samples are not shipped as public entry points.

The initial foundation phase adapted legacy Atlas captures into UI Maps. This
follow-up replaces default acquisition with UI Maps' native episode recorder.
The old adapter remains for previously recorded `.mlrec` files.

## What an end user does

Run `ui-atlas.cmd recording start`, select an application, approve recording,
and choose the attended mode. Alternatively select the HWND directly:

```powershell
ui-atlas.cmd list windows
ui-atlas.cmd recording start 0x123456
# Explicit paths, also used by MapLock:
ui-atlas.cmd record --hwnd 0x123456 --out recording.mlrec
```

The existing Atlas toolbar, target selection, click handling and mapping
exploration controls remain. Each committed observation now calls the native
UI Maps `EpisodeCaptureService`: isolated pixel capture, native window topology,
and accessibility providers, with native coverage, clock and provider-attempt
metadata. Automatic exploration remains an explicit choice. Attended capture
does not automatically traverse dialog tabs.

Finish seals the recording, runs UI Maps Perception and Map construction,
saves the editable map, and offers the KG editor. The original native episode
is retained in the recording; the Atlas frame JSON is its presentation adapter.
No external perception service or account is needed for the default local path.

## The editor concepts remain

| Editor layer | Authority | Meaning |
|---|---|---|
| Raw Data Streams | Original native sensor packets and provider records, separated by frame, query window and provider | What the APIs reported; original packet and coverage previews remain inspectable |
| Raw World | UI Maps Perception publications | Perceived World Objects, including separately derived visual/OCR evidence |
| Semantic World | UI Maps Map drafts/entities and review state | Reusable surfaces, controls, reviewed grid schemas, and evidence-linked map candidates |
| Predicted | Existing Atlas speculative planning/session state | Hypotheses, distinct from observed entities and publication evidence |

The original layer selectors, screenshot/structure inspection, variants and
Predicted control stay in the KG editor. Native records expose the actual
provider runtime ID separately from their native evidence ID. Query-window
association is explicitly labeled and does not assert provider-reported popup
ownership. Original sensor packet previews omit binary pixel payloads for
readability; stored packets are unchanged.

`Review map` handles names, parents, accept/reject, observed interactions,
validation, and immutable publication. `Mapping source evidence` shows pixels,
sensor packets, Perception publications and retained grid reviews. Unresolved
review findings stay visible. Publishing knowledge never grants action authority.

## How the implementations connect

```text
Atlas CLI / toolbar
  -> ManualRecordingSession + NativeRecorderCapture
  -> UIMaps.Recorder.Windows EpisodeCaptureService
     -> isolated UIMaps.Recorder.ProviderProbe
  -> sealed .mlrec: original native episodes + PNGs + Atlas presentation
  -> AtlasMapWorkspace
     -> UIMaps.Perception.Core
     -> UIMaps.Map.Core drafts / reviews / publication
  -> AtlasMapProjection
     -> existing KG editor: Raw Data Streams / Raw World / Semantic World
     -> existing Predicted session overlay
```

Atlas retains its product UI, catalog, bundle sealing, attended event handling,
bounded mapping interaction helpers and compatibility readers. UI Maps owns
new sensor acquisition, Perception and authoritative map semantics. Historical
bundles pass through `AtlasEpisodeAdapter`, which labels unavailable provenance
as legacy, uses wall-clock-derived timing, and never invents native parity.
Native recordings preserve their monotonic clock and original episode metadata.
The default attended profile is a Survey, not a simultaneous stable Snapshot.

## Storage and sharing

Normal Atlas artifacts live under `%LOCALAPPDATA%\UiAtlas\Core`, or the explicit
output path. A new map consists of:

```text
sample.mlrec                         sealed recording, including native packets
sample.db                            disposable Atlas editor graph projection
sample.session.json                  recording list, grids and Predicted state
sample.db.uimaps/
  active.json                        committed generation pointer
  write.lock                         local writer exclusion
  generations/<generation>/
    atlas-map.json                   workspace source / draft / grid index
    map/map.db                       authoritative UI Maps sessions and drafts
    map/revisions/sha256/             immutable published revisions
    recordings/<sha256>.mlrec         retained original bundles
    evidence/*.episode.pb            original native or adapted legacy episode
    evidence/*.perception.pb          Perception publications
    grid-reviews/                    review JSON, images, capture manifests/tiles
```

New generations leave the prior authority intact on cancellation/failure. Reviews
require the current generation and draft etag. Cyclic parents are rejected.
Catalog copy/archive/delete carries the Map companion workspace.

Use full workspace export to share editable maps and evidence; moving only the
`.db` loses the authority. Full archives include screenshots and raw text, grid
capture tiles, and editor Predicted state. Import validates bounded paths, bundles,
publication lineage and grid image hashes and rebases local artifact references.
Predictions are restored as predictions. Immutable `.uimap` revision export is
available for graph-only sharing; labels and identity references can remain.

```powershell
ui-atlas.cmd map open <map-id>
ui-atlas.cmd map drafts sample.db
ui-atlas.cmd map validate-drafts sample.db
ui-atlas.cmd map review sample.db <draft-ref> rename <entity-ref> --etag <etag> --reviewer <name> --value 'Reviewed label'
ui-atlas.cmd map review sample.db <draft-ref> accept <entity-ref> --etag <etag> --reviewer <name>
ui-atlas.cmd map publish sample.db --reviewer <name>
ui-atlas.cmd map export-revision sample.db <revision-ref> --out application.uimap
ui-atlas.cmd map export-workspace sample.db --out application.atlasmap --allow-sensitive-evidence
ui-atlas.cmd map import-workspace application.atlasmap --out restored --allow-sensitive-evidence
ui-atlas.cmd map convert-recording existing.mlrec --out new-map.db
```

Existing `.db` / `.json` maps remain readable. Conversion preserves the original
recording. Legacy catalog rebuild retains old graph/curation under `legacy/`;
old curation IDs are not guessed onto new entities. Existing graph JSON formats
remain projections, not complete UI Maps workspace interchange.

## MapLock invocation and data handoff

MapLock's Maps recording action now launches the staged public Atlas CLI. The
picker supplies the exact HWND, PID and process start time; Atlas rechecks that
identity. A unique request folder is created under
`%LOCALAPPDATA%\UIMaps\atlas-recordings/<request-id>`.

MapLock invokes `record` with `--request-id`, `--result`,
`--expected-process-id` and `--expected-process-start`. The attended toolbar still
requires the user's Start/mode selection. After Finish, Atlas creates the map,
full archive, snapshot import protobuf and an atomic result receipt. MapLock
verifies invocation, process lifetime, paths and payload hash, then calls its
existing local Map service pipe with `map.workspace.import`.

The service verifies that the snapshots match the archive, retains the archive
under its Map root's `workspace-sources/<sha256>.atlasmap`, and atomically imports
unpublished drafts. The completion UI selects the exact returned draft refs.
Identical retries are idempotent. Existing edited/partial destination state causes
a conflict, never an overwrite. If import fails, the original Atlas recording and
workspace remain in the request folder for recovery.

**This is an explicit snapshot handoff.** Atlas and MapLock have separate catalogs;
there is no ongoing two-way synchronization. Later edits in one do not update the
other. MapLock's private Maps review UI operates on its imported draft. The full
archive retains the original evidence and review history; the import does not
turn that history into a shared live undo stack. Re-export/import an Atlas
workspace for independent editing in the public KG editor.

Both private workspaces use the exact public foundation sources through
`integration/UIMaps.SharedFoundation.targets`. Builds and publishes stage the
complete CLI/editor/native-worker deployment under `atlas/`. A complete alternate
deployment can be selected with `UIMAPS_ATLAS_EXE`. The connector script wires
source sharing; the private launcher and Map host dispatch changes also belong
in the private repositories and are not copied into the public product.

## Data grids

Selecting a grid and choosing Explore performs Atlas' existing explicitly
approved, bounded mapping exploration. The recorder pauses its competing input
handling, records the exact native host context, and the grid helper captures
viewports, movement/restoration evidence and a stitched image. This interaction
helper is separate from UI Maps' native sensor acquisition.

Extract schema reads selected header crops through the same local Windows OCR
provider used by UI Maps Perception. Small unreadable headers get deterministic
enlargement; silence stays unreadable. Optional explicitly configured Azure
header reading remains separate. The user reviews/edits columns and saves.

Saved reviews retain original images, capture manifests and hashed source tiles.
Only an exact bundle/frame/native-evidence match attaches the reviewed physical
columns to the Map grid host. Partial width remains partial. Semantic World shows
the grid and its reviewed columns with source links. Older or ambiguous reviews
remain `pending-host-binding` in the source evidence viewer; recording/exploring
the host again is needed to obtain a native binding.

Schema review does **not** qualify row acquisition or establish complete business
data coverage. Private MapLock live-grid acquisition and workflow consumers keep
their own approval, binding, coverage and runtime requirements.

## Parity follow-up

The [native recorder parity repair](2026-10-15-native-recorder-parity.md) restores
raw native families, provider trees and verified image pairing while retaining
UI Maps authority. Its qualification supersedes the initial native rollout's
capture and rendering checks below.

## Verification boundaries

The native integration tests exercise a real owned WPF target, native isolated
providers, sealed recording validation, all editor layers, reviewed partial grid
schema, grid tile portability, prediction roundtrip and actual CLI
Start/Manual/Finish controls. Separate private tests transfer the archive through
an isolated Map host pipe and verify exact drafts, idempotence and receipt checks.

This is not customer-application qualification: Word, OpenEdge and Abacre were
not used as live targets for this rollout. Existing desktop-activation test
failures are compared with prior binaries and reported separately. Same-host
profile installation and portable source builds do not establish independent
Windows 10/11 qualification, signing or release readiness.

Detailed local commands, test outcomes, package identity and visual evidence are
recorded in ignored `artifacts/uimaps-rollout-qualification.md`. Build/rebuild uses
the normal Visual Studio 2026 startup project; validation uses isolated outputs
and does not replace the user's installed application. Nothing is pushed.
