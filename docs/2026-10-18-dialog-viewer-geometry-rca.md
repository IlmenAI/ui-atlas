# Dialog viewer pixel isolation and MSAA coordinates

## Evidence and root causes

The supplied `20261018-0743-51.8076460.mp4` shows **Raw Data Streams** selected
throughout, including the transition at approximately 25 seconds from UIA-only
to UIA + MSAA. The affected saved recording is
`20261018-072025-excel-c3e5072b`, observation 3, Format Cells.

1. UIA returned **timeout, zero records** for that dialog. The old banner only
   said that no enabled control records existed; it concealed the useful cause
   in the provider inspector. An empty UIA-only canvas was not a lost successful
   UIA response. This repair does not relabel MSAA evidence as UIA or claim to
   repair Excel's UIA provider timeout.
2. MSAA returned **3,570 raw records**. Office SDM supplied physical window/client
   roots and nonclient chrome, but client descendants used twice the physical
   scale around the physical client origin. For example, OK was reported at
   `(3558,1385,151,54)`; its reconciled rectangle is `(3223,898,75,26)`.
3. The raw viewer expanded its viewport to the union of reported control
   rectangles and cropped those rectangles from the composite desktop image.
   Wrong MSAA coordinates therefore sampled the worksheet behind the dialog.
   Exact query-window membership alone did not guarantee correct pixel scope.
4. Raw World already selected the independent dialog image and clipped regions,
   but had not reconciled the MSAA coordinate spaces before scene reconstruction.
   The previous repair covered live recording highlights, not this saved-map path.

## Goals and boundaries

- Keep the standalone dialog canvas and crops inside its recorded pixel surface.
- Explain each provider's actual outcome in the main canvas banner.
- Correct qualified MSAA geometry in Perception/Raw World, before clipping and
  occlusion. Reopening existing retained maps must receive the correction.
- Preserve every raw record and byte, provider identity, query membership,
  separate popup observations, semantic reviews, and existing grid work.
- Do not reconstruct or silently normalize Raw Data Streams, invent missing
  UIA records, merge provider responses, or use live HWND state to repair history.

## Implementation rollout

- [x] Raw dialog evidence uses its independent HWND image when available. The
  legacy fallback is bounded to that frame's dialog and masks known occluders.
  The original full-frame screenshot remains available in the inspector.
- [x] The raw canvas bounds cannot grow when sources are toggled. Pixel crops
  additionally respect the declared display bounds.
- [x] When visible command rectangles lie wholly outside a dialog, retain their
  original geometry for inspection, clip canvas rendering, disable misleading
  crops for the affected provider, and show an explicit coordinate warning.
- [x] Show `UIA: timeout, 0 records` and the actual MSAA status/count directly.
  Raw World also reports provider outcomes and coordinate-correction provenance.
- [x] Reconcile Office SDM client geometry only when all visible MSAA edit
  rectangles corroborate the candidate DPI scale against multiple unique native
  EDTBX rectangles from the same observation and window. Require actual overflow
  corrected by that transform. Reject missing, foreign, ambiguous, already
  physical, and unsupported evidence.
- [x] Correct only MSAA client descendants. Leave roots, caption controls, UIA,
  other windows, raw sensor packets, and original coordinate properties intact.
  Applying reconstruction again is idempotent.
- [x] Add renderer regressions across Window, Controls, Overlay, Structure, and
  Structure Overlay, with isolated-image and legacy-image paths, repeated source
  toggles, and a deliberately overflowing dialog rectangle.

## Verification

The focused set passes **51 tests**, including eight coordinate qualification
cases and dialog rendering with both image paths. Existing owner-query popup
tests continue passing; they still retain their recorded popup pixels.

Replay of the original Excel map (without modifying its recording) validates
the graph and produces a **502 by 552** viewport in each affected mode. Raw World
renders **106 visible regions**, with **92 pixel crops** in Controls/Overlay.
The correction is supported by three distinct native edit rectangles. Visual
inspection of the actual renderer output confirms aligned fields, list items,
tabs, checkboxes and OK/Cancel, with no worksheet pixels in the dialog.

Raw Data Streams deliberately continues to expose the original inconsistent
MSAA rectangles, bounded to the dialog with a warning and no misleading control
crops. The corrected scene is in Raw World. UIA remains unavailable in this old
frame; a later successful capture is required to obtain actual UIA evidence.

Local diagnostic receipts are in the task's `uiatlas-raw-world-dialog-20261018`
directory: extracted video frames, `results/`, and eight `probe/` render images.
User recordings and images are not added to the repository.

Deployment: normal development build **182** completed with zero warnings and
errors. The broader related grid/mapping/reader set passed **150 tests**; three
unrelated native-window grid test classes were excluded. An initial broader
filter accidentally included those classes and was stopped after the known
native-grid window test failure; the corrected scoped run passed. The shared
foundation boundary check passed with **69 pinned files**.

The existing managed map was rebuilt and validated: **27,317 nodes, 27,517
edges**, semantic hash
`8b91cd0e8e118dac84dc8da980b1fc1d296d9eef1b32060f60ad19b188e17a17`.
The original recording SHA-256 remains
`1b5f95c6f28998bb9d291caa1b26157708aa2762122fad192b2e996b38906e6b`.
Map authority/review history was retained through the workspace generation flow.
The deployed editor was opened on that map and checked visually: Raw World
Structure and Overlay align with Format Cells; Raw Data Streams shows the UIA
timeout, and enabling MSAA retains the bounded dialog with its coordinate warning.
The editor is left on Raw World / Overlay for inspection. Initial loading of
this large map remains slow; the geometry repair does not claim to fix that.
