using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace UiAtlas.Presentation;

/// <summary>The same General appearance editor in both product hosts.</summary>
public sealed partial class AppearanceSettingsPanel : StackPanel
{
    private bool _syncing;

    public AppearanceSettingsPanel()
    {
        InitializeComponent();
        // A local type in this XAML forces a second markup pass for the theme assembly.
        PaletteIconHost.Child = new WorkspaceIcon { Kind = "palette", Width = 17, Height = 17 };
        ThemeList.ItemsSource = ThemePreview.All;
        Sync();
        ThemeList.SelectionChanged += Save;
        ModeList.SelectionChanged += Save;
        Loaded += (_, _) => { Appearance.Changed += Changed; Sync(); };
        Unloaded += (_, _) => Appearance.Changed -= Changed;
    }

    private void ThemeGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Use the space available inside Settings, which can be narrower than the host window.
        ((UniformGrid)sender).Columns = Math.Clamp((int)(e.NewSize.Width / 180), 1, 3);
    }

    private void Sync()
    {
        var value = Appearance.Current;
        _syncing = true;
        try
        {
            ThemeList.SelectedValue = value.ThemeId;
            ModeList.SelectedValue = value.ColorMode;
        }
        finally { _syncing = false; }
    }

    private void Changed(AppearancePreference preference)
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Changed(preference)); return; }
        if (IsLoaded) Sync();
    }

    private void Save(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (((ListBox)sender).SelectedValue is not string value) { Sync(); return; }
        var current = Appearance.Current;
        try
        {
            Appearance.SetPreference(sender == ThemeList
                ? current with { ThemeId = value }
                : current with { ColorMode = value });
            SaveError.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            Sync();
            SaveError.Text = "Could not save appearance: " + error.Message;
            SaveError.Visibility = Visibility.Visible;
        }
    }
}
