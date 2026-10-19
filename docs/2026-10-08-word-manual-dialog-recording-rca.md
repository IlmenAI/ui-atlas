# Word manual recording: automatic tabs and dialog-close interruption

## Session evidence

Inspected the original retained bundle in the local catalog:
`20261008-230838-winword-fca9bbb7.mlrec` (WINWORD, PID 15896,
selected/root HWND 1053920). It contains ten observations and ended as
`partial` at 23:09:28.854 UTC on October 8, 2026.

- The user opened Font via `FontDialog`. The trace includes a successful
  `autoExplorer` interaction, `auto-dialog-tab:13a0102:6a531402bdd80f45`,
  selecting Font between frames 7 and 8 during the manual session.
- The last pointer-up is at 23:09:28.467 UTC, at (1341, 351) on the Font
  dialog HWND 20578562. A `manual-click` marker follows, but there is no
  corresponding resulting frame or completed interaction. The bundle ends
  387 ms after that pointer-up.
- The health stream contains no terminal exception text. The exact exception
  thrown in this Word run is therefore unavailable; the closing-window race
  below is reproduced independently and matches the observed boundary.

This is separate from the earlier fatal native-callback investigation in
`2026-10-08-word-dialog-recording-crash-rca.md`. Existing popup-hook lifecycle
repairs and unrelated working-tree edits were preserved.

## Causes

### Automatic tab selection in manual recording

`CaptureClickAsync` calls `WaitForDialogCaptureAsync` when it observes a new
dialog. That shared routine unconditionally called
`CaptureDialogTabVariantsAsync`, which selects every unselected tab through
`TrySelectDialogTabAsync`, including physical clicks for native MSAA tabs.
The routine did not distinguish passive manual observation from active auto
exploration. This behavior exists in the initial `a478ce5` implementation.

### A transient HWND loss can end the recording

Post-click capture resolves a scoped HWND, reads point controls, then captures
the exact dialog. Close/Cancel can destroy the HWND between those operations.
Strict collector scope validation then throws `InvalidOperationException`;
other disappearance paths can throw native/window-resolution exceptions.
Previously this escaped `CaptureClickAsync` into the recording-phase exception
handler in `Program.cs`, which calls `session.Fail()` and returns a failed
phase. `Fail()` retains an existing recording as `partial`.

A deterministic synthetic Windows regression closes the owned dialog inside
its in-flight accessibility read, then invokes the real collector's scope
validation. With recovery disabled, the actual scope exception escapes. This
demonstrates the defect without claiming to have replayed the original Word
exception or modified a user document.

## Repair

- `WaitForDialogCaptureAsync` observes only the current page by default.
  Its `exploreTabs` flag defaults to false. The three automatic exploration
  callers explicitly opt in; manual recording never opts in.
- The manual click boundary catches window-resolution/scope failures only
  when the original clicked scope was a transient HWND that no longer belongs
  to the sealed process. It records `clicked-window-closed`, refreshes the
  selected application root, and falls back to a visual root frame if needed.
  The original input remains in the same active session.
- Collector identity checks are unchanged. Cancellation, selected/root-window
  loss, and failures while the clicked window remains valid still propagate.

## Verification

The existing native popup/dialog integration test now runs with and without
dialog tabs. It verifies exactly two stable reads for passive dialog capture,
no `auto-dialog-tab` interactions, recovery from dialog destruction, a captured
root frame, and acceptance of another frame in the same session.

Restoring the two prior behaviors while retaining the existing popup-hook
repair produces two failures: extra dialog reads (4 instead of 2) and the
actual collector scope exception. With the final repair and real collector
check, both cases pass as part of the full Windows suite.

Validation with SDK `10.0.401`:

- Windows suite: **523 passed, 0 failed** (includes both regression cases).
- Focused `RecorderWorkflowCommandTests` and `RecordingGraphInputTests`:
  **73 passed, 0 failed**. The full Core suite was not run for this change.
- Isolated CLI Debug build: **0 warnings, 0 errors**.
- `git diff --check`: passed.

The running `ui-atlas` process (PID 26480) locks the normal CLI output DLLs,
so the normal-output build and initial Core-test build could not copy their
outputs. Process termination was disabled. Both build and focused Core tests
succeeded using isolated output paths. The repaired executable is at
`%USERPROFILE%\.codex\tmp\ui-atlas-word-manual-build\Debug\net10.0-windows10.0.19041.0\ui-atlas.exe`.
The running recorder has not been restarted and still uses its loaded DLLs.

Subsequent Visual Studio workflow validation refreshed the normal Debug outputs
using Visual Studio 2026's installed MSBuild engine: the entire solution built
with zero warnings and zero errors. Executable projects now disable VS's fast
up-to-date bypass, ensuring the existing cleanup hook runs when VS builds them.
The cleanup regression also passed actual Build and Rebuild with a running copy
locking the tested output, while neighboring output copies remained running.
The separate development launcher and its shortcut were removed. These checks
used MSBuild and synthetic idle recorder processes; the VS F5 UI was not driven.

A live Word replay remains unperformed. No claim is made about unrun desktop
acceptance or hosted PR checks.
