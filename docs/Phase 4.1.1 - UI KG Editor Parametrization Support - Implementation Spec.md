# Phase 4.1.1 — UI KG Editor Parametrization Support: Implementation

Status: implemented and qualified locally on 2026-10-20\
Date: 2026-10-20\
Companion: [Product specification](Phase%204.1.1%20-%20UI%20KG%20Editor%20Parametrization%20Support%20-%20Product%20Spec.md)

## 1. Existing integration

`ExplorerWindow` is the shared WPF KG editor. `UiMappingReadModel` provides observed and semantic projections. `AtlasMapWorkspace` owns the `.db.uimaps` generation, while the adjacent graph database is a compatibility projection.

Mapping already retains `SavedDataGridReview`, physical `GridColumn.ColumnKey`, header/body regions, schema completeness, coverage, and source image identity. `AtlasGridReviewAdapter` retains these in workspace generations and binds eligible schemas to exact recorded hosts. Phase 4.1.1 exposes that evidence; it does not invent a second grid capture pipeline.

The adapter binds `HostedDataGrid.SourceGridRef` to the retained review's `atlas:grid:<id>` and resolves its mapped grid and host. `GridInternalDataGridRef` describes internal controls and is not the source-review identity. Duplicate/ambiguous bindings remain unresolved. The native host is suppressed as a duplicate review item once its mapped grid is present.

## 2. Components

| Component | Responsibility |
| --- | --- |
| Contracts | Versioned parameter evidence, scalar/grid proposals, review decisions, documentation sources, and grid variation rules |
| Reader analysis | Deterministic candidate discovery, evidence fingerprints, observed/inferred distinctions, grid aggregation, and decision applicability |
| Storage | Bounded validation, atomic revision-checked persistence in the active workspace generation; legacy sidecar fallback |
| Mapping adapter | Expose retained reviewed grids with exact host bindings and evidence provenance |
| Desktop partial class | Review toolbar, hierarchy, graph, overlays, properties/forms, evidence/source actions, and save/error handling |

## 3. Review model

Proposal identity is derived from map-local target identity and property/region, not displayed data values. Source fingerprints cover relevant evidence and schema revision. Accepted/denied/deferred state is separate from the detected proposal. Reanalysis preserves decisions only when the source fingerprint is unchanged.

A grid rule records FullyVariable or AccordingToSchema, row-count and row-order variation, column-change policy, optional schema reference, and explicit subregion bounds. Column identities are retained independently of display labels. A documentation source records its title, URI, excerpt, and applicability; only HTTP(S) links can be opened, and sources are never executed.

Initial discovery is bounded, local, and deterministic. It uses same-control observed differences and explicit role/document priors. Grid cells do not create thousands of scalar suggestions. Documentation-backed suggestions retain their source; user attachments support additional controls. No LLM service, credential, or new network dependency is required.

## 4. Storage and compatibility

Use `parameter-review.json` inside the active `.uimaps/generations/<id>` for authoritative workspaces. Reuse the workspace write lease and check the generation before committing. Full-workspace packaging already carries generation files; import additionally validates this document. Builds copy forward review history; applicability is recalculated against current proposals. Duplication rebinds the logical map identity without rewriting evidence.

Legacy maps use a bounded adjacent `.parameters.json` companion. Catalog copy/archive/delete handles it. Review data never rewrites original graph nodes or source bundles. Unknown format versions, invalid enums, excessive counts, invalid links/bounds, foreign logical identities, and stale revisions fail explicitly. Write via a temporary file and atomic replacement.

The first authoritative rebuild also carries forward a legacy review document after validating its original map identity. The legacy file is retained; subsequent copies prefer the active generation. Saves check both the document revision and the generation observed by the editor, so a rebuild cannot silently accept a review against an older graph.

## 5. Editor integration

Add a compact Data & parameter review toolbar and Parameters/Data grids modes to the existing editor. Existing map modes and evidence attachment remain available.

Selection updates hierarchy, the compact review graph, captured image highlights, and Properties together. Grid overlays distinguish reviewed header from variable body and show evidence coverage. Unbound grid evidence uses the retained, hash-checked image with an explicit binding limitation. Review forms support scalar edits, grid policy changes, documentation, manual selected-element suggestions, and subregion refinement.

