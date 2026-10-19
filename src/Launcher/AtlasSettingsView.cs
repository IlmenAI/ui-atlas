using System.Windows;
using System.Windows.Controls;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Shell;
using UIMaps.Application.Core;

namespace UiAtlas.Launcher;

public sealed class AtlasSettingsView : UserControl, IShellNavigationGuard
{
    private readonly AtlasSettingsWindow _editor = new(embedded: true);
    public AtlasSettingsView(LauncherPreferences preferences)
    {
        var root = new DockPanel();
        var startup = new StackPanel { Margin = new Thickness(24, 0, 24, 12) };
        startup.Children.Add(new TextBlock { Text = "Workspace on startup", FontWeight = FontWeights.SemiBold });
        var choice = new ComboBox { ItemsSource = new[] { "UI Maps", "Diagnostics" }, SelectedIndex = preferences.Load() == ExperienceProfileId.Diagnostics ? 1 : 0, Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        choice.SelectionChanged += (_, _) =>
        {
            try { preferences.Save(choice.SelectedIndex == 1 ? ExperienceProfileId.Diagnostics : ExperienceProfileId.Maps); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Could not save startup preference"); }
        };
        startup.Children.Add(choice);
        startup.Children.Add(new TextBlock { Text = "Applies to the next desktop launch. Changing workspaces does not change this preference." });
        startup.Children.Add(new TextBlock { Text = "Shared recording and map library: " + UiAtlas.Catalog.AtlasCatalogLocation.Resolve(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        DockPanel.SetDock(startup, Dock.Top); root.Children.Add(startup);
        var editor = (UIElement)_editor.Content;
        _editor.Content = null;
        root.Children.Add(editor);
        Content = root;
    }
    public bool CanLeave(Window owner) => _editor.ConfirmDiscard(owner);
}
