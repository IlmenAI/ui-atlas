namespace UIMaps.Application.Core;

public enum ExperienceProfileId
{
    Work,
    Maps,
    Integrations,
    Diagnostics,
}

public enum ExperienceAvailability
{
    Available,
    InDevelopment,
    NotInstalled,
}

public sealed record ExperienceProfileOption(
    ExperienceProfileId Id,
    string Label,
    ExperienceAvailability Availability)
{
    public bool IsAvailable => Availability == ExperienceAvailability.Available;
    public string Status => Availability switch
    {
        ExperienceAvailability.InDevelopment => "In development",
        ExperienceAvailability.NotInstalled => "Not installed",
        _ => string.Empty,
    };
}

public sealed record ExperienceNavigationItem(
    string Route,
    string Label,
    string Glyph,
    bool IsAvailable = true,
    string Status = "",
    bool StartsNewSection = false);

public sealed record ExperienceCommand(
    string Id,
    string Label,
    string Glyph,
    bool IsAvailable = true,
    string Status = "");

public sealed record ExperienceProfileDefinition(
    ExperienceProfileOption Option,
    IReadOnlyList<ExperienceNavigationItem> Navigation,
    IReadOnlyList<ExperienceCommand> NewCommands,
    IReadOnlyList<ExperienceCommand> HudCommands,
    string? LandingRoute = null);

public sealed class ExperienceProfileCatalog
{
    private readonly IReadOnlyDictionary<ExperienceProfileId, ExperienceProfileDefinition> _definitions;

    public ExperienceProfileCatalog(bool diagnosticsAvailable, bool workSessionsAvailable = false, bool skillsAvailable = false)
    {
        var work = new ExperienceProfileOption(ExperienceProfileId.Work, "Work", workSessionsAvailable ? ExperienceAvailability.Available : ExperienceAvailability.InDevelopment);
        var maps = new ExperienceProfileOption(ExperienceProfileId.Maps, "Maps", ExperienceAvailability.Available);
        var integrations = new ExperienceProfileOption(ExperienceProfileId.Integrations, "Integrations", ExperienceAvailability.InDevelopment);
        var diagnostics = new ExperienceProfileOption(
            ExperienceProfileId.Diagnostics,
            "Diagnostics",
            diagnosticsAvailable ? ExperienceAvailability.Available : ExperienceAvailability.NotInstalled);

        Profiles = Array.AsReadOnly(new[] { work, maps, integrations, diagnostics });

        var mapsNavigation = Array.AsReadOnly(new[]
        {
            new ExperienceNavigationItem("home", "Home", "\uE80F"),
            new ExperienceNavigationItem("work-sessions", "Tasks", "\uE7C3", workSessionsAvailable, workSessionsAvailable ? "" : "In development", true),
            new ExperienceNavigationItem("business-knowledge", "Business Knowledge", "\uE8F1", skillsAvailable, skillsAvailable ? "" : "Not installed"),
            new ExperienceNavigationItem("people-escalation", "People & Escalation", "\uE716", skillsAvailable, skillsAvailable ? "" : "Not installed"),
            new ExperienceNavigationItem("skills", "Skills", "\uE945", skillsAvailable, skillsAvailable ? "" : "Not installed"),
            new ExperienceNavigationItem("execution-history", "Execution History", "\uE9D9", skillsAvailable, skillsAvailable ? "" : "Not installed"),
            new ExperienceNavigationItem("recordings", "UI Map Recordings", "\uE714", true, "", true),
            new ExperienceNavigationItem("maps", "UI Maps", "\uE81E"),
            new ExperienceNavigationItem("integrations", "Integrations", "\uE774", false, "In development"),
            new ExperienceNavigationItem("models", "ML Models", "\uE950", false, "In development"),
            new ExperienceNavigationItem("settings", "Settings", "\uE713", true, "", true),
        });
        var mapsNew = Array.AsReadOnly(new[]
        {
            new ExperienceCommand("new.work-session", "Task", "\uE7C3", workSessionsAvailable, workSessionsAvailable ? "" : "In development"),
            new ExperienceCommand("new.map", "UI Map", "\uE81E"),
        });
        var mapsHud = Array.AsReadOnly(new[]
        {
            new ExperienceCommand("hud.record-map", "Record UI Map", "\uE81E"),
            new ExperienceCommand("hud.record-work-session", "Record Task", "\uE7C3", workSessionsAvailable, workSessionsAvailable ? "" : "In development"),
            new ExperienceCommand("hud.run-workflow", "Run Task", "\uE768", workSessionsAvailable, workSessionsAvailable ? "" : "Not available"),
        });
        var diagnosticsNew = Array.AsReadOnly(new[]
        {
            new ExperienceCommand("new.work-session", "Task", "\uE7C3", workSessionsAvailable, workSessionsAvailable ? "" : "In development"),
            new ExperienceCommand("new.map", "UI Map", "\uE81E"),
            new ExperienceCommand("new.integration", "Integration", "\uE774", false, "In development"),
        });
        var diagnosticsHud = Array.AsReadOnly(mapsHud.Concat(new[] {
            new ExperienceCommand("hud.diagnostics", "Diagnostics", "\uE9D2", diagnosticsAvailable, diagnosticsAvailable ? "" : "Not installed")
        }).ToArray());
        _definitions = new Dictionary<ExperienceProfileId, ExperienceProfileDefinition>
        {
            [ExperienceProfileId.Work] = new(work, mapsNavigation, mapsNew, mapsHud),
            [ExperienceProfileId.Maps] = new(maps, mapsNavigation, mapsNew, mapsHud),
            [ExperienceProfileId.Integrations] = new(integrations, mapsNavigation, mapsNew, mapsHud),
            [ExperienceProfileId.Diagnostics] = new(diagnostics, mapsNavigation, diagnosticsNew, diagnosticsHud),
        };
    }

    private ExperienceProfileCatalog(IReadOnlyList<ExperienceProfileOption> profiles,
        IReadOnlyDictionary<ExperienceProfileId, ExperienceProfileDefinition> definitions)
    { Profiles = profiles; _definitions = definitions; }

    public static ExperienceProfileCatalog ForAtlas()
    {
        var maps = new ExperienceProfileOption(ExperienceProfileId.Maps, "UI Maps", ExperienceAvailability.Available);
        var diagnostics = new ExperienceProfileOption(ExperienceProfileId.Diagnostics, "Diagnostics", ExperienceAvailability.Available);
        ExperienceNavigationItem[] navigation = [
            new("maps", "Maps", "\uE81E"),
            new("recordings", "Recordings", "\uE714"),
            new("diagnostics", "Diagnostics", "\uE9D2"),
            new("settings", "Settings", "\uE713", true, "", true)];
        ExperienceCommand[] create = [new("new.map", "UI Map", "\uE81E")];
        ExperienceCommand[] hud = [new("hud.record-map", "Record UI Map", "\uE81E")];
        return new([maps, diagnostics], new Dictionary<ExperienceProfileId, ExperienceProfileDefinition> {
            [ExperienceProfileId.Maps] = new(maps, navigation, create, hud, "maps"),
            [ExperienceProfileId.Diagnostics] = new(diagnostics, navigation, create, hud, "diagnostics") });
    }

    public ExperienceProfileId DefaultProfile => ExperienceProfileId.Maps;
    public IReadOnlyList<ExperienceProfileOption> Profiles { get; }

    public ExperienceProfileDefinition GetDefinition(ExperienceProfileId requested)
    {
        if (!_definitions.TryGetValue(requested, out var definition) || !definition.Option.IsAvailable)
            return _definitions[DefaultProfile];
        return definition;
    }
}
