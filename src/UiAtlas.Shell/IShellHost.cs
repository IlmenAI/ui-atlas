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

public interface IShellSettingsSectionNavigation
{
    void ShowRecordingSettings();
    void ShowServerCloudSettings() { }
}

/// <summary>Current-page drill-down navigation for the shared title-bar Back button.</summary>
public interface IShellBackNavigation
{
    bool CanGoBack { get; }
    event EventHandler? CanGoBackChanged;
    void GoBack();
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

public interface IShellSkillsHost
{
    FrameworkElement CreateSkillView(string route);
}

public interface IShellHomeHost
{
    FrameworkElement CreateHomeView(Action<string> navigate);
}

/// <summary>Optional selected business unit, backed by the existing authoring tools.</summary>
public interface IShellBusinessUnitHost
{
    string? BusinessUnitLabel { get; }
    string? BusinessUnitName => BusinessUnitLabel;
    string? BusinessOrganizationName => null;
    void LeaveBusinessUnit();
}

/// <summary>Optional shared-business platform.</summary>
public interface IShellPlatformHost
{
    FrameworkElement CreatePlatformView(string route, Window owner);
}

public interface IShellBusinessBreadcrumbHost
{
    event EventHandler? BusinessContextChanged;
    Task<IReadOnlyList<ShellOrganization>> BusinessOrganizationsAsync();
    Task SelectBusinessOrganizationAsync(string id);
}

public sealed record ShellOrganization(string Id, string Name);
/// <summary>Optional MapLock workspace; standalone Atlas retains its existing navigation.</summary>
public interface IShellOrganizationHost
{
    bool CanLeaveOrganizationView(FrameworkElement view, Window owner) => true;
    string? OrganizationId { get; }
    IReadOnlyList<ShellOrganization> Organizations();
    event EventHandler? OrganizationChanged;
    void SelectOrganization(string? id);
    FrameworkElement CreateOrganizationView(string route, Window owner, Action<string> navigate);
}

/// <summary>Optional chat entry point supplied by MapLock.</summary>
public interface IShellAssistantHost
{
    Task OpenAssistantAsync(Window owner);
}
