# AppMap five-mode parity follow-up

The 2026-10-15 09:49 recording shows the same saved Word map after the native
recorder migration. Its remaining rendering gaps are independent of capture
quality and do not require another recording or a map rebuild.

## Video findings

| Video time | Observation | Required behavior |
| --- | --- | --- |
| 00:24–00:38 | Main-window Controls shows geometry instead of individual control images | Show crops at the recorded control bounds |
| 00:38–00:45 | Overlay resembles the window screenshot | Place control crops over the recorded screenshot |
| 00:54–01:14 | Structure Overlay loses the structural layer | Show geometry over a translucent screenshot |
| 01:51–02:16 | Word popup has pixels but little provider structure | Preserve the missing native child data; do not infer controls from pixels |
| 02:18–02:39 | Font dialog repeats all three mode failures | Apply the same five-mode contract to dialogs |

Window and Structure already displayed their respective source types. The
video explicitly limits this follow-up to composition, including the exception
for Word's opaque popup.

## Root causes

1. `RawVariantPresentation.PolicyFor` tied inspection crops to verified capture
   stability. The old recording has no stability proof, so its real pixels and
   real provider bounds could not be inspected as crops.
2. `RenderAppMap` suppressed the entire control layer in both overlay modes
   for those observations. Structure Overlay therefore became a translucent
   screenshot with no structure.
3. The common Overlay policy requested no control crops, even for stable frames.
   It was a geometry-overlay mode instead of test5's control-images-over-scene
   mode.
4. Selecting a control replaced its image brush with a solid highlight fill.
   Selection should retain the crop and emphasize its boundary.

The reference behavior is test5's `UiKgAppMapWindow`: `UsesControlScreenshots`,
`HasSceneScreenshotUnderlay`, `RenderSurfaceFrameLayers`, and
`BuildControlElement`. These establish the composition contract; proprietary
workflow behavior is not imported.

## Fixes

| Mode | Window pixels | Control images | Control geometry |
| --- | --- | --- | --- |
| Window | Full opacity | None | None |
| Controls | Hidden | Crops from the same observation | Recorded bounds and structural context |
| Overlay | Full opacity | Same control crops | Recorded bounds |
| Structure | Hidden | None | Blueprint |
| Structure Overlay | Translucent | None | Blueprint |

Rendering eligibility is separate from `HasVerifiedAlignment`. An unverified
or changed capture keeps its warning and original acquisition status. Crops are
explicitly image regions at recorded bounds; rendering them does not establish
synchronized acquisition or change a Survey into a Snapshot.

Canonical bundle/frame references, exact window lookup, provider selection,
visibility filtering and occlusion masks remain in force. A crop cannot use
inherited pixels, extend beyond the available image, or include a known occluded
region. Missing provider controls stay missing. Selection retains image pixels.

The CLI, recorder, grid review, Perception, Map authority and private workflow
dispatch are unchanged by this presentation repair. Normal Visual Studio
Build/Rebuild updates the editor deployed with the CLI and MapLock integration.

## Verification

Regression cases exercise the actual WPF renderer with distinct parent, popup
and composite image colors, across stable, changed, unverified and legacy
observations using both legacy and UI Maps map backends. They assert crop pixels,
scene visibility/opacity, selection behavior and separate popup provenance.
Additional checks retain cross-frame and occlusion rejection.

The retained Word recording rendered 129 control crops in main-window frame 7
and 25 in Font-dialog frame 12, in both Controls and Overlay. Popup frame 4
retained three structural records and zero crops. All 15 surface/mode combinations
were rendered without changing the recording or its unverified alignment status.
The targeted suite passed 71 cases, the broader Core subset passed 496, and the
non-live mapping suite passed 39. These counts overlap. Foreground-dependent
and known-baseline exclusions are documented in the local qualification report.

The local qualification directory `.codex-out/video-gaps-20261015/` contains
the automatic transcript, timestamped video frames, test logs, actual-recording
renders and final qualification results. It is excluded from the public package.
