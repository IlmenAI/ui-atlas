# PowerPoint Raw World: missing controls, duplicate menus, and tooltips

Source: `20261019-0918-23.4457014.mp4` (2m15s), reviewed with video frames and a local audio transcript. Recording: `20261019-083838-powerpnt-b6598185.mlrec`, 50 observations. This repair changes derived interpretation and presentation; sealed recording evidence is not rewritten.

## Feedback and evidence

| Video time | Feedback | Retained evidence |
|---|---|---|
| 00:00–00:32 | Two Change Case surfaces; the second has no command metadata | Frames 3 and 4 have the same native menu HWND 202070, accessible root name, owner and bounds. Frame 4's preferred tree contains only a UIA root and a native child HWND. Its retained MSAA response has 126 records, including all five visible commands. |
| 00:32–01:12 | Tooltips lack visible text and need a separate window type | `OfficeTooltip` windows are classified as popups. Frame 31's Presentation title UIA record is under the main-window query. Frame 7 has a direct tooltip record and a valid isolated image, but Controls view treats its root as a large structural container and omits its pixel crop. Both frames retain readable tooltip pixels. |
| 01:52–02:15 | Shape Fill appears three times; these should be observations of the same surface | Frames 47, 48 and 49 have HWND 53218626, accessible root name Shape Fill, the same owner and 284×495 bounds. Their reconstructed member counts were 88, 89 and 121; the complete member fingerprint was incorrectly used as family identity. Gradient and Texture are separate native child popup surfaces. |

Video frames: [Change Case controls](assets/2026-10-19-raw-world/before-010.jpg), [second observation](assets/2026-10-19-raw-world/before-020.jpg), [missing tooltip](assets/2026-10-19-raw-world/before-040.jpg), [repeated Shape Fill](assets/2026-10-19-raw-world/before-125.jpg).

The [automatic audio transcript](assets/2026-10-19-raw-world/automatic-transcript.txt) retains timestamps; transcription errors in technical terms have not been silently rewritten.

## Root causes

1. Perception normalizes only `AccessibilityPacket.Nodes`, the recorder's preferred/supplemented tree. `RawProviderResponses` contains additional successful MSAA data that the preferred tree omitted. The provider summary comes from the original responses, so it reports success while the world lacks those commands.
2. Popup family identity hashes every control's role, name and relative geometry. Provider coverage, selection, and submenu changes therefore create different surfaces. Conversely, empty tooltip trees all share the same content fingerprint and can incorrectly merge different tooltip texts.
3. Tooltip ownership is not recognized when a native tooltip root is reported through the owning application's tree. Nested popups reported through another popup also cannot be reassigned because reconstruction only considers main-window-owned branches.
4. Controls rendering intentionally omits large container crops. A tooltip's only text-bearing element often is that container, so the result is a blank rectangle even when pixels and its accessible name exist.

## Fix scope and rollout

- Complete perception from same-episode worker responses. Prefer existing exact semantic/geometry matches, add missing visible records and their source ancestors, and keep source identity and hierarchy. Offscreen raw records remain available in Raw Data Streams. Never import another frame's controls.
- Upgrade retained publications in memory with deterministic supplemental object IDs. Existing object IDs, source packets, reviews, and grid work remain intact.
- Identify named popup families by process instance, native owner, class, surface type and native title, or the unambiguous accessible root name corroborated by the same HWND and bounds. Office's menu native captions are empty; its accessible roots supply the names. Keep the content fingerprint on each observation. Unnamed surfaces retain conservative content-based identity; concurrent windows remain distinct.
- Add `RawTooltipWindow`, based on known native tooltip classes or a matching native tooltip accessibility root. Propagate it as `SemanticTooltipWindow`, retaining the source type for downstream consumers. A borderless tool window alone is not sufficient evidence of a tooltip.
- Resolve tooltip and nested-popup branches from this observation's process, owner chain, popup ancestry and geometry. Mere overlap cannot steal unrelated controls.
- Allow tooltip root pixel crops in Controls view. Text remains recorded evidence, not synthesized UI text.
- Verify reconstruction, stable family identity, ambiguity, immutable evidence, repeated replay, tooltip classification/crops, and the actual retained 50-frame PowerPoint map. Rebuild the development app and inspect the resulting editor.

Non-goals: no cross-frame scene completion; no changes to Raw Data Streams records, recorder consent, grid schemas/reviews, or map publication authority; no claim that missing provider data can be recovered from other observations.

## Verification

- Development build **216**: successful, zero warnings/errors.
- Mapping suite: 61 tests passed (60 in the full run, then the old version-string assertion was updated and its test passed). Includes live fixture capture, reviewed-grid retention, map package/handoff, and editor rendering.
- Presentation/geometry regression subset: 60 tests passed, including the two new tooltip crop cases.
- All 50 PowerPoint observations reprojected: **173,201 nodes, 187,985 edges, no integrity issues**. Raw World has **24 visible surface families**, down from 28. Change Case has frames 3/4 in one family; Shape Fill has frames 47/48/49 in one family. Gradient and Texture remain separate.
- Frame 4 has all five MSAA command records visible. Frame 31's tooltip record now belongs to its tooltip surface and has a visible region. `RawTooltipWindow` is propagated to two `SemanticTooltipWindow` families.
- All **145,911 Raw Data Streams nodes** remain present. Original bundle SHA-256 remains `1c65b97485036d332fc4477a4ab8c615eaeb310a87982bf27fcda6c045dec500`.
- Retained Semantic World assignments that used the original reported owner keep their reviews and diagnostic lineage. The validator accepts reconstruction v2 only with the existing exact source-surface and frame-evidence checks.
- Live editor verification on build 216: [frame 4 Controls](assets/2026-10-19-raw-world/after-change-case-frame4.png) shows all five commands; [tooltip Controls](assets/2026-10-19-raw-world/after-tooltip-controls.png) shows the recorded text; [type filter](assets/2026-10-19-raw-world/after-tooltip-type.png) distinguishes two `RawTooltipWindow` families; [Shape Fill](assets/2026-10-19-raw-world/after-shape-fill.png) has one surface and three observations. The editor is left open on Shape Fill frame 49.
- [Retained-map validation receipt](assets/2026-10-19-raw-world/validation.json) records the graph counts, surface families and frame memberships.

The native `NUIDialog` class is also now classified as a dialog, so Font and Insert Table are no longer labeled as ordinary windows.

Existing workspaces are upgraded on open, in memory. No new recording is needed. The adjacent `.db` projection may retain its older serialized cache until rebuilt/exported; the application and workspace CLI use the current reconstruction.

Remaining qualification: observations whose providers actually failed still lack those provider records; timing/alignment warnings remain honest. Named families are scoped to the recorded process and owner, and are not a claim of identity across application launches. Existing semantic drafts are preserved rather than silently consolidated or re-reviewed.
