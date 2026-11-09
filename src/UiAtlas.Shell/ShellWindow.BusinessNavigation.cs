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

    private void UpdateBreadcrumbs()
    {
        if (BreadcrumbItems is null) return;
        BreadcrumbItems.Children.Clear();
        if (_host is not IShellBusinessUnitHost unit) { BreadcrumbBar.Visibility = Visibility.Collapsed; return; }
        BreadcrumbBar.Visibility = Visibility.Visible;
        NavigationList.Tag = new { Unit = unit.BusinessUnitName, Organization = unit.BusinessOrganizationName };
        Crumb("Home", () => Navigate("platform-organizations"));
        if (_activeRoute == "platform-organizations") return;
        if (unit.BusinessOrganizationName is { } organizationName)
        {
            AddSeparator(); Crumb(organizationName, () => Navigate("platform-home"));
        }
        var scoped = _activeRoute is "platform-unit" or "platform-unit-ontology" or "platform-unit-data" or "platform-unit-plans" or "platform-unit-actions"
            or "skills" or "evaluations" or "simulations" or "organization-maps" or "organization-knowledge" or "organization-about";
        if (scoped || _activeRoute == "platform-home") { AddSeparator(); Crumb("Business units", () => Navigate("platform-home")); }
        if (unit.BusinessUnitName is { } name && scoped)
        {
            AddSeparator(); Crumb(name, () => Navigate("platform-unit"));
        }
        if (_activeRoute != "platform-home")
        {
            AddSeparator();
            BreadcrumbItems.Children.Add(new TextBlock { Text = Navigation.FirstOrDefault(n => n.Route == _activeRoute)?.Label ?? "Home", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 4, 0, 4) });
        }
    }

    private Button Crumb(string text, Action? navigate)
    {
        var button = new Button { Content = text, Padding = new Thickness(4), Margin = new Thickness(0, 0, 4, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = (Brush)FindResource("AccentBrush"), MaxWidth = 360 };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        if (navigate is not null) button.Click += (_, _) => navigate();
        BreadcrumbItems.Children.Add(button); return button;
    }
    private void AddSeparator() => BreadcrumbItems.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0) });
}
