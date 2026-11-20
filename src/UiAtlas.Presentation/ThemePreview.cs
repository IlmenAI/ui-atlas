namespace UiAtlas.Presentation;

internal sealed record ThemePreview(string Id, string Label, string Description, string Accent,
    string Line, string Sidebar)
{
    // Each miniature has a fixed palette so the choices stay recognizable in either color mode.
    internal static IReadOnlyList<ThemePreview> All { get; } =
    [
        new("plex", "Plex", "Blue title bands, sculpted controls and softly shaded workspaces.",
            "#356EB5", "#CBD7E7", "#EDF2FA"),
        new("aero", "Slate", "Silver glass, blue command bars and luminous selection.",
            "#386DA8", "#C2CBD4", "#F0F3F6"),
        new("chatgpt", "ChatGPT-like", "Neutral surfaces, quiet dividers and a text-first hierarchy.",
            "#222222", "#E3E3E3", "#F4F4F5"),
        new("maplock", "MapLock", "The current MapLock palette, Segoe UI and familiar blue accents.",
            "#066BD7", "#D5E0F0", "#F2F6FC"),
        new("ilmen", "Ilmen", "DM Sans, Instrument Serif and Ilmen’s calm blue-gray palette.",
            "#426D87", "#D3DEE5", "#EEF3F6"),
        new("fluent", "Fluent", "Windows-inspired layered surfaces, restrained elevation and clear blue selection.",
            "#005FB8", "#DCE1E8", "#EEF1F6")
    ];
}
