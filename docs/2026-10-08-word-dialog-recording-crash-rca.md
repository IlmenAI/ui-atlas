# Word dialog recording crash: RCA and repair

## Evidence

The supplied console log records an initial Word session, cancellation from the
toolbar, a restart in that same recorder process, and a fatal failure after the
first captured click in the restarted session. The runtime reports a callback on
a garbage-collected `NativeMethods.EnumWindowsProc` delegate and exit code
`0x80131623`. The active stack is:

`CaptureOwnedDialogAsync` -> `WaitForDialogCaptureAsync` ->
`CaptureDialogTabVariantsAsync` -> `TrySelectDialogTabAsync` ->
`TryActivateWindow` -> `PeekMessageW`.

This is a fatal unmanaged callback failure, rather than a recoverable screenshot
or UI Automation exception. The earlier `LimitReached` scan result is not the
termination reported in this log.

## Confirmed defect and causal assessment

`PopupWindowEventMonitor` installed an out-of-context WinEvent hook directly on
the caller's thread. `AdaptiveCaptureCoordinator.Start` is called by the async
recorder, and cancellation/finish invokes `DrainAsync` and `DisposeAsync` through
async continuations. There is no guarantee that teardown runs on the installing
thread. The old monitor called `UnhookWinEvent` on the teardown thread, ignored
its result, and cleared the hook handle. The coordinator then cleared its monitor
reference. This implementation dates to the initial `a478ce5` commit; it was not
introduced by the October 7 AppMap changes.

Windows requires hook removal on the installing thread. Removal on another
thread fails, leaving the native hook registered while the managed monitor and
callback can become collectible. The original implementation also provided no
dedicated message loop for the installing thread. A later `PeekMessageW` on that
thread can dispatch the retained event callback. This fits the cancel/restart
sequence and the message-pumping crash boundary in the supplied log.

A focused regression reproduced the thread-affinity defect before the repair:
installation and removal ran on different native thread IDs. The same test
passes after the repair. Microsoft documents both the
[same-thread removal requirement](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unhookwinevent)
and the
[event delivery, message loop, and callback lifetime requirements](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook).

The specific stale callback in the fatal log is not conclusively identified:
the runtime names `EnumWindowsProc`, whereas the persistent hook uses
`WinEventProc`. Native callback thunk reuse is a possible explanation, but it has
not been established from a crash dump. Synchronous window enumeration delegates
are rooted by the P/Invoke marshaler for the duration of their calls, as described
in Microsoft's
[callback documentation](https://learn.microsoft.com/en-us/dotnet/framework/interop/how-to-implement-callback-functions).
The persistent hook lifecycle is a demonstrated defect and the leading causal
explanation; the exact original Word crash has not been replayed end to end.

## Repair

- Give the popup monitor a dedicated background thread with a message queue and
  `GetMessageW` loop.
- Install, deliver callbacks, and remove the hook on that thread.
- Publish readiness only after the queue and hook exist; report startup failure.
- Stop through `WM_QUIT`, suppress callbacks during teardown, and join the thread
  before releasing the monitor. A shutdown timeout leaves the running thread
  holding its callback rather than discarding a live native callback.
- Keep the callback alive through native teardown. Thread termination also
  removes hooks if explicit removal fails.
- Make disposal repeatable and prevent restarting a stopped monitor. Post quit
  only once while the worker owns its thread ID, avoiding reused thread IDs.

The hook still observes only the selected target process and skips the recorder's
own process. Dialog capture and tab-selection behavior are unchanged.

## Validation

`PopupWindowEventMonitorTests` verifies cross-thread disposal and exactly-once
hook removal, startup failure, rejection of start after disposal, and eight
native event-hook start/stop cycles with forced garbage collection. The native
test uses a tiny off-screen synthetic window, confirms callback delivery and
successful hook removal on the installing thread, and exercises `PeekMessageW`
and window enumeration between sessions. It does not modify a user document.

Validation results on October 8, 2026, with SDK `10.0.401`:

- The cross-thread teardown regression failed before the repair and passes after
  it; all four new monitor tests pass.
- Windows suite on the final implementation: **522 passed, 0 failed**.
- Core suite: **390 passed, 4 failed**. Failures are the inspector containment
  assertion in `RealWindowOverlayRefreshSelectionCaptureAndTeardown` and three
  cases of `ExploreOpaqueGridTraversesAndRestoresWithoutReadingText` timing out
  while awaiting mapper state. These tests do not instantiate the popup monitor.
- All four failures reproduced with the same assertions in an archived `HEAD`
  baseline (`64ba7f7`) without this repair, using the same SDK selection. They are
  pre-existing failures on this desktop, not regressions introduced by the hook
  lifecycle change.
- The CLI Debug build succeeded with **0 warnings and 0 errors**, refreshing the
  executable and recording DLL in the exact Debug output directory named by the
  crash log. Build process termination was explicitly disabled.
- `git diff --check` passed. Existing edits to `README.md`, `docs/release.md`, and
  `global.json` were preserved.

A live Word cancel/restart/dialog recording remains a separate manual
qualification boundary. The original fatal Word process was not reproduced;
the native regression qualifies callback delivery and lifetime in a synthetic
Windows target. No full-suite green claim is made.
