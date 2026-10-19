# Deep survey for UI mapping

New UI Atlas mapping recordings now default to `deep-survey`, including recording
started by MapLock through the shared Atlas tools. Existing explicitly saved
profile choices and overrides remain supported; this machine has no such override.
The profile is frozen when a recording starts. Existing recordings are unchanged.

The shared profile is now version `1.1.0`. It prioritizes completeness of the
currently exposed application state:

- No overall, accessibility aggregate, window-count, node-count, or depth ceiling.
- The default accessibility timeout is **0**, meaning wait for completion or user
  cancellation. UIA, MSAA, and native HWND workers receive that policy. A finite
  accessibility timeout remains an explicit Settings option for Deep survey.
- One full-depth survey pass, retaining the configured fresh-worker retries;
  no artificial shallow/node-limited pass before the exhaustive traversal.
- Original UIA, MSAA and native HWND responses remain separate retained evidence.
- Target-process/window scope, screenshot verification, worker containment,
  episode size limits and explicit provider failure reporting remain in place.

This does not automatically explore unopened dialogs or application states. The
user still navigates the application during attended recording. Complete provider
coverage is independent of temporal stability and of map review/publication.
Map building uses retained evidence; it does not select another capture profile.

The default, settings UI and capture changes live in UI Atlas. The UI Maps
integration compiles the same canonical recorder sources and stages the same
Atlas tools in MapLock. Its session regression tests now exercise staged survey
merging with `thorough` and wait for actual provider startup when testing cancel.

## Live Excel verification

An isolated diagnostic harness called the actual `NativeRecorderCapture` with
default settings and the deployed provider worker, with Format Cells open in
Excel. No application settings or original catalog entries were modified. The
dialog was cancelled afterward without applying workbook edits.

The capture took **149.99 seconds**. The retained policy was `deep-survey/1.1.0`,
episode health was **Complete**, and before/after consistency was **stable**.

| Window | Native HWND | UIA | MSAA |
| --- | ---: | ---: | ---: |
| Format Cells | 6 | 651 | 3,570 |
| Excel owner window | 22 | 983 | 3,121 |

All six provider attempts completed. None reported time, node or depth truncation.
The dialog's UIA and MSAA trees both include Cancel. The projected raw streams
retain 8,353 records across providers/windows; this is not a deduplicated semantic
control count. [Retained capture summary](assets/2026-10-19-deep-survey/excel-capture.json).

The actual Perception packet validator accepted the episode. Atlas's
`map import-episode` then produced an isolated Survey draft with **1,792 entities**,
Complete capture health, and normal review requirements. No draft was published.
Diagnostic source, original episode and imported workspace remain under
`.codex-out/deep-survey-check`.

## Automated verification

- Recording-profile defaults, scope, overrides and validation: 4 passed.
- New exhaustive-provider/retry/cancellation tests and native parity: 10 passed.
- Full mapping regression suite excluding desktop-owning live tests: 62 passed
  (includes the 10 tests above).
- Settings UI, persistence and unlimited-timeout editing: 3 passed.
- Shared recorder policy, capture session, cancellation and isolation: 20 passed.
- Both Launcher projects built in Debug and Release with zero warnings/errors.
- Both updated Debug launchers opened. UI Atlas Settings visibly selected Deep
  survey for new recordings. MapLock invoked its staged Atlas `settings` command;
  that standalone window was not exposed by the desktop automation tool, so its
  visual contents were not verified. The verification process was stopped afterward.

The foundation provenance check is blocked by unrelated hash mismatches in
`ObservationNormalizer.cs`, `WorldObjectBuilder.cs`, `SurfaceSceneReconstructor.cs`
and `OfficeMsaaGeometry.cs` in the Perception foundation. The five recorder source
entries changed for this work have matching updated hashes. Those unrelated
Perception sources and their provenance entries were not changed here.
