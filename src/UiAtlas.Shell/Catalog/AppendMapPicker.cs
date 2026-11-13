using System.Windows;
using System.Windows.Controls;
using UiAtlas.Catalog;
using UIMaps.Protocol;
using UIMaps.Protocol.V3;

namespace UiAtlas.Shell.Catalog;

internal sealed class AppendMapPicker : Window
{
    internal sealed record Choice(CatalogMap Map, string Label)
    {
        public override string ToString() => Label;
    }
    internal static Choice[] Candidates(IEnumerable<CatalogMap> maps, string executable) => maps
        .Where(m => !string.IsNullOrEmpty(m.Generation))
        .Select(m => (Map: m, Entry: MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(m.Entry))))
        .Where(v => !v.Entry.Archived && v.Entry.MapDraftRef.Length > 0 && v.Map.Application is { } app && WindowsExecutableIdentity.ProcessName(app.Executable)
            .Equals(WindowsExecutableIdentity.ProcessName(executable), StringComparison.OrdinalIgnoreCase))
        .Select(v => new Choice(v.Map, v.Entry.ApplicationDisplayName + " · " + v.Map.Id + "\n"
            + (v.Entry.IsReady ? "Ready" : "Draft") + " · Updated "
            + (DateTimeOffset.TryParse(v.Entry.UpdatedAt, out var date) ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "unknown")
            + " · " + v.Map.Application!.ApplicationVersion + " · " + v.Map.Application.WindowTitle)).ToArray();

    public CatalogMap? SelectedMap { get; private set; }
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly Button _add, _retry;
    private readonly string _executable;
    private bool _closed;
    private int _loadGeneration;
    internal Task Loading { get; private set; } = Task.CompletedTask;
    private Func<Task<CatalogMap[]>>? _load;
    internal AppendMapPicker(string executable, Func<Task<CatalogMap[]>> load) : this([], executable)
    {
        _load = load; _status.Text = "Loading compatible maps…";
        Loaded += (_, _) => Loading = LoadMapsAsync();
    }
    internal async Task LoadMapsAsync()
    {
        var generation = ++_loadGeneration;
        _list.ItemsSource = null; _retry.IsEnabled = false; _add.IsEnabled = false; _status.Text = "Loading compatible maps…";
        try { var maps = await _load!(); if (!_closed && generation == _loadGeneration) SetMaps(maps); }
        catch (Exception error) { if (!_closed && generation == _loadGeneration) _status.Text = "Could not load maps: " + error.GetBaseException().Message + " Retry, or cancel and return to your recording."; }
        finally { if (!_closed && generation == _loadGeneration) _retry.IsEnabled = true; }
    }
    private void SetMaps(IEnumerable<CatalogMap> maps)
    {
        var choices = Candidates(maps, _executable); _list.ItemsSource = choices;
        _status.Text = choices.Length == 0 ? "No compatible active maps. Create or restore a map for this application first." : "Select a destination, then add the recorded observations.";
    }
    internal AppendMapPicker(IEnumerable<CatalogMap> maps, string executable)
    {
        _executable = executable;
        Title = "Add observations to an existing map"; Width = 680; Height = 520;
        MinWidth = 500; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;
        var layout = new DockPanel { Margin = new Thickness(24) }; Content = layout;
        var heading = new TextBlock { Text = "Choose the destination map", FontSize = 24, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var help = new TextBlock { Text = "Choose the map for the same application and version. Observations will be added as a draft. Existing tasks keep their current map.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(help, Dock.Top); layout.Children.Add(help);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(18, 8, 18, 8) };
        var add = _add = new Button { Content = "Add observations", IsEnabled = false, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(12, 0, 0, 0) };
        footer.Children.Add(cancel); footer.Children.Add(add);
        _retry = new Button { Content = "Refresh maps", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(12, 0, 0, 0) };
        _retry.Click += (_, _) => { if (_load is not null) Loading = LoadMapsAsync(); };
        footer.Children.Insert(0, _retry);
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(_status, Dock.Top); layout.Children.Add(_status);
        _list = new ListBox { DisplayMemberPath = nameof(Choice.Label) };
        System.Windows.Automation.AutomationProperties.SetName(_list, "Destination maps");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_status, System.Windows.Automation.AutomationLiveSetting.Polite);
        _list.SelectionChanged += (_, _) => add.IsEnabled = _list.SelectedItem is Choice;
        add.Click += (_, _) => { if (_list.SelectedItem is Choice choice) { SelectedMap = choice.Map; DialogResult = true; } };
        layout.Children.Add(_list); SetMaps(maps);
        Closed += (_, _) => _closed = true;
    }
}
