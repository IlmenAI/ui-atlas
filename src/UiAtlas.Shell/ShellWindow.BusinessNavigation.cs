using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace UiAtlas.Shell;

public partial class ShellWindow
{
    private readonly Stack<string> _routeBack = new();
    private bool _goingBack;

    private string? _workspaceSettingsSection;
    public void SetWorkspaceSettingsSection(string name)
    {
        _workspaceSettingsSection = name; if (_activeRoute == "settings") UpdateBreadcrumbs();
    }

    private void UpdateBreadcrumbs()
    {
        if (BreadcrumbItems is null) return;
        BreadcrumbItems.Children.Clear();
        if (_host is not IShellBusinessUnitHost unit) { BreadcrumbBar.Visibility = Visibility.Collapsed; return; }
        BreadcrumbBar.Visibility = Visibility.Visible;
        Resources["WorkspaceScopeName"] = (_activeRoute == "settings" ? "Settings" :
            IsSharedTool(_activeRoute) || _activeRoute == "platform-shared" ? "Shared resources" :
            unit.BusinessUnitName ?? unit.BusinessOrganizationName ?? "Workspace").ToUpperInvariant();
        NavigationList.Tag = new { Unit = unit.BusinessUnitName, Organization = unit.BusinessOrganizationName };
        Crumb("Home", () => Navigate("platform-overview"));
        if (_activeRoute == "platform-overview") return;
        if (_activeRoute == "settings")
        {
            AddSeparator(); Crumb("Settings", () => Navigate("settings"));
            if (_workspaceSettingsSection is { } section) { AddSeparator(); CurrentCrumb(section); }
            return;
        }
        AddSeparator(); Crumb("Organizations", () => Navigate("platform-organizations"));
        if (_activeRoute == "platform-organizations") return;
        if (unit.BusinessOrganizationName is { } organizationName)
        {
            AddSeparator(); Crumb(organizationName, () => Navigate("platform-organization"));
            if (_activeRoute == "platform-organization") return;
        }
        var scoped = _activeRoute is "platform-unit" or "platform-unit-ontology" or "platform-unit-data" or "platform-unit-plans" or "platform-unit-actions"
            or "skills" or "evaluations" or "simulations" or "organization-maps" or "organization-knowledge" or "organization-about";
        if (_activeRoute == "platform-home") { AddSeparator(); Crumb("Business units", () => Navigate("platform-home")); }
        if (unit.BusinessUnitName is { } name && scoped)
        {
            AddSeparator(); Crumb(name, () => Navigate("platform-unit"));
        }
        if (IsSharedTool(_activeRoute) && unit.BusinessOrganizationName is not null)
        {
            AddSeparator(); Crumb("Shared resources", () => Navigate("platform-shared"));
        }
        if (_activeRoute is not ("platform-home" or "platform-unit"))
        {
            AddSeparator();
            BreadcrumbItems.Children.Add(new TextBlock { Text = Navigation.FirstOrDefault(n => n.Route == _activeRoute)?.Label ?? "Home", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 4, 0, 4) });
        }
    }

    private void CurrentCrumb(string text) => BreadcrumbItems.Children.Add(new TextBlock
    {
        Text = text, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 0, 0)
    });

    private Button Crumb(string text, Action? navigate)
    {
        var button = global::UiAtlas.Presentation.Appearance.Bind(new Button { Content = text, Padding = new Thickness(4), Margin = new Thickness(0, 0, 4, 0), BorderThickness = new Thickness(0), MaxWidth = 360 },("Background", "TransparentBrush"),("Foreground", "AccentBrush"));
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        if (navigate is not null) button.Click += (_, _) => navigate();
        BreadcrumbItems.Children.Add(button); return button;
    }
    private void AddSeparator() => BreadcrumbItems.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0) });
}
