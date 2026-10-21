using System.Windows;
using UiAtlas.Core.Storage;
using UiAtlas.Shell;
using UiAtlas.Shell.Catalog;
using UIMaps.Application.Core;

namespace UiAtlas.Launcher;

public sealed class AtlasShellHost : IShellHost
{
    private readonly AtlasCatalogClient _catalog;
    private readonly LauncherPreferences _preferences;
    public AtlasShellHost(ExperienceProfileId? workspace = null, LocalArtifactCatalog? catalog = null,
        LauncherPreferences? preferences = null)
    {
        _catalog = new AtlasCatalogClient(catalog?.Root);
        _preferences = preferences ?? new();
        StartupProfile = workspace ?? _preferences.Load();
        Launcher = new AtlasCapabilityLauncher(_catalog);
        Maps = new AtlasMapsLibrarySource(_catalog);
    }
    public string ProductName => "UI Atlas";
    public ExperienceProfileCatalog Profiles { get; } = ExperienceProfileCatalog.ForAtlas();
    public ExperienceProfileId StartupProfile { get; }
    public string StartupRoute => StartupProfile == ExperienceProfileId.Diagnostics ? "diagnostics" : "maps";
    public IUiMapsCapabilityLauncher Launcher { get; }
    public IMapsLibrarySource Maps { get; }
    public IRecordableApplicationDiscovery Applications { get; } = new RecordableApplicationDiscovery();
    public IRecordingDisplayCatalog Displays { get; } = new RecordingDisplayCatalog();
    public FrameworkElement CreateSettingsView() => new AtlasSettingsView(_preferences, _catalog.Root);
    public FrameworkElement CreateRecordingsView() => new AtlasRecordingsView(_catalog);
}
