# RCA: different Excel map results from the two launchers

Investigated on 2026-10-19, following the [sequential launcher smoke test](2026-10-19-launcher-excel-smoke-test.md).

**The two runs used the same recording policy, capture implementation, target application, and map-building settings. Their saved input evidence differed because additional provider attempts timed out during the MapLock run. The shared recorder makes these timing differences severe: it kills the worker and loses its entire unreturned tree, then makes only one attempt.**

The smaller map is already explained by the recorded capture failures, before catalog loading or map building. A four-way rebuild and live worker comparisons rule out a different map-builder configuration as the cause of the reported control-count difference. The precise transient condition that slowed the two historical MapLock ribbon captures was not logged; CPU scheduling, worker startup delay, and time waiting on Excel/COM cannot be distinguished retrospectively.

No application source, deployed binaries, or saved settings were changed. Diagnostic programs and isolated rebuilds were created under `.codex-out/excel-launcher-rca`. The live checks only switched ribbons and opened/cancelled Format Cells; Excel was returned to Home and its original window size.

## 1. Settings and code comparison

The authoritative comparison comes from decoding every retained `raw/native/frame-*.episode.pb`, rather than assuming today's settings were also used earlier. All ten frames contain the same effective policy after excluding resolution timestamps and session/window identities.

| Setting | Both recorded runs |
| --- | --- |
| Recording profile | `mapping-checkpoint`, version `1.0.0` |
| Custom capture limits requested | No |
| UIA per-window deadline | 3,000 ms |
| MSAA per-window deadline | 3,000 ms |
| Native HWND worker deadline | 750 ms, derived as one quarter of UIA's deadline |
| Overall episode budget | 20,000 ms |
| Accessibility aggregate budget | 19,000 ms |
| Attempts | 1; no retry after the first timeout |
| Maximum windows | 64 |
| Nodes per window / episode | 10,000 / 50,000 |
| Maximum depth | 64 |
| Global / per-process concurrency | 3 / 1 |
| Maximum episode bytes | 128 MiB |
| Capture semantics | Snapshot |
| Visual-control detection / OCR request flags | Enabled in both |

Both recorders read `%LOCALAPPDATA%\UiAtlas\recording-profiles.json`. That override file is absent, consistent with the retained default-profile requests. Both builders read `%LOCALAPPDATA%\UiAtlas\map-build-settings.json`, currently **1,000,000 nodes and 1,000,000 edges**, last written before the tests. Build limits reject an oversized graph; they do not silently truncate it. The larger rebuilt graph has only 4,008 total nodes and 5,577 edges.

Both runs targeted the same Excel process instance, PID 31092, root HWND 200060, started `2026-10-17T22:40:09.1145089Z`, at 192 DPI. All ten retained observations report stable before/after pixels and topology. There is no evidence of a target-process switch, different zoom/DPI policy, or node/depth-limit exhaustion causing the divergence.

The shell's **UI Maps / Diagnostics** selector is an experience/navigation profile, distinct from the recorder's capture profile. Both capability launchers call the shared `AtlasCatalogClient.RecordAsync`. The recording command forwards target identity, not a separate MapLock capture profile. The shell sets a `RecordingDiagnosticsEnabled` field, but the shared Atlas command does not consume it. Selecting Diagnostics alone does not start an ambient diagnostic capture; the explicit Explore action launches that capability. No additional launcher-started provider process was present during the controlled comparison. This is not historical proof that the entire machine was idle during the earlier recordings.

The DLL file hashes differ between the separately compiled deployments. Comparing method IL, referenced string literals, embedded resources, and assembly references found **no capture/build implementation difference** in the examined assemblies:

- `UiAtlas.Core.Recording.Windows`: 5,417 method bodies.
- `UiAtlas.Core.Recording`: 349.
- `UIMaps.Recorder.Windows`: 854.
- `UIMaps.Recorder.ProviderProbe`: 25.
- `UiAtlas.Core.Build`: 860.
- `UiAtlas.Core.Mapping.Atlas`: 421.
- `UIMaps.Protocol`: 9,618.
- `ui-atlas`: 3,224; three generated regex type names differ, while their IL and literals match too.

