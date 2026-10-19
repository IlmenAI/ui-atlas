# Excel recording smoke test: UI Atlas, then MapLock

Executed on 2026-10-19 using the existing Debug launcher deployments and native Windows UI automation. No application source was changed for this test.

**Result: launch, record, build, shared catalog, and editor handoff work. Capture quality does not pass.** Both saved recordings and maps pass structural validation, but every captured screen has partial control coverage. The requested Delete step could not be completed in the launcher Maps UI: it exposes Archive instead, which was used and is explicitly a test deviation.

## Procedure and environment

Launchers were closed and freshly opened one after another, with the UI Atlas workflow completed before the MapLock workflow:

- UI Atlas: `C:\stuff\dev\successors\ui-atlas\src\Launcher\bin\Debug\net10.0-windows10.0.19041.0\UiAtlas.exe`
- MapLock: `C:\stuff\dev\successors\UIMaps.Core\uimaps-application\src\Launcher\bin\Debug\net10.0-windows10.0.19041.0\MapLock.exe`
- Shared catalog: `C:\Users\danie\AppData\Local\UiAtlas\Core`.
- Target: the existing Excel workbook window, Excel version `16.0.20430.20182`.

Both recordings used the same short manual sequence: initial Home ribbon, select Insert, return to Home, open Format Cells on its Font tab, then Cancel. No workbook data or formatting was intentionally changed. Recording and finalization were driven through the launcher/Atlas recorder UI; the CLI was used afterward for read-only validation.

Before the first recording, the existing Excel map `20261018-072025-excel-c3e5072b` was archived. Before the second recording, the newly created first-test map was archived. Neither map nor its recording was permanently deleted. Other historical Excel maps were preserved.

Excel was initially minimized. Restoring it and refreshing the application picker enabled Record new map; this was not a confirmed launcher defect.

## Results

| Check | UI Atlas run | MapLock run |
| --- | --- | --- |
| Launcher opens | Pass | Pass |
| Delete existing Excel map in launcher | Unavailable; Archive used | Unavailable; Archive used |
| Start Excel recording | Pass | Pass; launches Atlas recorder |
| Finish and build map | Pass | Pass |
| Open UI KG Editor | Pass | Pass; correct Atlas viewer and map loaded |
| Recording structural validation | Valid | Valid |
| Map structural validation | Valid | Valid |
| Captured screens / screenshots | 5 / 5 | 5 / 5 |
| Missing screenshots | 0 | 0 |
| Semantic surfaces | 2 | 2 |
| Semantic controls | 784 | 457 |
| Complete interactions | 3 of 4 | 1 of 4 |
| Partial screen scans | 5 of 5 | 5 of 5 |
| Map quality status | Needs Review | Needs Review |

The raw count difference does not establish a launcher-specific cause. These are separate captures of a running application, and the second recording contains more provider timeouts. Both maps remain unreviewed drafts; the normal explicit-review requirement is distinct from the control-capture failures.

## Recorded artifacts

| Artifact | UI Atlas run | MapLock run |
| --- | --- | --- |
| Recording / map ID | `20261019-130737-excel-e84d903d` | `20261019-131419-excel-afa718e8` |
| Draft ID | `uimaps:map-draft:c0f1c8657bbe4b0998af54fe12e8d0ea` | `uimaps:map-draft:ce3736aeea9441ad87f4a9f782229814` |
| Recording start, UTC | 13:08:03 | 13:14:41 |
| Recording end, UTC | 13:11:48 | 13:18:24 |
| Map file last write, UTC | 13:11:52 | 13:18:27 |
| Recording bytes | 6,402,768 | 6,251,691 |
| Map database bytes | 29,085,696 | 15,814,656 |
| End-of-test map state | Archived before second run | Active draft |

Bundles are under `<catalog>\recordings\<ID>.mlrec`; databases and their `.db.uimaps` authority workspaces are under `<catalog>\maps`. Both recording bundles remain available in both launchers' Recordings lists.

## Editor and shared-catalog checks

