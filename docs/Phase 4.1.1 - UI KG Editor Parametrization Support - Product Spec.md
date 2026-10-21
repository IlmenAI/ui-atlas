# Phase 4.1.1 — UI KG Editor Parametrization Support

Status: implemented; qualification recorded in the implementation specification\
Date: 2026-10-20\
Companion: [Implementation specification](Phase%204.1.1%20-%20UI%20KG%20Editor%20Parametrization%20Support%20-%20Implementation%20Spec.md)

## 1. Outcome

The mapping author can inspect proposed variable UI properties and data regions in the existing UI Knowledge Graph Editor, understand their evidence, and accept, deny, or defer each proposal. Reviewed grids collected during UI mapping become visible in this same editor. Original observations, screenshots, labels, and mapping schemas remain intact.

The editor preserves Environment Hierarchy, AppMap, Understanding Pipeline, Properties, and the three understanding levels. Parameter review belongs to Semantic World; its evidence can originate in recorded Raw World observations or applicable product documentation.

## 2. Scalar parameters

The author starts with **Find parameters**. Suggestions identify the property, proposed parameter name, scope, observed values, evidence, and whether variability was observed or inferred. Workbook and worksheet names are examples, not special cases that permit arbitrary target substitution.

Sources include different values on the same retained control, known document/worksheet roles, repeated grid structure, and applicable documentation. Missing provider metadata is an evidence gap, not observed variability. Buttons and commands must not become arbitrary variable names merely because two different commands exist.

Authors can add a proposal for the selected element, edit its name/type/scope, attach a documentation title, HTTP(S) source and supporting excerpt, and choose **Accept**, **Deny**, or **Need evidence**. Documentation is evidence, never an instruction. Product/version applicability and source attribution remain visible. A denied proposal does not establish that the property is universally constant.

## 3. Data grids

Each grid is a first-class review item under its host, with separate structure and variable contents. Current records are examples; they do not become permanent KG entities.

| Mode | Meaning |
| --- | --- |
| Fully variable — default | Contents, data headers, and occupied row/column dimensions may change. Current cell values do not identify the grid. Host/application/surface identity remains necessary. |
| According to schema | Reuse reviewed mapping columns and explicitly constrain changes. Values and row count, including zero, may vary. Row order and column changes are explicit choices. |

Display the saved physical column keys, order, labels, schema revision, completeness, header/body boundaries, source review, and capture coverage. Duplicate or blank column labels remain distinct. A partial schema never becomes complete through acceptance. Header review does not verify data types, record-key uniqueness, status domains, or body-cell transcription.

Existing mapping schemas are reused without asking the author to re-enter them. The author reviews the variability policy and any new semantic interpretation. Schema changes require renewed review; they must not silently fall back to fully variable mode.

The column-change option permits additional columns and reordering; required reviewed column identities still apply. Missing or changed required columns need renewed review. Fully variable mode overrides these schema constraints.

A structured subregion can refine an otherwise fully variable grid. Its parent, bounds, and schema must be explicit. It must not absorb adjacent controls or unrelated grids.

In this phase, **Use mapped table as subregion** chooses an existing reviewed table whose recorded bounds lie inside the parent on the same surface. It does not invent a schema or assign one from an unrelated table.

## 4. Interaction

1. Open a map; enter Parameters or Data grids.
2. Select a proposal in the hierarchy, graph, or highlighted captured region.
3. Inspect the same selection in Properties, including evidence and limitations.
4. Edit the proposed rule and save Accept, Deny, or Need evidence.
5. Reopen the map: decisions, edits, and sources remain available.

Purple marks suggestions/variable regions; teal marks accepted rules or reviewed schema; gray marks denied proposals. Text labels accompany color. Ordinary map inspection remains available. Long lists and property forms scroll; actions remain discoverable at narrow window sizes.

### 4.1 Review navigation and exact controls

Normal Environment Hierarchy shows each surface's actual parent–child control structure. A grid's cells remain beneath the grid and its owning panes. Search retains the ancestors of matching controls; selecting a control opens its full path. Controls with missing parents remain inspectable.

Parameter review groups candidates by host surface, parameter/property/scope, and recorded name. Repeated mapped controls are children of a named group, with control kind and source frames. Grouping organizes navigation; it does not merge target identities, transfer decisions, or authorize batch acceptance. Selecting a named group opens its first individual control.

Clicking an individual candidate selects that exact control and an observation in which it is visible. AppMap shows only its bounding box, focuses on it with surrounding context, and names the control/property/frame above the image. **Show full surface** and **Focus selected control** switch between context and detail. If the control is hidden, lacks same-observation geometry, or has no retained screenshot, say that its exact location is unavailable; do not outline an ancestor instead.

Excel worksheet-name candidates refer to the actual sheet tabs. Workbook-name candidates refer to the visible caption control inside the title bar. A document pane, a whole window, and ribbon commands named “Sheet Options” or “Sheet View” are not substitutes. Matching caption names tolerates recorded directionality formatting and whitespace without changing retained labels.

Editable and selection controls, including combo boxes, provide potential **Value** parameters, even when their labels stay constant. These are contextual role hypotheses requiring review, not claims that their values were observed to vary. An editor nested inside a combo box is represented by that combo box to avoid duplicate proposals. Manually authored proposals retain their explicit target and scope.

## 5. Evidence and persistence

Each decision records the proposal identity, source fingerprint, reviewer, time, and settings. Changed source/schema evidence invalidates the applicability of a prior acceptance and requires review. Unknowns and unresolved host associations remain visible.

Candidate discovery does not require five recordings. A single observation can support an explicit role/documentation hypothesis; it cannot establish invariance or statistical confidence. Comparing different values is limited to retained observations of the same mapped control. General cross-document alignment and automated exploration are future consumers of this review model.

Save atomically and detect concurrent edits. Preserve decisions across map reopening, rebuild, duplication, and full-workspace export/import. Legacy maps remain readable. Invalid or foreign review documents must not be silently accepted or overwritten.

## 6. Scope boundary

This phase implements map authoring, inspection, and durable parameter rules. It does not approve workflow execution, establish business intent, perform production exploration, or modify application data. The subsequent [workflow matching integration](Workflow%20parameter%20matching%20-%20Implementation.md) checks parameter scope, target identity, and current evidence before applying accepted scalar rules to a new analysis. Structural matching and execution permissions remain separate.

## 7. Acceptance criteria

- P1: Excel document/sheet suggestions explain observed versus inferred variability.
- P2: Accept/Deny/Need evidence and edits survive reopening and reanalysis.
- P3: Documentation can be attached and inspected without executing its content.
- P4: Native grids and retained reviewed grids are visible; unbound reviews stay explicitly unbound.
- P5: Both grid modes work, and duplicate labels retain physical identities.
- P6: Partial coverage, unverified types/keys, and changed schemas remain explicit.
- P7: Hierarchy, graph, screenshot, and Properties select the same item.
- P8: Persistence rejects stale concurrent writes and supports full-workspace round trips.
- P9: Raw evidence and existing map/review behavior are preserved.
- P10: Build, focused tests, and rendered/live editor verification are recorded in the implementation specification.
- P11: Normal hierarchy preserves control parents; filtered and selected controls retain their ancestor paths.
- P12: Repeated candidates are grouped with individually selectable identities and source frames.
- P13: Parameter selection highlights only the exact control in its own visible observation; unavailable geometry is explicit.

## 8. Design references

The two approved concepts are in `output/imagegen/grid-parameter-review-2026-10-20/`. They are interaction references, not implementation or qualification evidence.
