# Raw World surface reconstruction

The rollout follows the test5 observable Raw World behavior. Raw Data Streams
already satisfies the agreed surface/query contract and is unchanged.

## Implementation

`SurfaceSceneReconstructor` runs in the shared UIMaps perception pipeline before
tracking and publication. Atlas and the connected MapLock builds compile the same
implementation. The editor renders published geometry and relationships rather
than inventing ownership during painting.

The reconstructor preserves the original reported surface and parent, classifies
native windows/dialogs/tool windows/popups, and resolves popup branches using the
same episode's process identity, native ownership, ancestry and bounds. Word menu
branches returned through its main-window UIA query can therefore appear on the
correct popup. Native handles reported by accessibility are retained even when
they disagree with the actual popup HWND. Geometry plus positive popup ancestry
can resolve that disagreement; mere overlap cannot establish membership.

Structural containment, popup presentation and occlusion are separate facts.
Prior observations can establish continuity of a stable control; history never
supplies absent controls and is reset between recordings. A pre-existing gallery item may keep its original owner
while the popup presents it. A covered ribbon control remains on the main surface.
Clipping uses native bounds, display coverage and observed stacking order. Hidden
ancestry suppresses descendant pixels. Unknown timing stays explicitly qualified.

Popup fingerprints use observed content and relative layout, excluding volatile
runtime IDs. Since reconstruction v2, named families use the native title or a
corroborated accessibility root name, scoped by process, owner, class and type;
content fingerprints identify observation variants. Unnamed surfaces retain the
conservative content fallback. Simultaneous identical instances and unresolved
native identities are kept separate. Tooltips have `RawTooltipWindow` identity,
propagated as `SemanticTooltipWindow`. Temporal tracking
also checks the reconstructed surface identity. Empty but visible surfaces remain
inspectable; invisible native helper windows remain retained evidence and are
omitted from the editor's Raw World surface list.

No label-only opener inference or first-surface ownership fallback is used.
Ambiguous popup candidates retain reported ownership with an unresolved reason.
Perception completes missing visible records and their ancestors from this same
episode's retained worker responses when the recorder's preferred tree omitted
them. Exact matching records are canonicalized only in the derived view; original
provider attempts and raw responses are never rewritten. See the
[PowerPoint RCA](2026-10-19-powerpoint-raw-world-rca.md) for evidence and verification.

## Existing maps and CLI

Opening an existing native workspace reconstructs retained publications in memory
from their sealed episodes, using the same shared algorithm and original object
references. It does not rewrite the recording, stored publication, map authority,
or existing review decisions. CLI inspection, validation and graph exports use the
same current workspace projection instead of a stale adjacent SQLite cache.

Existing semantic assignments that were based on reported ownership can disagree
with the newly resolved Raw World owner. They keep their identity and reviewed
parent, with `sceneLineageBasis=retained-reported-owner` and a review note. The graph
validator requires exact, same-observation reported-owner provenance for this
compatibility case; arbitrary cross-surface lineage remains invalid. New captures
enter Map construction with reconstructed ownership already applied.

The graph preserves observation-specific `contains`, `presents` and `occludes`
relationships, source packet/accessibility IDs, source Raw Data Streams control
and surface references, original ownership, resolution reasons and visible regions.
Surface observations preserve frame-specific visibility and HWND identity, including
when a popup family contains observations with different handles. The five editor
modes use that observation's independent window image. If unavailable, known
covering windows are masked in the screen crop; their pixels never become part of
the selected surface. Raw World never substitutes an older image. Observations with
an owned foreground dialog are retained as evidence but excluded from the owner's
default carousel; the dialog keeps its own observation.
Control crops retain static text and clip covered portions to the published visible
regions. Semantic review, grid acquisition and workflow behavior are separate.

## Qualification

Regression cases cover main-query popup branches, stable gallery presentation,
overlap-only occlusion, partial visibility, oversized containers, hidden ancestors,
HWND reuse, repeated and simultaneous popup identities, foreign processes,
unresolved ownership, timing qualification, retained-publication replay, CLI cache
refresh and the Raw Data Streams boundary.

The saved Word recording is replayed without recapture. Qualification checks all
five modes across main-window, Colors, Fonts, Effects, Paragraph Spacing and Font
dialog observations. Backgrounds and every rendered control crop are compared
with the selected recording image. Existing Raw Data Streams source-selection and
pixel regressions are rerun separately, and original map/recording hashes are
checked. This is saved-recording validation, not a new live capture.

The local rollout qualified 53 mapping tests (including an attended native capture,
CLI recording handoff, grid-review import and editor rendering), 500 selected Core
tests, and 85 shared-perception tests with three skips. The Core run excludes its
desktop-interaction cases and the broad machine-path scan; repository boundary and
source-provenance checks run separately. This does not claim the full Windows test
suite or a new Word recording.

The Word replay passed 55 Raw World surface/mode comparisons across 11 observations
and 180 Raw Data Streams surface/provider/mode comparisons across nine observations.
Every compared background and control crop matched the selected source pixels;
the original map and recording hashes stayed unchanged. Six visible reconstructed
surfaces were retained: the main window, Colors, Fonts, Effects, Paragraph Spacing,
and Font dialog. The Atlas CLI/editor and both connected MapLock application outputs
build without warnings. MapLock's application UI itself was not manually exercised.

Live-test diagnostics exposed a fixture race: its initial screenshot included the
Windows window-opening animation. The fixture now waits for layout and compositor
settling and can retain a failed recording for diagnosis. Production recorder
stability checks were not changed. Logs, replay images and test reports are retained
locally under `.codex-out/raw-world-20261015/`.

The follow-up isolation correction is qualified under
`.codex-out/raw-world-isolation-20261015/`. The original replay compared Raw World
pixels to the composite screen image, which missed dialog pixels leaking into the
main surface. The revised 55-case Word replay instead requires the exact per-window
image for every observation and verifies that frame 10 is excluded from Word's
default carousel while remaining available on the Font dialog. Color-coded fixture
images distinguish composite, main-window and dialog pixels across all five modes,
including the masked fallback when independent images are absent.
