# Native recorder and KG editor parity

The mapping recorder remains UI Maps native acquisition. UI Atlas supplies the
attended recording controls and the public KG editor. This repair restores the
observable recording/editor contract from test5 and the later Atlas popup and
grid fixes, without importing the proprietary workflow product.

## Observation contract

Raw Data Streams groups observations by native window family: Main Window,
Dialog Window, Tool Window, Borderless Window and Borderless Tool Window.
Instances retain bundle, frame, HWND, process lifetime, class, owner, native
styles, DPI and geometry. UIA, native HWND and MSAA attempts retain separate
responses and intervals, including empty and failed attempts. Provider parent
IDs are preserved within their attempt. Query HWND and reported native HWND are
separate facts. No inferred Office controls are added to raw MSAA evidence.

The recorder archives responses before canonical merging or survey processing.
Perception still receives the canonical accessibility packet; MSAA supplements
it only when UIA did not supply a tree. Raw World comes from UI Maps Perception,
Semantic World from Map drafts and reviews. The editor links those objects back
to the retained raw observations; the legacy builder is reused only for the Raw
Data Streams presentation contract, not as a second higher-world authority.

## Capture and rendering

Attended recording uses a bounded mapping acquisition profile, with a 20-second
acquisition budget, provider deadlines and node limits. Independent window
images have a separate five-second cumulative budget and 32-MiB image limit.
After acquisition, a second scoped topology and screenshot check verifies
unchanged pixels, native identity/geometry/order, and absence of observed input.
Provider completeness and temporal stability are independent. A partial but
verified stable observation can support verified screenshot/control pairing. A changed
or unverified observation remains a Survey and keeps its warning, while the editor
allows inspection of its recorded image regions and geometry. This is an
observed before/after consistency check, not an atomic Windows API snapshot.

| Editor mode | Recorded-evidence inspection |
| --- | --- |
| Window | Recorded window image |
| Controls | Control crops from the correct independent window plane, or safe visible composite regions |
| Overlay | Control crops over the full-opacity recorded image |
| Structure | Geometry blueprint |
| Structure Overlay | Blueprint over a translucent recorded image |

Popup controls use their own frame/HWND/provider and window image. Missing
window images and occlusion remain explicit; a parent screenshot cannot stand
in for an independently captured popup. Unverified observations can display
image regions cropped at the recorded control bounds, with an explicit alignment
warning. This does not change acquisition quality, reconstruct missing controls,
or establish that pixels and providers were captured simultaneously. Inherited
pixels and known occlusions cannot supply control crops. See the
[five-mode follow-up](2026-10-15-appmap-five-mode-parity.md).

Manual recording does not automatically traverse dialog tabs. Explicit auto
exploration keeps its existing bounded behavior. Grid selection, exploration,
schema review, exact native host binding, partial-coverage reporting and portable
tiles continue through the existing mapping helper and Map review adapter.

## Existing recordings and storage

Opening an existing UI Maps workspace regenerates the disposable editor
projection. Original `.mlrec` bundles, source episodes, Map identities, reviews
and predictions are retained. Existing provider parents and native classes can
therefore recover hierarchy/families without recapturing or changing hashes.
Older recordings lack archived pre-merge responses, independent window planes
or consistency evidence where those were never collected; these cannot be
reconstructed or relabelled stable. Record a new observation for verified crops.

Native protocol additions are optional for old readers: original provider
responses, native window metadata, independent images and consistency evidence.
Packet and recording integrity checks cover retained data. Import additionally
rejects recursive provider archives, invalid image scope/status and unsupported
claims of stable snapshot evidence.

## CLI, MapLock and distribution

`ui-atlas record --hwnd <handle> --out <recording.mlrec>` opens attended recording.
Start / Manual begins capture; Finish seals the bundle and builds the map. The
CLI and KG editor ship together with the native worker. Normal Visual Studio
Build/Rebuild and F5/Ctrl+F5 remain the development entry points.

MapLock launches the same deployment with a request ID and exact process/window
lifetime binding. Finish produces the portable workspace and a hash-bound
receipt. The private Map host imports an explicit snapshot; catalogs remain
separate and later edits are not silently synchronized. Both private workspaces
compile the public shared foundation and retain their private workflow dispatch.

Only mapping, CLI and KG editing are distributed publicly. The package boundary
excludes MapLock, workflow recording/runtime, localization and private diagnostics.
Updated source hashes and adaptation origins are recorded in provenance.

## Qualification

Regression coverage includes native provider archives and parent identities,
window family classification, input/topology/pixel consistency, contradictory
import evidence, all five rendering modes through the UI Maps backend, popup
image separation, portable grid reviews, CLI Start/Manual/Finish and the private
Map host snapshot handoff. Local qualification records and screenshots are kept
under `.codex-out/`; they are not part of the public package.

Fresh Word qualification retained a stable main-window observation and a stable
Font-dialog observation, with independent parent/dialog images and separate
native HWND, UIA and MSAA responses. All five editor modes were rendered from the
retained Font-dialog bundle. The live mapper now compares DWM visible-frame
bounds for native observations, avoiding false resize invalidation from the
outer Win32 border. Mapping screenshot workers have a three-second deadline;
generic capture profiles retain their original budgets.

The retained Word recording used for the video RCA projects 22 frames, all 3,927
raw controls and 3,871 parent links into Main Window, Dialog Window and Borderless
Tool Window families. Its SHA-256 remains unchanged. Independent Windows 10 and
clean-machine qualification are still separate release requirements.
