using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UiAtlas.Presentation;

/// <summary>The same General appearance editor in both product hosts.</summary>
public sealed class AppearanceSettingsPanel : StackPanel
{
    private readonly ComboBox _theme = new() { MinWidth = 210, DisplayMemberPath = "Label", SelectedValuePath = "Id" };
    private readonly ComboBox _mode = new() { MinWidth = 120, ItemsSource = new[] { "Light", "Dark" } };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16) };
    private bool _loading;
    private bool _listening;

    public AppearanceSettingsPanel()
    {
        Children.Add(new TextBlock { Text = "Appearance", FontSize = 18, FontWeight = FontWeights.SemiBold });
        _theme.ItemsSource = AppearancePreference.Themes.Select(t => new ThemeChoice(t.Id, t.Label)).ToArray();
        AutomationProperties.SetName(_theme, "Theme");
        AutomationProperties.SetName(_mode, "Color mode");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(Field("Theme", _theme)); row.Children.Add(Field("Color mode", _mode));
        Children.Add(row); Children.Add(_status);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedInkBrush");
        Sync();
        _theme.SelectionChanged += Save; _mode.SelectionChanged += Save;
        Loaded += (_, _) => { if (!_listening) { Appearance.Changed += Changed; _listening = true; } Sync(); };
        Unloaded += (_, _) => { Appearance.Changed -= Changed; _listening = false; };
    }

    private static FrameworkElement Field(string text, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        panel.Children.Add(new Label { Content = text, Target = control, Padding = new Thickness(0, 0, 0, 5) });
        panel.Children.Add(control); return panel;
    }
    private void Sync()
    {
        var value = Appearance.Current;
        _loading = true;
        _theme.SelectedValue = value.ThemeId; _mode.SelectedIndex = value.ColorMode == "dark" ? 1 : 0;
        _loading = false;
    }
    private void Changed(AppearancePreference value)
    {
        if (!_listening || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Changed(value)); return; }
        Sync();
    }
    private void Save(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _theme.SelectedValue is not string id || _mode.SelectedIndex < 0) return;
        try
        {
            Appearance.SetPreference(new(id, _mode.SelectedIndex == 1 ? "dark" : "light"));
            _status.Text = "Saved. Applies to MapLock, UI Atlas and open companion windows. Windows High Contrast takes precedence.";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            Sync(); _status.Text = "Could not save appearance: " + error.Message;
        }
    }
    private sealed record ThemeChoice(string Id, string Label);
}
