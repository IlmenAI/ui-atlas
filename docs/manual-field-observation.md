# Manual field observation

This increment adds a field observation and review layer to the existing UI Map recorder. It records what changes while a person uses an application, without filling fields or changing replay matching rules.

## Use it

1. Start a manual UI Map recording. Open **Fields** in the recorder to see suggested inputs. The **Fields** switch in Highlights controls their outlines; observation continues with outlines hidden.
2. Edit fields in the application normally. Text, number/date inputs, selections, checkboxes and other controls are suggested from their exposed capabilities. A supported value, selection or checked-state difference changes the label to **Change observed**.
3. If a field was missed, press **Mark a field**, then click the field in the application. Press the button again to cancel marking. The click still follows the ordinary recording path. If the screen changed before the field could be identified, marking stays armed so it can be retried.
4. Choose **Review changes** during recording, or review the changes offered when stopping. Keep the observations you want; unchecked rows are marked Later. **Later** closes the review without changing existing decisions.

The existing rounded recorder, blue controls, wave artwork, stop/pause controls and Highlights window are retained. Review pauses recording. No field-writing, focus-walking or automatic filling is introduced by this layer.

## What the evidence means

- **Suggested** means an input capability was observed, or the person explicitly marked an observed control. It does not imply that a readable value exists.
- **Change observed** requires an actual value, selection or toggle difference for the same qualified control identity. Name, Automation ID, class, bounds and enabled-state differences are retained separately.
- **Keep** saves the observation for later use. It does not certify that the control can be found in another application run and does not loosen a locator or replay gate.

Controls are correlated within the recording using an actual provider runtime ID scoped to the application/window/provider, or an unambiguous Automation ID ancestry path. Observation-local evidence IDs are not runtime IDs. Coordinates are used to select and draw a field, never as its identity. Ambiguous paths remain observation-only. A complete disappearance interrupts comparison; partial or unqueried provider coverage does not fabricate disappearance.

Empty values are explicitly different from unavailable values. Unknown password status cannot authorize reading content; protected and known read-only controls are excluded from learning. UIA range values, selections and bounded document text are read only inside the existing isolated worker. Older protobuf observations without field presence cannot establish a successful empty read.

## Capture and persistence

Normal full observations seed the layer. A read-only UIA sample is attempted every 1.5 seconds while manual capture is active, skipping a busy capture gate. Each pass has a three-second aggregate budget and a bounded per-window worker. Stop takes one final bounded sample before review and seals the reviewed evidence afterward. Pausing, auto exploration and canceled sessions do not start autonomous field edits.

Recording bundles contain:

- `raw/field-observations.jsonl`: compact, deduplicated samples with acquisition times, scoped control identities, safe values and lightweight ancestry evidence.
- `derived/field-learning.json`: suggestions, observed property changes, unchanged properties and review decisions.

The logical map session manifest also retains the reviewed snapshot under `fieldLearningSessions`, keyed by recording session. Subsequent campaign saves and resumed recordings preserve it. The sealed bundle remains the evidence copy if updating the map's index fails.

The tracker is bounded to 512 fields and 64 property differences per field. Raw evidence uses the existing 2,000-observation/32 MB soft limits, retaining the triggering sample before stopping and reporting truncation. Individual evidence projections are bounded independently. Repeated scans do not consume the quota merely because their observation IDs changed.

## Qualification and remaining scope

Regression coverage checks empty versus missing reads, password/read-only handling, duplicates and ancestry, passive samples with sequence zero, provider-specific absence, mixed value/selection/toggle capabilities, manual marking, immutable snapshots, evidence deduplication/provenance, protobuf presence, merging and projection, source-coherent overlays, review interaction and workspace persistence.

This is the observation/review step toward better grounding. It does not yet compile kept observations into variable-property matching rules or prove re-identification after reopening a screen. Sampling may miss transient intermediate values, and controls without usable accessibility evidence can remain suggested even after a person edits them. Live qualification against the VHP application is a separate check from compilation and synthetic regression tests.
