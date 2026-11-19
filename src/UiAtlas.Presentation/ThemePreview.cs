namespace UiAtlas.Presentation;

internal sealed record ThemePreview(string Id, string Label, string Description, string TitleStart,
    string TitleEnd, string Sidebar, string Line, string Accent, string Surface = "#FFFFFF")
{
    // Each miniature has a fixed palette so the choices stay recognizable in either color mode.
    internal static IReadOnlyList<ThemePreview> All { get; } =
    [
        new("plex", "Plex", "Blue title bands, sculpted controls and softly shaded workspaces.",
            "#4C83CB", "#7AB9F7", "#E2E8F1", "#BCCCE3", "#5595D5"),
        new("aero", "Slate", "Silver glass, blue command bars and luminous selection.",
            "#E1E5E6", "#A7B5C2", "#EFF2F4", "#C3CEDB", "#3E679E"),
        new("chatgpt", "ChatGPT-like", "Neutral surfaces, quiet dividers and a text-first hierarchy.",
            "#F8F8F8", "#F8F8F8", "#EEEEEE", "#D7D7D7", "#222222"),
        new("maplock", "MapLock", "The current MapLock palette, Segoe UI and familiar blue accents.",
            "#F8FBFF", "#F8FBFF", "#F0F5FB", "#CCDAEF", "#066BD7"),
        new("ilmen", "Ilmen", "DM Sans, Instrument Serif and Ilmen’s calm blue-gray palette.",
            "#080B0E", "#080B0E", "#18232D", "#5B7283", "#A9CCE0", "#10151B"),
        new("fluent", "Fluent", "Windows-inspired layered surfaces, restrained elevation and clear blue selection.",
            "#EEF2F7", "#EEF2F7", "#EDF1F6", "#CBD3DF", "#005FB8")
    ];
}
