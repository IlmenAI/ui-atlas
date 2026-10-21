# Workflow matching with reviewed parameter properties

Implemented 20 October 2026.

The historical Raw World matcher now consumes the scalar parameter decisions introduced by phase 4.1.1. A changed workbook, worksheet, document, slide, section, or input value can be ignored only through an applicable parameter property. Original observations remain intact.

## Confirm candidates during workflow comparison

Recording review now reads the KG editor's **suggested** scalar parameters for the retained Atlas revision. A separate comparison preview applies those suggestions to copies of the saved evidence. It displays the recorded and Atlas values, the candidate's evidence and scope, whether the difference explains a rejected path score, and any remaining structural failures or multiple reviewed targets.

Select an action, expand **Possible parameter differences**, inspect a comparison path, and choose **Confirm selected & re-analyze**. This saves the selected parameter definitions into the same Atlas review store used by the KG editor and starts a new durable analysis against the same exact revision. The two saved-result stages and original analysis remain unchanged until the new analysis produces its own results.

This is a review of parameter definitions, not a manual target assignment. A compatible comparison can support a parameter while other ancestors still fail. An incomplete gesture, missing input recipient, unknown outcome, or multiple targets remains significant.

The Atlas-owned `parameter-review-read` operation returns separate accepted and preview policies. The preview is never admitted to Workflow Recorder. `parameter-confirm` rebuilds current candidates, checks their fingerprints, exact publication, active generation, review revision and review checksum, and atomically saves only selected still-suggested definitions. Denied, stale, conflicting and unsupported grid proposals cannot be accepted through this shortcut. Existing decisions and raw evidence are preserved.

If another editor changes the review, refresh the suggestions before retrying. If confirmation succeeds but analysis cannot start, the Atlas decisions remain saved and a new analysis can be started separately. Existing saved analyses remain available when the catalog or suggested-parameter preview is unavailable.

## Rule admission

The Atlas catalog compiles `parameter-review.json` when handing an exact revision and its original Raw World sources to a new analysis.

- Only **Accepted** decisions with a current source fingerprint qualify. Suggested, denied, stale, missing, and unsupported grid-content decisions do not create matching rules.
- The current draft entity must still equal its published entity after canonical ordering. A decision about changed draft evidence cannot be applied to an older publication.
- Every rule identifies its proposal, fingerprint, property, published entity, exact source hash, original object, and expected recorded value.
- Explicit denials take precedence when several semantic projections refer to the same raw property. Conflicting masks are withheld.
- A review snapshot and its checksum are retained with the analysis. Later editor changes affect new analyses; saved analyses keep their original rules and results.

## What varies

`Name` permits the marked name to vary. `Value` permits an input value to vary while preserving its stable label, such as “Font Size.” A value becomes a variable accessible name only when the original provider explicitly reports that same value as the name.

Workbook/document captions retain the application suffix and recognized read-only, compatibility, and protected-view markers. Exact copies in document containers can share the parameter. A worksheet name can also vary in the recorded `Sheet <name>` pane. These bindings stay within the same original surface and respect an explicitly narrowed review scope.

An automation identifier varies only when it is an exact copy of the marked name or its parameter portion. A stable identifier such as `SheetTab` is retained. The observed name and identifier copies must agree, as must repeated occurrences of the same parameter along a path.

Word and PowerPoint caption discovery now follows the same title-bar evidence rule as Excel. Explicit slide/section roles can propose naming parameters. Generic controls still need retained variation evidence or an explicit mapper decision; role labels alone do not authorize a match.

## What still determines a match

Application identity, original parent path, control role, incompatible native classes, stable provider identifiers, source integrity, and reviewed publication lineage remain significant. A parameterized leaf still needs a stable class or identifier anchor. Missing parents and anonymous controls do not become known through a wildcard.

An equal parameter value receives the same score as a changed one. It cannot break a tie between otherwise equivalent sheets, slides, or sections. The matcher retains ambiguity instead of choosing an arbitrary target.

The change does not associate an incomplete gesture with a recipient, verify an action outcome, change execution permissions, or relax grid schemas.

## Implementation boundaries

| Layer | Change |
| --- | --- |
| UI Atlas Reader | Office caption and explicit slide/section proposals; existing Excel decision fingerprints preserved |
| UI Atlas catalog | Compile current accepted scalar decisions; expose candidate previews and scoped confirmation |
| Catalog contract | Additive parameter-policy and review DTOs; no private workflow dependency |
| Private workflow contract | Retained parameter-policy snapshot and result checksum |
| Localization | `historical-raw-world-binding/2`, with shared parameter-aware scoring |
| Workflow Recorder | Validate and retain the policy; verify Localization used the admitted snapshot |
| Recording review | Explain saved comparisons; preview suggestions, confirm parameter definitions and start a new analysis |

Legacy `historical-raw-world-binding/1` analyses remain readable with their original diagnostic scores. Updated workflow services require paired Atlas and Localization tools that advertise the new contract rather than silently ignoring parameter metadata.

## Qualification

Automated checks cover Office names and font values, fixed labels and caption suffixes, denied/stale decisions, exact source binding, ambiguity, invalid parent/class/application/identifier evidence, repeated parameter consistency, integrity failures, offline retention, and unchanged historical results.

The retained Excel map and saved Book2 observations were also exercised read-only with synthetic accepted decisions. Those qualification decisions were never saved into the user's map. Suggestions can now be confirmed from Recording review while examining the mismatched values, or from the KG editor, before a new workflow analysis uses them.

See the [validation report](../output/parameter-matching-2026-10-20/verification.md) for test totals and retained-data results.

See the [candidate-review validation](../output/parameter-review-2026-10-20/verification.md) for the subsequent confirmation flow.

## Shared confirmations and action filters

Current scalar confirmations now apply to equivalent structural roles in the same map. The Reader computes a role key from the application, property, inferred parameter role, scope, stable provider selectors and complete semantic ancestor path. Recognized document names may vary along that path; fixed labels and control identities remain significant. Font Size and Font Name, or the same setting in different fixed contexts, do not share a confirmation.

The same resolver serves the graph editor and catalog matching/preview. It computes inherited decisions without saving additional human approvals. Compiled rules retain the original approval ID and fingerprint together with the exact target entity and source-property bindings. Only current, unmodified inferred definitions propagate; manual proposals, grids, narrowed definitions and stale source decisions do not. A local decision, including a denial or stale review, takes precedence over inheritance. Targets outside the exact published revision remain ineligible.

Recording review displays the additional controls covered by existing confirmations and explains the scope of a new confirmation. Already covered candidates are not offered for redundant approval. New analyses admit the expanded policy; earlier saved analyses keep their original policy and results.

Workflow actions now offers **Complete clicks** and **Incomplete gestures** alongside All actions, Needs attention and Excluded. Complete clicks includes ordinary clicks and right-clicks whose recorded gesture is complete, regardless of whether their targets resolved. Filters compose with search and preserve annotations; they do not remove retained actions or manufacture missing input. When an observation matches Atlas but its action has no recipient, the action list explicitly says “Object matched · gesture incomplete” or “Object matched · action recipient unresolved.”

The legacy `test5` filter only heuristically identified a terminal recorder-stop click from screen position and stop timing. That heuristic is not used to classify arbitrary workflow actions here.
