# Excel Format Cells: retained recording and dialog overlay repair

## Evidence and causes

The affected recording is `20261018-072025-excel-c3e5072b.mlrec`, with three
observations. Its original SHA-256 is
`1b5f95c6f28998bb9d291caa1b26157708aa2762122fad192b2e996b38906e6b`.

1. **Map finalization rejected valid JSON.** Frame 3 is 8,913,180 bytes. ZIP
   security accepts entries up to 64 MiB, but strict JSON validation separately
   read entries with an 8 MiB cap. The actual diagnostic was `json.strict at
   raw/observations/frame-000003.json: Entry exceeds read limit.` The workspace
   builder discarded the path and message, leaving only `json.strict` in the UI.
   Native frame JSON includes both the combined compatibility observations and
   the separate raw responses, so a dense frame can readily cross that cap.
2. **Partial acquisition was a truthful provider outcome.** Frame 3 is temporally
   stable, but UIA timed out for both Excel and Format Cells at roughly three
   seconds. MSAA returned 3,570 dialog entries and 2,617 main-window entries;
   native HWND enumeration returned another 6 and 22 respectively. A separate
   read-only UIA probe against the still-open dialog also exceeded 30 seconds.
   Increasing the capture deadline alone is not an established fix. Frame 1
   was interrupted by input; frame 2 was stable and complete.
3. **The overlay used the owner as its drawing surface.** Native observations
   retain the selected Excel window in `frame.Window`, with Format Cells in
   `ScopedWindows`. The overlay used only `frame.Window` for its occlusion target,
   so the dialog itself became a clipping obstacle. It also consumed controls
   across windows and subsequently replaced them with a UIA-only passive query.
   Native provider evidence IDs are opaque: treating every such ID as UIA lost
   the distinction between UIA, MSAA and bare HWND geometry.
4. **Office returned mixed coordinate spaces within its MSAA tree.** The dialog
   is `bosa_sdm_XL9`, reporting 192 DPI, on a display where its owner is at 96 DPI.
   Windows reports its physical client as `(2887,410,501,522)` and, in the
   dialog's own DPI context, as `1002x1044`. MSAA window/client roots are physical,
   but client descendants retain the larger local scale. For example, OK was
   reported at `(3558,1385,151,54)`, outside the real 502x552 dialog image.
   Using the measured client transform gives `(3223,898,75,26)`, matching the
   retained image. The same issue reproduced with unaware, system-aware,
   per-monitor and per-monitor-v2 caller contexts; changing caller DPI alone
   did not repair Office's descendant coordinates.

The Windows distinction between physical and logical coordinates is documented
in [Microsoft's accessibility scaling guidance](https://learn.microsoft.com/en-us/windows/win32/winauto/active-accessibility-and-windows-vista-screen-scaling).
The specific mixed Office response above was established from the recording,
live Win32 client measurements and live MSAA probes, rather than assumed from
that general documentation.

## Implemented changes

- Validate JSON within the existing archive entry bound. Duplicate keys, invalid
  JSON, checksums, archive size and compression protections remain enforced.
- Include the failing entry and reason in map-build validation errors.
- Register a recovered recording in an existing empty logical-map session,
  preserving other session state, so a later resume can find its source bundle.
- Choose the visible, enabled, frontmost recorded surface within the selected
  process/root-owner scope. Scope controls to that HWND and follow its own
  physical bounds, including dialogs outside the owner's rectangle.
- Resolve provider identity from the frame's separate raw responses. Display
  MSAA controls in the blue accessibility category and label the legend
  `UIA / MSAA`; bare native HWND records do not become UIA controls.
- Preserve the MSAA parent chain across passive UIA refreshes of the same
  surface/layer/size. A new observation, layer change, resize or surface switch
  replaces it. Reject stale, moved, hidden or destroyed probe targets.
- For live Office SDM overlays only, qualify the HWND/process, captured visible
  bounds and matching MSAA client root, then measure physical and target-context
  client sizes. Convert only MSAA descendants when those measurements differ
  and ordinary visible controls exhibit the demonstrated mismatch. Do not
  rescale window roots, non-client chrome, UIA or another window's records.
- Explain provider timeouts in the recorder's incomplete-observation status.

## Boundaries

The recording is not rewritten or reclassified as complete. Raw Data Streams
continue to expose what each provider reported, including the original Office
geometry. The DPI repair is a qualified live overlay projection, not a guessed
repair to historical source evidence. UIA can still time out; available MSAA
evidence is usable without claiming complete UIA coverage. Native grid support,
grid reviews, extraction and manual-versus-automatic capture choices are retained.

## Validation and recovery

- The original recording now validates without changing its bytes.
- Rebuilt its managed map: **27,317 nodes and 27,517 edges**; graph validation
  passes. Semantic hash:
  `c719eb88ea134fa4dc6ff27598c7bebe42de848ecbf548e6c22775b383d70c8e`.
- Before recovery, copied the existing session manifest to the task's local
  recovery-backup directory. The recovered session retains the original bundle.
- Focused tests cover valid JSON over 8 MiB, duplicate-key rejection over that
  boundary, archive protections, exact-window MSAA provenance, mixed-DPI client
  conversion, existing grid classifications, and stationary/moved overlays.
- A real owned-window regression verifies visible dialog boxes outside the
  owner, movement, preservation after an empty UIA refresh and replacement by
  the next recorded response.
- Replaying the retained Format Cells frame against the unchanged live HWND
  yields **44 visible MSAA overlays**, including OK, Cancel, font/style/size
  fields, tabs and checkboxes. Before the coordinate conversion, only six boxes
  survived the surface bounds checks.
- The focused set passed all 43 cases. The final broad run passed 493 cases with
  the same two previously established
  failures: native inspector containment and the repository absolute-path rule.
  Five other desktop test classes were excluded from that run.
- A live replay of the repaired overlay on the still-open Excel dialog was
  visually checked: boxes align with font controls, tabs, checkboxes and OK/Cancel.
  The worksheet is no longer the active overlay surface behind that modal.
- Normal development build **177** completed with zero warnings/errors and
  validates the recovered managed map. The idle old recorder was closed before
  replacing its binaries; Excel's open dialog and workbook were left intact.

Local receipts are under the task's `uiatlas-excel-dialog-fix-20261018` directory:
`results/`, `probe/dialog-result.json`, `probe/original-dialog.png`, and
`recovery-backup/`. These are local diagnostics, not committed user data.