Recorder/protocol copies inside each deployment's worker directory also match that deployment's root copies. Runtime configuration files match. Different binary hashes alone were therefore misleading evidence of a potential version difference.

## 2. The exact point where the recordings diverged

Run A is `20261019-130737-excel-e84d903d` from UI Atlas. Run B is `20261019-131419-excel-afa718e8` from MapLock.

| Frame | Excel state | Run A: UIA result | Run B: UIA result |
| --- | --- | --- | --- |
| 1 | Home | 387 nodes, 1.872 s | 387 nodes, 1.594 s |
| 2 | Insert | 345 nodes, 2.392 s | **Timeout at 3.053 s; zero returned nodes** |
| 3 | Home again | 387 nodes, 2.167 s | **Timeout at 3.030 s; zero returned nodes** |
| 4 | Format Cells and disabled owner | Both UIA queries time out | Both UIA queries time out |
| 5 | Home after Cancel | 387 nodes, 1.796 s | 387 nodes, 2.753 s |

Run B's frame 2 also loses the native-HWND attempt to its 750 ms deadline. Consequently, the projected raw control counts in that frame are **367 versus 0**. Frame 3 contains **409 versus 22**, with Run B retaining only native HWND controls. MSAA timed out for every queried window in both historical recordings, so it could not fill these gaps.

The resulting 784 versus 457 semantic controls are unions/deduplications across observations; the 327-control difference is not expected to equal one provider's raw node count. It is the result of building from different retained evidence.

### Controlled cross-build

Each original bundle was copied into two new, isolated data roots and built using each deployment's existing CLI. The user's shared catalog and original map authority were not rebuilt or modified.

| Identical input bundle | Atlas deployment | MapLock deployment |
| --- | --- | --- |
| Run A | 784 semantic controls; 4,008 nodes / 5,577 edges; 3/4 interactions | Exactly the same counts and quality |
| Run B | 457 semantic controls; 2,200 nodes / 3,051 edges; 1/4 interactions | Exactly the same counts and quality |

The results follow the recording, not the deployment doing the build. These are equal counts and quality reports, not byte-identical maps: fresh builds allocate new identities and report different hashes.

## 3. Why the shared recorder loses this evidence

### A hard deadline discards the entire worker response

`ProviderProcessRunner` starts a new process and times its execution through response/exit. On timeout it terminates the worker. `ProviderProbe` emits its protobuf result only after the provider traversal returns; it sends no intermediate tree. The parent therefore records an empty timed-out packet even if the worker already read useful nodes.

The mapping profile allows one attempt. A transient delay crossing three seconds thus produces zero UIA evidence rather than a partial tree or a successful retry. The native worker has an even shorter 750 ms budget that includes worker execution/startup overhead. Its failure timestamps are currently created after failure, so its recorded zero-millisecond attempt duration does not describe the actual wait.

### Full sibling enumeration is too expensive for this Excel dialog

The worker uses `TreeWalker.RawViewWalker`, calls `GetFirstChild`, then walks every sibling with `GetNextSibling`, collecting the complete child list before proceeding. It performs this enumeration before applying the depth cutoff. There is no cooperative traversal deadline or streaming checkpoint inside this loop.

An instrumented copy of the same worker source, kept outside product outputs, showed:

- About **4.070 seconds** enumerating the Format Cells root's 32 immediate children.
- About **16.788 seconds** enumerating the font list's 588 children.
- About **21.454 seconds** for the traced UIA traversal overall.

This is measurable traversal work, not evidence that a single call hangs forever. The three-second deadline can expire before the root's children have even been fully enumerated. Raising it to twelve seconds still does not finish the font-list traversal.

The unmodified deployed workers independently confirmed this result: with a diagnostic-only 30-second allowance, MapLock's worker returned **651 nodes in 21.933 seconds** and Atlas's returned **651 nodes in 22.867 seconds**. No node or depth limit was reached. No saved profile was changed to perform these checks.

