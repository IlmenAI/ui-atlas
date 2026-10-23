using System.Windows;
using UIMaps.Application.Core;

namespace UiAtlas.Shell;

/// <summary>Product composition for the shared desktop. Views never locate services themselves.</summary>
public interface IShellHost
{
    string ProductName { get; }
    ExperienceProfileCatalog Profiles { get; }
    ExperienceProfileId StartupProfile { get; }
    string StartupRoute { get; }
    IUiMapsCapabilityLauncher Launcher { get; }
    IMapsLibrarySource Maps { get; }
    IRecordableApplicationDiscovery Applications { get; }
    IRecordingDisplayCatalog Displays { get; }
    FrameworkElement CreateSettingsView();
    FrameworkElement CreateRecordingsView();
}

public interface IShellNavigationGuard
{
    bool CanLeave(Window owner);
}

// Optional product capability. The standalone Atlas host does not implement it.
public interface IShellWorkSessionsHost
{
    FrameworkElement CreateWorkSessionsView(Window owner);
    Task StartWorkSessionAsync(Window owner, RecordingDisplayScope? displayScope = null);
    Task<bool> TryCloseWorkSessionsAsync(Window owner);
}

public interface IShellWorkflowRunHost
{
    Task RunWorkflowAsync(Window owner);
}