Parameter overlays use the selected observation's visible regions; offscreen or conflicting source geometry is withheld. The schema header and variable body use separate colors. Review uses the Window view; **Back to map** restores ordinary projection controls. Explicit subregions reuse a mapped table within the parent on the same surface, and rebind their image/schema references on reload.

Every save reports success or the specific failure. Reopening and reanalysis use saved decisions. The initial authoring phase did not alter workflow scoring. The subsequent [parameter matching integration](Workflow%20parameter%20matching%20-%20Implementation.md) consumes applicable accepted scalar decisions for new analyses; execution permissions remain separate.

## 6. Verification plan

- Unit tests: candidate provenance, no variability inferred from missing metadata, grid-cell suppression, duplicate headers, partial schemas, decision invalidation.
- Persistence tests: first save/reopen, stale writer rejection, malformed/foreign documents, copy/rebind, export/import, evidence preservation.
- WPF tests: actual review controls, selection synchronization, all three decisions, mode switching, narrow rendering.
- Build the shared editor and standard UI Atlas launcher; stage the same editor through the MapLock launcher integration when available.
- Open retained Excel/grid evidence in the built editor and retain screenshots of actual behavior, separately from concept art.

## 7. Qualification record

### Automated checks

The following focused run passed **75 tests**, including the actual WPF review form, all three decisions, selection synchronization, both grid modes, duplicate and blank column labels, partial coverage, documentation validation, manual candidates, schema subregions, stale revisions/generations, legacy migration, catalog lifecycle, full workspace round trips, unchanged original evidence, and existing rendering/repository regressions:

```powershell
dotnet test tests/UiAtlas.Core.Tests/UiAtlas.Core.Tests.csproj --no-restore --nologo -v:q -p:StopRunningUiAtlasProcesses=false --filter "FullyQualifiedName~ParameterizationTests|FullyQualifiedName~RawOfficeCoordinatesTests|FullyQualifiedName~SemanticWorldPresentationTests|FullyQualifiedName~LocalArtifactCatalogTests|FullyQualifiedName~RawPopupRepairTests|FullyQualifiedName~RepositoryBoundaryTests" --logger "trx;LogFileName=phase-4.1.1.trx"
```

The live mapping integration test also passed (**1 test**). It captures a controlled WPF table, retains its reviewed schema, verifies the exact host binding, saves a schema variation decision, transfers the full workspace, verifies the decision's continuing applicability and image identity, and loads it in the real KG editor:

```powershell
dotnet test tests/UiAtlas.Core.Mapping.Tests/UiAtlas.Core.Mapping.Tests.csproj --no-restore --nologo -v:q -p:StopRunningUiAtlasProcesses=false --filter FullyQualifiedName~AttendedCaptureOfOwnedTestWindowBuildsMapsAndRendersTheExistingEditor --logger "trx;LogFileName=phase-4.1.1-mapping.trx"
```

Results are retained under each test project's `TestResults/`. Setting `UIATLAS_PARAMETER_QA_DIR` retains rendered qualification screenshots.

### Build and deployment

Both commands succeeded with **zero warnings and zero errors**:

```powershell
dotnet build src/Launcher/Launcher.csproj --no-restore --nologo -v:q -p:StopRunningUiAtlasProcesses=false
dotnet build ../UIMaps.Core/uimaps-application/src/Launcher/Launcher.csproj --no-restore --nologo -v:q -p:StopRunningUiAtlasProcesses=false
```

UI Atlas stages the editor in `src/Launcher/bin/Debug/net10.0-windows10.0.19041.0/tools/viewer/`. MapLock stages its independently built copy in `../UIMaps.Core/uimaps-application/src/Launcher/bin/Debug/net10.0-windows10.0.19041.0/atlas/viewer/`. Each staged DLL is checked against its own build output; the isolated MapLock build is not required to have the same binary hash as the ordinary build.

Final SHA-256 verification of `UiAtlas.Core.Desktop.dll`:

