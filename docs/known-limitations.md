# Known limitations

- New recording acquisition uses the UI Maps native recorder. Atlas retains attended controls and bounded mapping/grid interaction helpers. Legacy bundles use an explicit compatibility adapter.
- Mapping checkpoints verify before/after pixels, topology and observed input separately from provider coverage. The editor can inspect same-observation crops and overlays even when alignment is unverified; its warning and original Survey quality remain unchanged. Older recordings cannot acquire stability retroactively. See [native recorder parity](2026-10-15-native-recorder-parity.md) and [five-mode composition](2026-10-15-appmap-five-mode-parity.md).
- MapLock receives an explicit unpublished-draft snapshot and retained workspace archive. Its catalog is separate; later edits are not synchronized back to Atlas.
- Grid reviews enter Semantic World only with an exact native host binding. Unresolved historical reviews remain visible. Reviewed columns do not establish qualified row reading or complete data coverage.
- Raw Data Streams, Raw World, Semantic World and Predicted remain distinct. Predicted state travels in full workspace archives but is not observed Map evidence.

For UI Maps migration behavior, storage, qualification and public/private scope,
see the [execution rollout](2026-10-13-uimaps-foundation-rollout.md). Converted Atlas
frames are partial Surveys. Review authority and observed interactions live in the
adjacent `.uimaps` workspace; legacy graph exports do not replace that authority.

- Native mapping retains a composite screenshot from the UI Maps screen provider and independent HWND images from Windows Graphics Capture. Independent images are bounded to five cumulative capture seconds and 32 MiB; unavailable images remain unavailable. Protected, hardware-overlay, minimized, cloaked, or higher-integrity windows may yield missing, blocked, or stale pixels. The legacy compatibility capture path retains its separately reported fallbacks.
- UI Automation and MSAA support depend on the target provider. Native provider workers run with deadlines and Windows Job Object containment; a failed attempt remains explicit raw evidence.
- Printable key identity is deliberately unavailable, so text-entry state correlation uses timing/category evidence only.
- Safe export removes free text, source linkage, screenshot references, coordinates, and canonical IDs. Retained kinds, topology, and cardinality can remain sensitive; it is not a proof of anonymity.
- Multi-monitor and per-monitor DPI metadata are collected. Automated qualification has run on the environment listed in `qualification.md`; Windows 10 and an independent clean Windows 11 x64 host remain unverified.
- Catalog `map open <map-id>` resolves matching full-evidence recording data automatically. Direct-path maps require `--evidence` or the Evidence button. Safe exports intentionally cannot resolve retained frames.
- The UI KG editor preserves the three-level pipeline and evidence views. For UI Maps workspaces, Review map supports acceptance, rejection, name/parent corrections and publication; it does not expose proprietary workflow authoring. The legacy graph renderer refuses graphs above 5,000 nodes or 10,000 edges to bound synchronous WPF materialization; the reader and CLI support the larger storage limits.
- Semantic World is deterministic structural generalization, not intent inference. It identifies reusable surfaces and controls but does not infer workflows, action templates, or business meaning.
- Existing v2 maps remain readable, but recordings built before 1.6.0 must be rebuilt to receive effective popup ownership, surface-local projection evidence, and higher-world observation curation. The 1.5.0 rebuild also introduced corrected borderless/transient popup classification, owner-scoped identity, semantic class names, and observation filmstrips.
- The 1.7.0 AppMap composition and topology-lane corrections are reader-only and apply immediately to an existing map. Re-recording or rebuilding is unnecessary unless the underlying UI states or the 1.6.0 graph corrections are missing.
- Version 1.8.0 writes organized `ui-atlas.uikg.export/1` JSON; legacy flat v2 JSON remains readable. During attended recording, ordinary clicks remain armed while tab and popup deltas are processed in the background. A very short-lived popup can close before its bounded background inspection and is then reported as a missed delta.
- Version 1.9.0 makes hierarchical `ui-atlas.map.json/1` the default `export map` format. It intentionally duplicates a control under every variant where that control was observed; use `format=sqlite` for compact canonical storage or `format=ui-atlas_flat` for the existing main-UiAtlas compatibility seam.
- V1 follows Win32 root-owner relationships. A separate same-process top-level window that is not owned by the selected root is not recorded automatically; select it explicitly for a separate recording.
- The compatibility adapter is pinned to the documented v5 authoring seam. Import into an existing project must use the destination's supported import/save path because its authoritative SQLite graph takes precedence over a JSON migration source. A future destination schema requires a new adapter version.
