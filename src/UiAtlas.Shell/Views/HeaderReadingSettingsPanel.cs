using System.Windows;
using System.Windows.Controls;
using UiAtlas.Catalog;
using UiAtlas.Shell.Catalog;

namespace UiAtlas.Shell.Views;

/// <summary>Selects a shared AI connection; this view never owns credentials.</summary>
public sealed class HeaderReadingSettingsPanel : UserControl
{
    private readonly AtlasCatalogClient _atlas;
    private readonly Func<CatalogRequest, Task<CatalogReply>> _request;
    private readonly Func<Task>? _refreshProviders;
    private readonly ComboBox _provider = new() { ItemsSource = new[] { "Local OCR", "OpenAI", "Azure OpenAI" }, SelectedIndex = 0, MinHeight = 40 };
    private readonly CheckBox _automatic = new() { Content = "Automatically analyze table headers and suggest a data grid name", Margin = new Thickness(0, 20, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _save = new() { Content = "Save header reading settings", Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Left };
    private bool _loaded, _busy;
    public HeaderReadingSettingsPanel(AtlasCatalogClient atlas, Func<CatalogRequest, Task<CatalogReply>>? request = null, Func<Task>? refreshProviders = null)
    {
        _atlas = atlas; _request = request ?? (value => atlas.RequestAsync(value)); _refreshProviders = refreshProviders;
        var root = new StackPanel { Margin = new Thickness(24, 12, 24, 18), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        Add(root, "Table header reading", 19);
        Add(root, "Choose Local OCR or a connection already saved in AI providers. Models, endpoints and keys are managed there.");
        root.Children.Add(_provider); root.Children.Add(_automatic);
        Add(root, "After you explore a table, prepare its schema and name automatically. Explore details lets you review headers and column boundaries before saving. Partial captures stay marked as partial.");
        root.Children.Add(_status); root.Children.Add(_save); Content = root;
        System.Windows.Automation.AutomationProperties.SetName(_provider, "Header reading provider");
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await LoadAsync(); } };
        _save.Click += async (_, _) => await SaveAsync();
    }
    private static void Add(Panel panel, string text, double size = 13) => panel.Children.Add(new TextBlock
        { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6) });
    private async Task LoadAsync()
    {
        try
        {
            _save.IsEnabled = false;
            if (_refreshProviders is not null) await _refreshProviders();
            var settings = (await _request(new("header-settings-read", _atlas.Root))).HeaderSettings
                ?? throw new System.IO.IOException("Rebuild the paired Atlas tools to configure header reading.");
            _provider.SelectedIndex = !settings.Enabled ? 0 : settings.Provider == "openai" ? 1 : 2;
            _automatic.IsChecked = settings.AutoAnalyze;
            _status.Text = settings.Enabled && !settings.HasKey ? "Configure the selected connection in AI providers." : "";
        }
        catch (Exception error) { _status.Text = error.Message; _loaded = false; }
        finally { _save.IsEnabled = true; }
    }
    private async Task SaveAsync()
    {
        if (_busy) return; _busy = true; _save.IsEnabled = false;
        try
        {
            if (_refreshProviders is not null) await _refreshProviders();
            var reply = await _request(new("header-settings-save", _atlas.Root, HeaderSettingsUpdate:
                new(_provider.SelectedIndex != 0, "", "", Provider: _provider.SelectedIndex switch { 1 => "openai", 2 => "azure-openai", _ => "local" }, AutoAnalyze: _automatic.IsChecked == true)));
            if (!reply.Success) throw new InvalidOperationException(reply.Message);
            _status.Text = "Settings saved. The next table exploration will use these options.";
        }
        catch (Exception error) { _status.Text = error.Message; }
        finally { _busy = false; _save.IsEnabled = true; }
    }
}