| Deployment | Hash; matches its build output |
| --- | --- |
| UI Atlas | `D913DECF348A34C412E086B849D96A793D1A580C2E084007FA3C7C18257F027E` |
| MapLock | `ACD8707ADBD44B543E0A505DE693758483A6C74C624E3FBEEE803D2AAA6A19F7` |

### Visual and retained-recording checks

The retained Excel map `20261019-131419-excel-afa718e8` opened in the running editor with **8 parameter candidates and 2 data grids**. Workbook/worksheet names appear with their evidence. Grid contents default to Fully variable. The same map opens through the staged UI Atlas CLI's `map open` path. This existing recording has no saved reviewed column schema; the separate controlled live mapping test establishes that integration.

- [Actual editor: mapped schema from the live controlled table](assets/phase-4.1.1/live-mapped-schema-editor.png).
- [Actual editor: schema policy and duplicate/blank labels in the WPF fixture](assets/phase-4.1.1/grid-review.png).
- [Actual editor: narrow window](assets/phase-4.1.1/grid-review-narrow.png).

These are rendered application evidence, not the earlier ImageGen concepts. The two table fixtures contain synthetic example records. A multi-observation rendering assumption found during qualification was also repaired: raw coordinate lookup now abstains when no registration exists instead of requiring one observation on every control.

### Deliberate limits

- Discovery is local and conservative. It does not claim that one or five recordings prove an element is constant.
- Product-document priors currently include Excel naming. Other documentation can be attached by the mapper; there is no automatic web crawler or LLM dependency.
- Schema mode requires a retained reviewed mapping schema. Adding a subregion requires an explicitly mapped inner table; arbitrary region drawing/schema invention is outside this phase.
- A reviewed header does not prove value types, a unique record key, full row coverage, or correct row transcription.
- Saved rules do not rewrite existing historical workflow resolution, authorize execution, or establish business intent. The subsequent [parameter matching integration](Workflow%20parameter%20matching%20-%20Implementation.md) validates scope and current source evidence before using accepted scalar rules in a new analysis.

## 8. Review follow-up: hierarchy and exact parameter targets (2026-10-20)

### Root cause

The 14:29 review recording exposed three coupled problems:

1. `BuildHierarchy` alphabetized all controls directly beneath a surface, discarding the already retained `ParentControlId` relationship.
2. Parameter review grouped only by surface. Separate control identities from different mapped variants appeared as repeated names without their control kind or source observation.
3. Discovery treated `Sheet ...` labels and document/window containers as naming parameters. This missed `Sheet Tab` controls and Excel's caption button (whose label includes directionality characters and extra spaces), while admitting “Sheet Options” and “Sheet View”. The overlay then drew every candidate on the surface simultaneously, including large container rectangles. Selection only changed stroke weight; choosing the latest observation could also choose hidden geometry.

### Implementation

- `UiControlHierarchy.Parents` constructs a surface-local forest from retained control parents. Search includes matching nodes and their ancestors; absent parents become roots and cycles are broken for presentation. Raw records are unchanged.
- The normal editor keeps lazy surface expansion, attaches control items beneath their real parents, and uses the existing path reveal for selected controls.
- Parameter navigation is surface → parameter/property/scope → recorded name → individual control. Controls retain distinct proposal IDs, decisions, kinds, and frame lists.
- Discovery recognizes worksheet tabs by role/provider identity, captions by title-bar ancestry plus normalized application title, and contextual input values by control role. Broad automatic container proposals are removed. Old decisions are not copied to newly discovered child controls.
- `ParameterReviewPresentation` requires evidence for the exact bundle/frame and excludes retained-from-another-frame, offscreen, and conflicting semantic geometry. Selection prefers an observation with visible geometry and a screenshot.
- AppMap overlays only the selected proposal. It focuses on that box, offers full-surface context, and reports missing location evidence explicitly. Reviewed grid imagery and header/body policy overlays remain available.
- Retained Office dialogs can contain DPI-virtualized provider coordinates. Parameter geometry follows the exact semantic → Raw World → Raw Data Streams source references and reuses `RawOfficeCoordinates` only when its independent native anchors qualify the transformation. Unqualified suspicious dialog geometry is withheld. A duplicate parent-window query is also withheld when the identical provider/runtime identity is present in a child-window query in the same process and frame.
- Tests exercise real WPF tree selection, nested control paths, repeated candidate groups, exact bounds, missing/offscreen geometry, and both grid modes. Optional `UIATLAS_PARAMETER_RETAINED_MAP` qualification opens the actual retained Excel map and selects worksheet, caption, Font Size, and dialog controls through the tree.