### MSAA also exceeds the default budget on the ribbon

On a stable Insert ribbon, both unmodified workers timed out with the normal three-second allowance. With a twelve-second test allowance, both returned **2,923 MSAA nodes in about 4.0 seconds**. Home returned 3,121 MSAA nodes in 3.9–4.2 seconds.

Format Cells behaved differently: its MSAA query returned 3,570 raw nodes in about 1.4 seconds in both deployments during the later controlled tests, although it timed out in both original recordings. Thus the original dialog MSAA failure is not a permanently unavailable provider or a consistently reproducible three-second overrun. Its historical transient cause remains unobserved. The full response is available under favorable runtime conditions; the current all-or-nothing deadline makes adverse conditions lose it completely.

## 4. Why interaction counts also changed

The manual recorder resolves a click's source control from the preceding captured frame. Its native capture path replaces the latest observation with the new capture, even when coverage is partial. `ResolveManualInteractionOutcome` then labels an interaction Failed whenever the source control is absent, even when a result frame exists.

| Click | Run A | Run B | Explanation |
| --- | --- | --- | --- |
| Insert | Succeeded | Succeeded | Both initial Home frames contain its source control |
| Home | Succeeded | Failed / unknown | Run B's preceding Insert frame has no controls |
| Open Format Cells | Succeeded | Failed / unknown | Run B's preceding Home frame has only native HWND controls |
| Cancel | Failed / unknown | Failed / unknown | Both dialog frames lack the Cancel control |

**Every one of these interactions has a saved result-frame reference, including all Failed interactions.** The existing quality label “without a confirmed result screen” conflates missing source grounding, failed/unobserved outcomes, and missing result frames. It does not mean the Excel action failed or that its screenshot is absent.

## 5. Controlled comparison and remaining uncertainty

There were 24 sequential probes using the unmodified deployed workers, plus the separate instrumented trace. No concurrent probes were intentionally run against Excel. Home was tested in Atlas–MapLock–MapLock–Atlas order, Insert in MapLock–Atlas–Atlas–MapLock order.

| Stable target / provider | Atlas | MapLock |
| --- | --- | --- |
| Home UIA, 3-second budget | 387 nodes, 1.44 s on both attempts | 387 nodes, 1.42–1.43 s |
| Insert UIA, 3-second budget | 345 nodes, 1.08–1.32 s | 345 nodes, 1.19–1.29 s |
| Insert MSAA, 3-second budget | Timeout | Timeout |
| Insert MSAA, 12-second budget | 2,923 nodes, 4.03 s | 2,923 nodes, 4.06 s |
| Format Cells UIA, 3 / 12 seconds | Timeout / timeout | Timeout / timeout |
| Format Cells UIA, 30 seconds | 651 nodes, 22.87 s | 651 nodes, 21.93 s |
| Format Cells MSAA, 3 seconds | 3,570 nodes, 1.41 s | 3,570 nodes, 1.41 s |

These tests demonstrate shared behavior and do not reproduce an inherent MapLock slowdown. They do not reconstruct the exact machine load at the earlier failure. The retained records lack worker-ready, per-call, partial-progress, and host-contention timing, so identifying a particular OS/COM delay or background process as the historical trigger would be speculation.

The confirmed product-level causal chain is:

```text
Variable/expensive accessibility traversal
    -> fixed short deadline, one attempt
    -> worker killed before emitting its complete response
    -> missing control trees in retained recording
    -> fewer semantic controls in either builder
    -> subsequent clicks lose source grounding and are labelled Failed
```

Catalog caching is downstream of this chain and cannot restore evidence that was never retained.

## 6. Fix direction, not implemented in this RCA