- UI Atlas's editor displayed both semantic surfaces, raw-world evidence, and the recorded variants. The main Excel screenshot and sample control overlays rendered. Format Cells' independent window image rendered at its retained 1006 × 1104 resolution, but its control data was sparse.
- MapLock's Open action launched its staged `atlas\viewer\UiAtlas.Core.Desktop.exe` and loaded the second map. The editor exposed two semantic surfaces and 457 controls, with the expected recording/frame lineage. Its Format Cells surface again had only six native controls and zero UIA/MSAA records.
- MapLock editor visual inspection was limited by the secondary-display capture/geometry behavior: the automation screenshot showed a cropped center region and subsequent Window/Maximize clicks did not produce a verified view change. Editor handoff and loaded graph identity passed; a full visual rendering sign-off for that second editor did not. This test does not distinguish an application window-placement issue from an automation capture/input issue.
- Both launchers' Recordings views displayed the same 25-recording library, with the two new Excel IDs at the top. Atlas's Maps view also displayed MapLock's exact new draft ID and matching counts: two surfaces, 457 controls, five sources.
- Archive changes were reflected in the shared shell's active map lists. The recorder HUD's saved-map list still included archived maps without distinguishing them as archived.

## Failures and follow-up work

1. **Incomplete Excel accessibility capture.** All ten screen captures across the two runs were partial. Format Cells retained its image but both UIA and MSAA timed out and returned zero records; only six native panes/scrollbars remained in its semantic surface. The second run also had a ribbon capture with native-HWND, UIA, and MSAA timeouts. The retained provider records identify the failing layer; the underlying timeout cause needs separate RCA.
2. **Incomplete interaction capture.** The first run reports 3/4 complete interactions, the second 1/4. The first run's Cancel action lacked a resolved source control; the second run has three interactions marked unknown/failed. Actual Excel UI transitions occurred and all screenshots were saved, so a quality warning about an unconfirmed result must not be read as proof that its image is missing.
3. **Delete is absent from launcher Maps.** Archive removes an entry from the active list but is not deletion. The requested delete-and-recreate workflow therefore was not fully exercised.
4. **Archive visibility differs between shell and recorder HUD.** An archived map still appears in the recorder's saved-map list. Align filtering/status presentation with the shared library or explicitly indicate archive state there.
5. **Recordings loading has no visible progress state.** Both views initially showed an empty table and blank status. Atlas subsequently populated automatically; MapLock populated after Refresh was clicked. Manual Refresh being necessary is not established. Source inspection confirms an automatic Loaded refresh but no loading status before the asynchronous request. Show loading feedback and measure this path separately before calling it a catalog failure.
6. **Recorder elapsed-time display jumped during finalization.** The short recordings briefly displayed values such as `13:15:32` and `13:22:07` while building. The saved start/end timestamps were consistent with the actual short sessions. Investigate the HUD display separately from recording duration.

The next acceptance run should reproduce the same Excel sequence after resolving the provider timeouts, require complete control and interaction capture, exercise a defined Delete action, and finish visual checks of the MapLock-launched editor on the secondary display.

## Evidence

- [UI Atlas validation and quality output](assets/2026-10-19-launcher-excel-smoke/ui-atlas-validation.txt)
- [MapLock validation and quality output](assets/2026-10-19-launcher-excel-smoke/maplock-validation.txt)
- [Per-frame provider status summary](assets/2026-10-19-launcher-excel-smoke/capture-summary.json)
- [UI Atlas shared recordings screenshot](assets/2026-10-19-launcher-excel-smoke/ui-atlas-recordings.png) and [accessibility text](assets/2026-10-19-launcher-excel-smoke/ui-atlas-recordings.txt)
- [MapLock shared recordings screenshot](assets/2026-10-19-launcher-excel-smoke/maplock-recordings.png) and [accessibility text](assets/2026-10-19-launcher-excel-smoke/maplock-recordings.txt)
- [MapLock-created map visible in Atlas](assets/2026-10-19-launcher-excel-smoke/ui-atlas-maplock-map.png) and [exact map identity](assets/2026-10-19-launcher-excel-smoke/ui-atlas-maplock-map.txt)
- [MapLock-launched editor accessibility inspection](assets/2026-10-19-launcher-excel-smoke/maplock-editor.txt)

Validation used each launcher's own staged `ui-atlas.dll` with `recording validate <ID>`, `map validate <ID>`, and `map quality <ID>`. The non-strict quality command returns exit code zero even for Needs Review; the reported quality fields, not exit status alone, determine this test's outcome. No new build or automated test suite was run for this UI smoke test.
