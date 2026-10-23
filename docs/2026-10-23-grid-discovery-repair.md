# Grid discovery repair — 23 October 2026

This repair is now included in **Phase 4.2.14 — Grid discovery and continued
recording** in the UIMaps.Core documentation repository. The phase also fixes the
terminal adaptive-drain call made before Table Explorer, which prevented newly
opened windows from being retained after Resume, and the native owner-family
filter that omitted VHP's booking window. Build 565 passed the combined live
grid-exploration → booking → UI Map sequence. The results below describe the
earlier grid-only build 560. See
`UIMaps.Core/docs/UIMaps - Phase 4.2.14 - Implementation Spec.md` for the RCA and
`UIMaps - Phase 4.2.14 - Qualification.md` for the combined acceptance evidence.

## Cause

Phase 4.2.14 also includes the later Progress semantic-surface correction:
native owner-only windows with zero area stay in raw ownership evidence but
cannot create a Map surface. Historical projections check all source geometry,
preserving legitimate previously observed and image-only windows. The Map source
change is mirrored in `vendor/uimaps/uimaps-map` and UIMaps.Core; the vendored
source provenance entry points here and the full phase RCA/qualification above.

The default native recorder preserved provider evidence but bypassed the older
visual discovery path. VHP exposes `Arrival Guest List` as a `ProBrowser` pane
with `ScrollPattern`, not `GridPattern` or `TablePattern`. The mapper therefore
drew ordinary accessibility highlights and never offered Table Explorer.

Two downstream assumptions also blocked the restored candidate: image exploration
required Grid/Table capability before using ScrollPattern, and header detection
assumed a ruled header at the top of the control. VHP has a caption above the
headers and draws most column separators only in the body.
Once capture was restored, live validation exposed the same header-only divider
assumption in schema extraction: every label was sent to the reader as one cell.

## Change

- Inspect the exact retained screenshot asynchronously for two-dimensional table
  geometry within visible scroll panes. Present a derived grid candidate without
  changing the raw provider controls, raw image, or native-support claim.
- Bind exploration and review back to the actual provider host and retained
  evidence. Reject changed/ambiguous hosts and stale discovery completions.
  Preserve candidates across compatible passive refreshes and remove hidden ones.
- Allow a geometrically sealed ScrollPattern host in image exploration. Native
  value reading still requires its existing capabilities and qualification.
- Recognize a short caption above the header. An unruled header requires labels
  across at least three established body columns; empty strips and isolated
  captions fail. Schema review remains explicit.
- Derive schema dividers from a ruled body when multiple row boundaries support
  them, and require support in the original tiles to reject stitching borders.
  The text reader still receives only individual header crops.

Foreground, occlusion, process identity, user takeover, scrolling limits, and
restoration checks remain in force.

## Validation and deployment

Final source builds successfully. Build **560** is staged in the running MapLock
launcher's `atlas` directory. SHA-256 hashes of both changed runtime assemblies
match the source build; the previous package files are backed up locally.

- 53 mapper, native exploration, overlay, and schema-review desktop checks pass
  on the final build (`grid-repair-final-desktop-tests.log`).
- 24 schema-extraction and header-layout checks pass, including rejection of
  stitched borders and unsupported body-only dividers (`grid-repair-schema-tests.log`).
- The preceding 35 layout, registration, and export checks also passed. The
  exact retained VHP screenshot passes with scrollbars excluded:
  its header is 37 pixels tall and follows the caption.
- 28 acquisition checks pass. Run this group separately from other acquisition
  tests because the operation gate is shared across processes and test classes.
- The live packaged recorder detected VHP's grid and opened Table Explorer. This
  exposed the caption and unruled-header cases, which were subsequently repaired
  and verified by retained-pixel replay and regression tests.

After the user dismissed Windows `Shell Handwriting Canvas`, the final deployed
build completed VHP detection, Table Explorer capture, and header extraction:

- `vhp-final.mlrec` records detection of `Arrival Guest List` from a native frame.
- Live exploration `60b3d7b5e55f47b7b19e8fc2a55e3cf3` retained 21 images, accepted
  18 views, and assembled a 3909 by 701 pixel image. Its receipt reports complete,
  continuous column coverage and incomplete row coverage (`VisibleRowBand`).
- The configured header reader returned separate names in schema review, including
  `ResNo`, `G`, `MB`, `Ref-No`, `Reserve Name`, `RmNo`, `S`, and `GuestName`.
  There are 38 draft intervals, including an apparent trailing blank interval;
  these were left for explicit review, not saved as an approved schema.
- Both scroll positions returned to their original values. Exact pixel restoration
  remains **failed** with `restoration-anchor-or-position-mismatch`: the retained
  header pixels are identical, but the body includes a changed focus outline and
  edge repaint pixels. The diagnostic was preserved; matching was not relaxed.

The earlier `unsafe-occluded-table` and off-screen `no-room-for-explorer` failures
remain in their logs. The final 53-test desktop run passed after the obstruction
was dismissed. No claim is made that all 75 VHP rows were read, that every header
was manually verified, or that a new grid schema was reviewed/published.

Local evidence is retained in `.codex-out/grid-repair/` and adjacent
`grid-repair-*.log` files, including the diagnostic recordings, pixel replay,
deployment manifests, and package backup. They are not committed customer data.
Live tile images and receipts remain in
`%LOCALAPPDATA%/UiAtlas/GridExplorations/60b3d7b5e55f47b7b19e8fc2a55e3cf3/`.
`grid-repair-final-replay.log` records independent retained-pixel geometry and
restoration comparison; it performs no text reading or live input.