1. **Preserve progress.** Add bounded incremental worker responses/checkpoints with provenance and explicit coverage. Keep a terminal hard timeout for unresponsive providers, while retaining already emitted evidence. Avoid enumerating a large entire sibling list before committing any child evidence; enforce traversal budgets while collecting children.
2. **Make mapping capture budgets and recovery coherent.** Separate worker startup allowance, per-provider work, and aggregate episode limits; add a bounded retry/recovery strategy for transient timeout. A 30-second child allowance cannot help while the episode still expires at 20 seconds. Use the successful provider evidence without erasing the failed provider's status. Recheck target identity/stability on retries.
3. **Handle large Office child lists efficiently.** Investigate batched/cached enumeration and capture broad dialog controls before exhaustive font-list details. Do not fabricate controls or automatically traverse dialog tabs during a manual recording.
4. **Separate action outcome from grounding completeness.** Retain the click and existing result-frame linkage when source identification is unknown. Report that source evidence is incomplete rather than claiming a failed Excel action or missing result image.
5. **Add diagnostics and acceptance tests.** Record worker-ready time, traversal/serialization duration, progress count, terminal reason, and retry history. Test partial-progress preservation, delayed worker startup, a large sibling list, transient failure followed by recovery, and identical-recording parity through both deployments. Repeat the two launcher UI workflows afterward.

Simply increasing the map node/edge limit will not fix this. Increasing timeouts alone is a workaround with substantial latency and still leaves evidence loss at the next overrun.

## Source locations and retained evidence

Relevant source locations in this checkout:

- `src/UiAtlas.Core.Recording.Windows/RecordingProfileSettings.cs:12` and `NativeRecorderCapture.cs:16`: shared settings and request resolution.
- `src/UiAtlas.Shell/Catalog/AtlasCatalogClient.cs:81`: common recording launch arguments.
- `vendor/uimaps/uimaps-recorder/profiles/capture-profiles.v1.json:114`: mapping checkpoint defaults.
- `vendor/uimaps/uimaps-recorder/src/UIMaps.Recorder.Windows/ProviderIsolation.cs:49`: worker deadline; lines 298, 349, and 486 cover attempts, MSAA, and native worker allowance.
- `vendor/uimaps/uimaps-recorder/src/UIMaps.Recorder.ProviderProbe/Program.cs:33`: final-only output; line 109 begins child enumeration.
- `src/UiAtlas.Core.Recording.Windows/AdaptiveCaptureCoordinator.cs:401`: source lookup from previous observation in the native capture path.
- `src/UiAtlas.Core.Cli/Program.cs:3963`: Failed classification when source control is missing.
- `src/UiAtlas.Core.Cli/MapQualityInspector.cs:137`: combined “without confirmed result” count.
- `src/UiAtlas.Core.Build/MapBuildSettings.cs:14`: fail rather than truncate when map limits are exceeded.

Evidence directory: [assets/2026-10-19-excel-capture-rca](assets/2026-10-19-excel-capture-rca).

- [Common retained policy](assets/2026-10-19-excel-capture-rca/common-effective-policy.json)
- [Original frame comparison](assets/2026-10-19-excel-capture-rca/frames.json) and [provider attempts](assets/2026-10-19-excel-capture-rca/provider-attempts.csv)
- [Original interaction linkage](assets/2026-10-19-excel-capture-rca/interactions.json)
- [Four-way rebuild results](assets/2026-10-19-excel-capture-rca/cross-rebuild.json)
- [Method-body comparison](assets/2026-10-19-excel-capture-rca/method-body-comparison.json) and [deployment timestamps](assets/2026-10-19-excel-capture-rca/deployment-comparison.json)
- [Home probes](assets/2026-10-19-excel-capture-rca/home-probe.jsonl), [Insert probes](assets/2026-10-19-excel-capture-rca/insert-probe.jsonl), and [dialog probes](assets/2026-10-19-excel-capture-rca/dialog-probe.jsonl)
- [Instrumented traversal delays](assets/2026-10-19-excel-capture-rca/dialog-trace-gaps.json)

The diagnostic harness, decoded episode metadata, full traversal trace, and temporary rebuilt maps remain under `.codex-out/excel-launcher-rca`. This RCA does not claim that the unrelated editor secondary-display issue or Archive/Delete UX gaps have been fixed.
