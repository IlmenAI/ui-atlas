# Shared desktop launchers

Updated mapping ownership: see [one Atlas library](2026-10-19-shared-atlas-library.md).

UI Atlas and MapLock now host the same presentation source in `src/UiAtlas.Shell`.
Each solution has a project named `Launcher` marked `DefaultStartup="true"`.
UI Atlas produces `UiAtlas.exe`; the UI Maps application produces `MapLock.exe`.
Visual Studio may retain an older personal startup choice; select **Launcher → Set
as Startup Project** once in an existing workspace. New solution opens use the
checked-in default. No personal IDE state is committed.

## Products and ownership

| UI Atlas | MapLock |
| --- | --- |
| UI Maps and Diagnostics profiles | Existing Work, Maps, Integrations and Diagnostics profiles, including their availability states |
| Maps library is the initial page | Existing Home and saved mode/display preferences |
| Atlas recording catalog and local `.db.uimaps` authorities | Same Atlas recording catalog and workspaces; private settings services |
| Recordings page builds retained recordings and opens the existing editor | Same Atlas recording/build/editor workflow |
| Native read-only application snapshots, provider evidence and screenshots | Existing ambient and full Diagnostics executable |
| Recording profiles, map limits and startup preference in Settings | Existing full Settings plus shared Atlas mapping profiles, limits and library location |

The two launchers now use one Atlas catalog for UI Map recordings and workspaces.
Previous MapLock-only data remains separate legacy data until explicitly imported.
Atlas does not reference the MapLock executable or private local-services assembly.
MapLock compiles the canonical shared shell against its own Protocol and Recorder
versions in `UIMaps.SharedShell`; its adapter remains in `UIMaps.Application`.
The legacy `UIMaps.Application.exe` delegates to that same composition.

The UI Maps checkout expects the matching UI Atlas checkout alongside it, or
`UIAtlasFoundationRoot` pointing to that checkout. Keep both repository revisions
together for release builds. The retained shell copyright notice is in
`src/UiAtlas.Shell/LICENSE.txt` and `licenses/MapLock-shell-COPYRIGHT.txt`.

## Entry points

```text
UiAtlas.exe                                 desktop, Maps by default
UiAtlas.exe --workspace diagnostics          one-launch override
ui-atlas                                    command-line help
ui-atlas shell                              interactive command-line shell
ui-atlas app [--workspace maps|diagnostics]   open the desktop
ui-atlas settings                           recording/build settings window
```

Existing recording, map, open, build, import/export and validation commands retain
their behavior. Windows installation creates a UI Atlas shortcut that runs `app`.
MapLock's package Start script opens its Launcher. Both build and publish outputs
stage the runtime dependencies needed by their tools. MapLock stages diagnostics,
recorder, perception and localization under `services/`; Atlas stages its CLI,
native worker and editor under `tools/`.

Startup precedence in Atlas is explicit command override, then saved preference,
then Maps. Switching profiles does not rewrite the startup preference. Capture
profiles are separate from workspace profiles. Atlas profile overrides and map
limits also apply to its CLI and mapping started by MapLock; MapLock's other
recording/service settings remain in its own Settings pages.

Map builds validate limits before accepting output and retain the effective values
in each workspace generation's `build-settings.json`. Failed or cancelled builds
preserve the previous map. Reviews remain required before publication/readiness.

## Development checks

Build `src/Launcher/Launcher.csproj` in UI Atlas and
`uimaps-application/src/Launcher/Launcher.csproj` in UI Maps. Test the shared shell
with `tests/UiAtlas.Shell.Tests`, the MapLock adapter with
`uimaps-application/tests/UIMaps.Application.Tests`, and mapping behavior with
`tests/UiAtlas.Core.Mapping.Tests`. Live capture tests own desktop windows and should
run separately from manual UI checks.

## Initial launcher verification on 19 October 2026

This section records the first launcher extraction, before the shared-library
switch. Current mapping behavior and verification are in the linked shared-library notes.

Both root solutions built in Debug with zero warnings and errors. Both Launchers
also built and published in Release with their staged runtime dependencies.
Visual Studio F5 opened UI Atlas and MapLock from their respective Launcher
projects. Existing personal startup selections in these two open solutions were
set to Launcher as well; the checked-in solution defaults cover fresh workspaces.

The published applications were opened independently of Visual Studio. UI Atlas
showed only UI Maps and Diagnostics, with Maps as the default page and a working
Diagnostics command-line override. Its Settings exposed recording profiles and
map-build limits. MapLock retained its complete navigation, profiles, settings,
and availability states. Its Maps page loaded the existing Map service library
(4 applications and 29 recordings) without migrating or modifying it.

An isolated synthetic recording was built through Atlas's Recordings page into
a 38-node, 52-edge draft, then opened in the existing map editor. The workspace
retained its effective build settings. Standalone Diagnostics captured a native
File Explorer snapshot with 3 provider packets. Provider limitations remain visible
in the evidence; a captured snapshot is not a claim of complete accessibility
coverage. No existing recording was overwritten.

Focused automated checks passed:

| Area | Passed | Skipped |
| --- | ---: | ---: |
| MapLock application and shared-shell regression tests | 84 | 1 |
| Atlas launcher, navigation, preference and grouping tests | 3 | 0 |
| CLI entry points, settings and editor launch tests | 25 | 0 |
| Mapping tests excluding desktop-owning live tests | 60 | 0 |
| Recording-profile settings tests | 2 | 0 |
| **Total** | **174** | **1** |

The skipped MapLock test requires an optional live recording fixture. Desktop-owning
live tests were kept separate from UI verification. The build-process cleanup
script also passed its native/managed process, path-isolation and Build/Rebuild
checks. Direct CLI checks confirmed bare-command help, the explicit interactive
`shell`, GUI launch, and rejection of an unavailable workspace.

Full release ZIP validation remains blocked by two pre-existing checkout issues:
the repository boundary check reports the unreviewed
`docs/assets/2026-10-18-excel-expanded-map/chart-gallery-source.png`, and foundation
verification reports a hash mismatch in vendored
`UIMaps.Perception.Core/ObservationNormalizer.cs`. These files and the provenance
gate were left intact. Successful `dotnet publish` and desktop launch checks do
not imply that these full release checks passed.
