# Configurable capture size

UI mapping defaults to **256 MiB per captured observation**, including screenshots
and accessibility evidence. In either launcher, open **Settings > Recording
profiles**, choose the profile, enable **Customize this profile**, edit **Capture
size limit (MiB)**, and save. The supported range is **1–512 MiB**.

Both launchers use Atlas's shared profile settings. Explicitly saved overrides
remain intact. A recording already in progress retains its starting settings;
changes apply to new recordings. No saved overrides were present on this machine
when verified, so new recordings use 256 MiB.

## Implementation

- The shared recorder distinguishes the 256 MiB capture default from its 512 MiB
  supported capacity. The resolver no longer silently clamps custom values to
  256 MiB, and Atlas no longer declares a 128 MiB consumer ceiling.
- Perception, episode import, workspace package import, evidence preview, and
  recording readers accept the full supported range. Lowering the setting for
  future recordings does not invalidate previously saved larger captures.
- Recording archives previously imposed an additional 64 MiB entry limit and
  512 MiB aggregate staging/read limit. Entries now support 512 MiB; the aggregate
  recording quota is 4 GiB, matching the existing portable-workspace quota.
  Manifest hashing streams entries instead of allocating another full copy.
- MapLock Recorder/Perception service capabilities and IPC readers support 512 MiB
  captures, with 16 MiB of message-envelope headroom and corresponding frame-count
  limits. Diagnostics archive readers and the legacy capture default are aligned.
  Diagnostics retains its own existing profile-settings store.
- Oversized observations still fail explicitly with `RECORDER_CAPTURE_TOO_LARGE`.
  This change does not implement streaming acquisition or automatic capture splits.

## Verification

- 11 profile tests passed: default 256 MiB, persisted values of 1/128/256/384/512,
  no silent clamp, session settings frozen at recording start, invalid sizes rejected.
- 22 settings UI and archive-security tests passed, including editing to 384 MiB
  and restoring 256 MiB in the actual WPF Settings window.
- 64 mapping tests passed, excluding desktop-owning live mapping tests. A synthetic
  episode with over 257 MiB of retained protobuf data successfully passed recording
  validation, map building, reopening, direct episode import, workspace export,
  workspace import, and evidence hash preservation. This used incompressible test
  padding, not a live Excel capture. A separate acquisition test verifies that a
  small configured ceiling fails and increasing it permits the same evidence.
- 60 MapLock diagnostics tests passed, including actual named-pipe framing at 257
  and 528 chunks and rejection beyond the supported frame count.
- Both Debug Launcher builds succeeded with zero warnings/errors, and both opened
  responsive main windows. Their deployed recording assemblies independently
  resolved a 256 MiB default and each of 1/128/256/384/512 MiB without clamping.
  Isolated deployment builds use different source/PDB path mappings, so binary
  hashes are not expected to match; the deployed policy behavior was checked.

Logs and the deployed-assembly verification harness are under
`.codex-out/capture-size-*`. Both launcher windows were closed afterward at the
user's request.

The source-provenance audit still reports four pre-existing Perception hash
mismatches: `ObservationNormalizer.cs`, `WorldObjectBuilder.cs`,
`SurfaceSceneReconstructor.cs`, and `OfficeMsaaGeometry.cs`. Those unrelated
sources were not changed here. The three edited foundation files and new shared
capture-size constants have matching provenance entries.
