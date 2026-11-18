# Shared native appearance

This WPF-only library owns appearance for the Atlas launcher, viewer, recorder,
MCP capture capsule, and the private MapLock hosts. It has no dependency on the
shell, capture protocol, recording services, or business models. The existing
linked `AtlasFluent` factories delegate to this library; their assembly identity
and the public/private source ownership in `UIMaps.SharedFoundation.targets`
remain intact.

Hosts call `Appearance.Initialize(application)` before constructing the first
window. Recorder UI dispatchers call `Initialize()` before creating companions;
standalone companion windows can call `Apply(window)`. Code-created controls use
`SetResourceReference` or `Appearance.Bind`; XAML uses `DynamicResource`.

`%LOCALAPPDATA%/UIMaps/appearance.json` is an atomic, per-user preference file:

```json
{"schemaVersion":1,"themeId":"maplock","colorMode":"light"}
```

It contains no credentials or business settings. Tests may override its path with
`UIMAPS_APPEARANCE_SETTINGS`. Supported IDs are `plex`, `aero`, `chatgpt`,
`maplock`, `ilmen`, and `fluent`; modes are `light` and `dark`. Invalid fields fall
back independently to MapLock/light. Missing, malformed, or future-version files
load the default without overwriting the file.

In-process changes broadcast only immutable identity/mode. Each window and
application constructs its own resources on its dispatcher. Closed windows
unsubscribe. A two-second dispatcher timer and window activation refresh the
same preference in independent processes; no workspace is recreated and no
recording or execution command is sent. High Contrast replaces decorative
resources with system brushes and refreshes on system changes.

Style and template instances (including Fluent container templates) survive a
live change; only their resource values change. This prevents WPF from raising
another workspace Loaded event or replacing an editable control's text editor.

The twelve dictionaries translate the supplied MapLock design tokens to typed
WPF resources. Shared templates live in `Controls.xaml`. Status resources are
independent of the selected accent. Fonts use local Windows font fallback lists;
appearance never fetches network fonts. Retained images and coordinate overlays
remain evidence content, outside the palette.

Run `tests/UiAtlas.Presentation.Tests` for persistence, native templates, state
retention, and dispatcher ownership. `UIATLAS_APPEARANCE_QA_DIR` optionally saves
native palette PNGs outside the source tree. Product qualification still includes
the existing shell, review, recorder, append-map, and schema history tests.
