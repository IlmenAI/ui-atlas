# Excel live overlay: origin stability and ribbon gallery presentation

## Evidence and root cause

The recording `20261018_024059.mp4` shows the live mapper overlay jumping over
Excel's Inventory List template. The spoken report concerns that movement.

The native capture path uses DWM visible-frame bounds. The 150 ms overlay timer
first updated the mapper to that origin, then the older `RefreshRootBounds` path
overwrote it with `GetWindowRect` bounds. In the observed Excel window those
origins were `(2880, 381)` and `(2872, 373)`. Applying both to the same captured
controls introduced an eight-pixel offset in both axes and also defeated the
background refresh's origin comparison.

The tall orange rectangle was a separate presentation defect. Excel's UIA
provider reported Cell Styles as `DataGrid`, class `NetUIGalleryButtonGroup`,
with a 212 by 784 rectangle inside a 214 by 56 scroll viewport. Its capabilities
were Selection and ScrollItem, not Grid or Table. Atlas classified that command
gallery as a potential data grid and drew its complete content rectangle.

## Repair scope

- Give the live target watcher sole ownership of the timer's root-position
  update, retaining the recorded visible-frame versus outer-window convention.
- Freeze the root origin when a passive probe starts. Reject stale completions
  before changing the layer, anchors, controls or grid hit regions.
- Use captured root bounds when available, including native frames without a
  separate scoped-window list; use the matching convention for live fallback.
- Present Office command galleries as ordinary UIA controls, not extraction
  targets. Preserve other unqualified table candidates and actual native grids.
- Clip displayed controls through same-window, provider-reported ancestor scroll
  viewports. Keep original provider bounds in the source observation. Raw Data
  Streams, capture packets, map storage and grid extraction contracts are unchanged.

## Validation

New regression checks failed before the repair on native-frame position drift,
gallery classification and missing viewport clipping. The focused set passes
21 cases after repair, including legacy and native captures, repeated stationary
updates, passive refresh, real window movement, stale-result rejection,
same-window viewport ancestry and preservation of original bounds.

The broader Core run and the repository-root-configured boundary rerun establish
488 passing cases and two existing failures: native inspector containment and
the source absolute-path policy. Both failures were reproduced using the
pre-repair test binaries. Five other desktop test classes were outside this run.

A read-only probe against live Excel using the repaired assembly still recognizes
the worksheet and InventoryList as native grids and produces no Cell Styles grid
candidate. Validation used an isolated build output so the user's running
recording process was not terminated or replaced. The existing process needs a
normal Visual Studio rebuild/relaunch to load this repair.
