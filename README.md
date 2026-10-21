# UiAtlas

Open `UiAtlas.Core.slnx` and run **Launcher** for the desktop. It opens the shared
shell with UI Maps and Diagnostics, plus recording and map-build settings. The
terminal command `ui-atlas` shows help; `ui-atlas shell` starts the interactive CLI,
and `ui-atlas app [--workspace maps|diagnostics]` opens the desktop.
See [shared launchers](docs/2026-10-19-shared-launchers.md) for composition and build details.
UI Atlas and MapLock now share the [same Atlas recordings and map library](docs/2026-10-19-shared-atlas-library.md).

UI Atlas provides an attended UI mapping recorder, command-line tool, and UI
Knowledge Graph Editor. New recordings use the UI Maps native recorder; new maps
use UI Maps Perception and Map Core. Raw Data Streams, Raw World, Semantic World
and Predicted remain in the editor, with original sensor evidence retained
alongside reviewable map drafts. The public
checkout builds independently; the MapLock-specific services, workflow recorder,
workflow authoring, runtime, and product integrations remain proprietary.
See the [migration rollout](docs/2026-10-13-uimaps-foundation-rollout.md) for
architecture, commands, compatibility, and local qualification.

Open **Settings** from the recorder's **More** menu or the map editor toolbar.
**Recording profiles** lists the shared Recorder profiles, including Atlas's
Mapping checkpoint default. Choose the profile for new recordings, customize
its timeouts and coverage limits, and save; an active recording keeps the
settings it started with. Restore defaults is staged until saved. Deep survey
keeps unlimited duration/window/node/depth coverage while exposing worker safeguards.
The recorder's existing table-reading settings remain available from this window.

**UI Map building** sets independent node and edge limits for the next build or
rebuild (100,000 each by default, up to 2,000,000 each). A build exceeding either
limit reports the required counts and keeps the previous map and source recordings.
Existing maps remain readable when local build limits are lowered. Larger maps
need more memory; older Atlas versions may reject maps above 100,000 nodes or edges.
Settings are saved locally under `%LOCALAPPDATA%\UiAtlas` in
`recording-profiles.json` and `map-build-settings.json`; MapLock settings are not modified.

<p align="center"><a href="https://gcore.jsdelivr.net/gh/AI-Successors/ui-atlas@4d1a316/docs/media/ui-atlas-demo.mp4"><strong>▶ Watch the UiAtlas product demo with English subtitles</strong></a></p>

<a href="https://www.producthunt.com/products/ui-atlas?embed=true&amp;utm_source=badge-featured&amp;utm_medium=badge&amp;utm_campaign=badge-ui-atlas" target="_blank" rel="noopener noreferrer"><img alt="UI-Atlas - Map desktop interfaces for computer-use agents | Product Hunt" width="250" height="54" src="https://api.producthunt.com/widgets/embed-image/v1/featured.svg?post_id=1245359&amp;theme=light&amp;t=1788941722658"></a>

UiAtlas is an offline-first Windows toolkit for manually recording one selected desktop application and its owned popups, preserving Raw Data Streams, building and reviewing UI Maps knowledge, inspecting and diffing graph projections, and sharing maps with their evidence.

<p align="center">
  <a href="https://gcore.jsdelivr.net/gh/AI-Successors/ui-atlas@6988b62/docs/media/ui-atlas.mp4">
    <img src="docs/media/ui-atlas-preview.jpg" alt="Watch the UiAtlas product introduction" width="960">
  </a>
</p>

<p align="center"><a href="https://gcore.jsdelivr.net/gh/AI-Successors/ui-atlas@6988b62/docs/media/ui-atlas.mp4"><strong>▶ Watch the UiAtlas product introduction</strong></a></p>

The supported scope includes Windows 10 version 2004 or later and Windows 11, attended capture, immutable recording bundles, UI Maps perception, map review, immutable revision publication, SQLite/JSON interchange, and the WPF editor. Automatic mapping exploration remains an explicit recorder mode. Publishing a map creates knowledge; it does not authorize application actions or create a workflow.

## Product demo

<p align="center">
  <a href="https://gcore.jsdelivr.net/gh/AI-Successors/ui-atlas@4d1a316/docs/media/ui-atlas-demo.mp4">
    <img src="docs/media/ui-atlas-demo-preview.jpg" alt="Watch the UiAtlas product demo with English subtitles" width="960">
  </a>
</p>