### Follow-up qualification

The focused suite passed **89 tests**. An additional run of the **12 coordinate tests** passed after extending the semantic-parameter regression to check the independently expected physical rectangle and abstention without native anchors.

```powershell
$env:UIATLAS_PARAMETER_RETAINED_MAP="$env:LOCALAPPDATA/UiAtlas/Core/maps/20261018-172907-excel-dbb8ea28.db"
$env:UIATLAS_PARAMETER_QA_DIR="C:/stuff/dev/successors/ui-atlas/artifacts/phase-4.1.1-hierarchy-review"
dotnet test tests/UiAtlas.Core.Tests/UiAtlas.Core.Tests.csproj --no-restore --nologo -v:q -p:StopRunningUiAtlasProcesses=false --filter "FullyQualifiedName~ParameterizationTests|FullyQualifiedName~RawOfficeCoordinatesTests|FullyQualifiedName~RawPopupRepairTests|FullyQualifiedName~SemanticWorldPresentationTests|FullyQualifiedName~UiMappingReadModelTests|FullyQualifiedName~RepositoryBoundaryTests" --logger "trx;LogFileName=phase-4.1.1-hierarchy.trx"
```

This qualification used the same retained Excel map as the user's video. Its automatic proposals changed from 64 broad naming candidates to 122 specific naming/value candidates, grouped for navigation; 26 grid candidates remain. Counts include distinct mapped control identities across observed variants, not 122 independent business parameters.

Verified local bounding boxes in the corresponding window image:

| Selected control | Frame | X, Y, width, height |
| --- | --- | --- |
| Inventory list sheet tab | 16 | 112, 978, 108, 32 |
| Workbook caption button | 21 | 303, 0, 187, 48 |
| Font Size combo box | 7 | 320, 90, 49, 24 |
| Insert Function category combo box | 16 | 114, 99, 214, 25 |

The dialog's unregistered box was 227, 169, 191, 50 after clipping, visibly over the wrong area. The qualified DPI registration restores the actual category combo box. The duplicate dialog candidate under the main Excel surface now reports its child-window provenance and draws no box there.

- [Actual normal control hierarchy](assets/phase-4.1.1/control-hierarchy.png)
- [Actual worksheet parameter selection](assets/phase-4.1.1/parameter-worksheet-tab.png)
- [Actual workbook caption selection](assets/phase-4.1.1/parameter-workbook-caption.png)
- [Actual dialog value selection](assets/phase-4.1.1/parameter-dialog-value.png)

The screenshots and `artifacts/phase-4.1.1-hierarchy-review/retained-selection.json` record the rendered editor and selected source identities. Original map evidence and parameter decisions were not rewritten during this qualification.

The controlled live mapping/grid integration test passed again (**1 test**; `phase-4.1.1-hierarchy-mapping.trx`). Both standard launcher builds completed with zero warnings and zero errors. The MapLock build required `-m:1 -nr:false` after an initial shared intermediate-assembly lock.

Updated staged editor hashes, each verified against its corresponding build output:

| Deployment | SHA-256 |
| --- | --- |
| UI Atlas | `7A229E6468B9B4F2E66A4E4416E0B071D75127736E3A5128757F19EABBF9ABD6` |
| MapLock | `4A57ADA8D6BA96765B96DF73CBF3D0F10C31586EEB9E786D1196A4ECF69BC7D6` |

The staged MapLock editor was reopened on the reviewed Excel map. Native UI interaction verified Parameters, grouped worksheet navigation, search, and clicking the second “Inventory list” control: selection changed to frame 17 and the visible outline remained on the sheet tab. The editor was left open with that selection and the search cleared.
