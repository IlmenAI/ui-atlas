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
        NavigationList.Tag = unit.BusinessUnitName;
        var organization = Crumb(unit.BusinessOrganizationName ?? "Organizations", null);
        if (_host is IShellBusinessBreadcrumbHost context)
        {
            organization.Content += "  ▾";
            organization.Click += async (_, _) =>
            {
                organization.IsEnabled = false;
                try
                {
                    var menu = new ContextMenu { PlacementTarget = organization };
                    foreach (var org in await context.BusinessOrganizationsAsync())
                    {
                        var choice = new MenuItem { Header = org.Name };
                        choice.Click += async (_, _) =>
                        {
                            if (!CanLeaveCurrentPage()) return;
                            try { await context.SelectBusinessOrganizationAsync(org.Id); Navigate("platform-home"); }
                            catch (Exception error) { MessageBox.Show(this, error.GetBaseException().Message, "Organization", MessageBoxButton.OK, MessageBoxImage.Information); }
                        };
                        menu.Items.Add(choice);
                    }
                    menu.Items.Add(new Separator());
                    var manage = new MenuItem { Header = "Manage organizations…" };
                    manage.Click += (_, _) => Navigate("platform-home"); menu.Items.Add(manage);
                    organization.ContextMenu = menu; menu.IsOpen = true;
                }
                catch (Exception error) { MessageBox.Show(this, error.GetBaseException().Message, "Organizations", MessageBoxButton.OK, MessageBoxImage.Information); }
                finally { organization.IsEnabled = true; }
            };
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