## Introduction to the problem
AI agents get lost in unfamiliar software, just like people get lost in a new city. We’ve just open-sourced UI Atlas, a tool that maps Windows apps—their screens, buttons, and what happens when you click. 
Learn more [here](https://x.com/danielko/status/2097594696193413448)

## Prerequisites

- Windows 10 2004+ or Windows 11
- .NET 10 Windows Desktop Runtime for the release ZIP
- .NET SDK 10.0.200 or a newer .NET 10.0 SDK feature band for source builds
- A desktop session at the same integrity level as the target application

## Install the release ZIP

Extract the ZIP and run one command from the extracted `ui-atlas-core` folder. Installation is per-user and does not require administrator rights:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

This copies the portable application to `%LOCALAPPDATA%\Programs\UiAtlas` and creates a **UiAtlas Recorder** Start-menu shortcut. The extracted folder also remains portable: run `ui-atlas.cmd` directly if installation is not wanted.

UiAtlas does not download components, upload recordings, or require an account. If the required Windows Desktop Runtime is missing, the installer stops with an explicit message instead of leaving a partial installation.

Restore and build using the committed lock files:

```powershell
dotnet restore UiAtlas.Core.slnx --locked-mode
dotnet build UiAtlas.Core.slnx -c Release --no-restore
dotnet test UiAtlas.Core.slnx -c Release --no-build --no-restore
```

Windows builds, rebuilds, and cleans automatically terminate running processes that use the executable project's exact output paths, including the recorder's UIA workers and desktop explorer. This also handles `dotnet`-hosted DLLs and applies in Visual Studio. Finish active recordings before building. Other output configurations, checkouts, and installed copies are left running; design-time builds skip cleanup. To opt out, pass `-p:StopRunningUiAtlasProcesses=false`.

In Visual Studio 2026, set `UiAtlas.Core.Cli` as the startup project, select Debug or Release, then Build/Rebuild and run with F5 or Ctrl+F5. Executable projects disable Visual Studio's fast up-to-date shortcut so MSBuild runs the output-cleanup hook and performs its incremental build checks. The launched recorder comes from that project's current `bin\<Configuration>\net10.0-windows10.0.19041.0` output. Finish active recordings before building; replacing the output stops running copies from that same directory.

Each recorder Build/Rebuild embeds the next local build number, displayed as `UI Atlas Core 2.0.N` in the startup banner and command help. Debug and Release share the counter in `.build/recorder-build-number.txt`, which is ignored by Git and survives Clean/Rebuild. Design-time checks and `--no-build` launches do not increment it. Numbers are allocated before compilation, so failed build attempts can leave gaps. `tools/Test-RecorderBuildNumber.ps1` verifies the emitted banners, repeated builds, Rebuild, both configurations, and fixed-number determinism. For reproducible packaging, `tools/Package.ps1` pins `UiAtlasBuildNumber=0`; build pipelines can supply `-p:UiAtlasBuildNumber=<number>` explicitly.

When all locked packages are already available in the local NuGet cache and the machine is intentionally offline, add `-p:NuGetAudit=false` to the restore command. Run the normal restore or the vulnerability command in `docs/release.md` while online to refresh advisory data before release.

## Record an application

```powershell
dotnet run --project src/UiAtlas.Core.Cli -c Release -- list windows
dotnet run --project src/UiAtlas.Core.Cli -c Release -- recording start 0x123456
dotnet run --project src/UiAtlas.Core.Cli -c Release -- list recordings
dotnet run --project src/UiAtlas.Core.Cli -c Release -- list maps
```

`recording start` asks for explicit consent and takes a native baseline Survey. Each attended click is stored immediately, then UiAtlas records the resulting state, an owned popup/dialog, or an explicit failed/unobserved interaction. UI Maps' isolated providers acquire pixels, window topology and accessibility; Perception derives visual/OCR observations. Original native packets retain coverage, timing and provider attempts. The Atlas toolbar shows elapsed time and capture/build progress. Finish seals evidence and builds the map; Cancel ends the current recording. Automatic exploration remains an explicit mode.

To capture the mapper's visible boundaries, control highlights, click indicators, and toolbar, take a desktop or region screenshot (for example, with Windows Snipping Tool). These overlays are included in user screenshots and screen sharing. UiAtlas temporarily hides them while capturing its own recording evidence so they do not become part of the mapped application.

Artifacts are kept under `%LOCALAPPDATA%\UiAtlas\Core`: immutable `.mlrec` recordings, SQLite maps, JSON exports, and a recoverable catalog trash. The recording and generated map share the displayed ID. Deletion commands move an artifact to that trash:

```powershell
dotnet run --project src/UiAtlas.Core.Cli -c Release -- recording validate <recording-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map build <recording-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map validate <map-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map quality <map-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map inspect <map-id> world streams
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map inspect <map-id> world raw
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map inspect <map-id> world semantic
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map open <map-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map export json <map-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map delete <map-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- recording delete <recording-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map delete all
dotnet run --project src/UiAtlas.Core.Cli -c Release -- recording delete all
```

`map quality` reports deduplicated screens, semantic controls, structured table cells, duplicate screenshots, partial scans, and clicks without a confirmed result screen. Add `--strict` in CI to return a failing exit code when review is required.

`export map` defaults to the identity-bearing, human-readable JSON map. Every world contains `windows`; every window contains `variants`; and every variant contains its observed `controls`. Lineage arrays connect windows, variants, and controls back to the preceding world. Other supported formats are explicit:

```powershell
ui-atlas export json validate C:\exports\sample.json
ui-atlas export json delete "$env:LOCALAPPDATA\UiAtlas\Core\exports\sample.json"
ui-atlas export map <map-id>
ui-atlas export map <map-id> format=json
ui-atlas export map <map-id> format=ui-atlas_flat
ui-atlas export map <map-id> format=sqlite
ui-atlas export map validate C:\exports\sample-map.json
ui-atlas export map delete "$env:LOCALAPPDATA\UiAtlas\Core\exports\sample-map.json"
```

`format=json`, `format=ui-atlas_flat`, and `format=sqlite` export compatibility projections. All three retain identity-bearing UI information and require confirmation; unattended scripts can supply `--acknowledge-sensitive-identities`. `map export json` remains the separate privacy-safe graph interchange command. For a complete editable map, use **Export → Complete map workspace** or `map export-workspace`; a SQLite projection alone does not contain UI Maps review authority. Catalog deletion moves both the projection and its authority to recoverable trash.

## Review and share a UI Maps map

Keep `sample.db` together with its adjacent `sample.db.uimaps` directory. Open the
map and select **Review map** to inspect entities, observed interactions, and
source evidence; accept or reject candidates, correct names and parents, validate,
then publish an immutable revision. Undo/redo is scoped to the open review window.

```powershell
ui-atlas map drafts sample.db
ui-atlas map validate-drafts sample.db
ui-atlas map review sample.db <draft-ref> accept <entity-ref> --etag <etag> --reviewer <name>
ui-atlas map publish sample.db --reviewer <name>
ui-atlas map export-revision sample.db <revision-ref> --out application.uimap
ui-atlas map export-workspace sample.db --out application.atlas-map --allow-sensitive-evidence
ui-atlas map import-workspace application.atlas-map --out restored --allow-sensitive-evidence
ui-atlas map convert-recording existing.mlrec --out new-map.db
ui-atlas map import-episode episode.pb --window <window-evidence-id> --output native-import --allow-sensitive-evidence
ui-atlas open native-import
```

Review commands require a current draft etag. Validation returns exit code 4 when
review or evidence blocks publication. Revision export excludes pixels and raw
provider packets but retains labels and source references. Workspace export retains
all evidence. Conversion leaves the original recording unchanged. Existing Atlas
SQLite/JSON maps remain readable; old curation stays with the legacy map, while
new UI Maps identities require their own review.

## Interactive command shell

The release ZIP includes `ui-atlas.cmd`. Run it without arguments to enter a persistent, DISKPART-style command shell:

```text
UiAtlas Core 2.0.0
Type HELP for a list of commands, or EXIT to leave.

UI-ATLAS> HELP
UI-ATLAS> LIST
UI-ATLAS> LIST WINDOWS
UI-ATLAS> HELP MAP
UI-ATLAS> MAP INSPECT <map-id> WORLD RAW
UI-ATLAS> EXIT
```

Command names are case-insensitive. `HELP`, `HELP LIST`, `HELP RECORDING`, `HELP MAP`, and `HELP EXPORT` show aligned command summaries; entering a command family without a subcommand shows the same family help. Every shell command also works as a one-shot command, for example `ui-atlas list windows` or `ui-atlas recording start 0x123456`.

Direct-path commands remain available for scripting and testing:

```powershell
dotnet run --project src/UiAtlas.Core.Cli -c Release -- record --hwnd 0x123456 --out sample.mlrec
dotnet run --project src/UiAtlas.Core.Cli -c Release -- build sample.mlrec --out sample.db
dotnet run --project src/UiAtlas.Core.Cli -c Release -- validate sample.db
dotnet run --project src/UiAtlas.Core.Cli -c Release -- inspect sample.db --query button
dotnet run --project src/UiAtlas.Core.Cli -c Release -- export sample.db --out sample.safe.json
dotnet run --project src/UiAtlas.Core.Cli -c Release -- export-ui-atlas sample.db --out ui_knowledge_graph_vnext.json --project-id my-stable-project --acknowledge-sensitive-identities
dotnet run --project src/UiAtlas.Core.Cli -c Release -- validate-ui-atlas-export ui_knowledge_graph_vnext.json
dotnet run --project src/UiAtlas.Core.Cli -c Release -- diff old.db sample.db
dotnet run --project src/UiAtlas.Core.Cli -c Release -- open sample.db
dotnet run --project samples/UiAtlas.Core.Consumer -c Release -- sample.safe.json
dotnet run --project samples/UiAtlas.Core.Consumer -c Release -- sample.db --query "Save" --json
```

The second sample is the integration seam for an autotest or AI agent: it returns semantic control IDs, stable selectors, supported actions, evidence counts, and observed destination states without giving the sample permission to click the application.

The editor opens the current UI Maps authority and projects three layers: original Raw Data Streams, Perception's Raw World, and the reviewed Semantic World. Hierarchy, topology, selection, filtering, screenshot crops and overlays remain available. **Review map** exposes the actual Map entities, variants, observed interactions, review decisions and publication state. A graph-only export opens for inspection; import the complete workspace to edit its authority.

**Data & parameter review → Parameters / Data grids** reviews variable names and data regions in the same KG editor. **Find parameters** discovers local candidates; **Accept**, **Deny**, and **Need evidence** save the author's decision with supporting documentation. Grids default to fully variable contents; **According to schema** reuses reviewed mapping columns, including duplicate or blank labels. These rules are authoring knowledge and do not authorize execution or change workflow target resolution. See the [Phase 4.1.1 product specification](docs/Phase%204.1.1%20-%20UI%20KG%20Editor%20Parametrization%20Support%20-%20Product%20Spec.md) and [implementation specification](docs/Phase%204.1.1%20-%20UI%20KG%20Editor%20Parametrization%20Support%20-%20Implementation%20Spec.md).

Legacy maps retain their existing trace, route, curation and evidence views. Those graph formats cannot represent every UI Maps review or interaction field. New observed interactions are inspected in **Review map → Observed interactions** and retained in revision/workspace exports. `map open <map-id>` attaches catalog evidence automatically; UI Maps workspaces also resolve their retained recordings. Safe exports omit screenshot linkage.

For a direct local launch, the desktop executable also accepts `graph.db --evidence recording.mlrec`.

For a capture-free deterministic smoke test, replace the record command with:

```powershell
dotnet run --project src/UiAtlas.Core.Cli -c Release -- synthetic-record --out sample.mlrec
```

## Privacy warning

For table schema extraction, Recorder **More → Settings** (also **Header reading settings** in schema review) offers Local OCR or Azure OpenAI **GPT-5.6 Sol · Light**. Azure requires your resource endpoint, deployment name, and API key. **Test connection** checks a synthetic header; **Save settings** applies the provider to the next **Extract schema** action. Schema extraction sends selected header-cell crops, keys are stored in Windows Credential Manager, and names remain editable before saving. Local OCR is the default.

The release contains the mapping recorder, CLI, and editor. Historical MCP,
customer-data migration, and consumer examples remain in repository history/source
for compatibility; their separate executables are excluded from this release.

Recording can retain pixels, window titles, accessibility labels, window handles, process identity, and input timing. Printable key identities and literal typed text are suppressed by default. The safe JSON export removes screenshot references, application-provided labels/properties, raw stable keys, source linkage, coordinates, build time, and canonical IDs. Generic kinds and topology can still be sensitive. Review every artifact before sharing it. Full-evidence and main UiAtlas compatibility exports each require explicit acknowledgement.

See [architecture](docs/architecture.md), [recording format](docs/recording-bundle-v1.md), [UI KG v4 format](docs/uikg-v4.md), [Interaction Trace](docs/interaction-trace.md), [main UiAtlas compatibility](docs/ui-atlas-compatibility.md), [privacy model](docs/privacy.md), [threat model](docs/threat-model.md), [qualification evidence](docs/qualification.md), [known limitations](docs/known-limitations.md), and [security policy](SECURITY.md).

## License and provenance

The repository carries the license in [LICENSE](LICENSE). Newly authored files and behavioral-reference dispositions are recorded in [the provenance ledger](provenance/files.csv). Runtime and test dependencies are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the [CycloneDX SBOM](sbom/ui-atlas-core.cdx.json).


## Authors and maintainers

UIAtlas was created and developed by **Daniel Kornev** and **Irina Nikitenko** as founders of AI Successors Corp.

- **Daniel Kornev** — [LinkedIn](https://www.linkedin.com/in/danielkornev/)
- **Irina Nikitenko** — [LinkedIn](https://www.linkedin.com/in/irina-nikitenko-07598657/)

For questions about the project, architecture, or collaboration, please contact the authors through LinkedIn.
