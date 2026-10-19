using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UiAtlas.Catalog;

namespace UiAtlas.Shell.Catalog;

/// <summary>The same recording workflow in Atlas and MapLock; all data work belongs to Atlas.</summary>
public sealed class AtlasRecordingsView : UserControl
{
    private readonly AtlasCatalogClient _client;
    private readonly DataGrid _list = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    private readonly Button _build;
    private readonly Button _cancel;
    private CancellationTokenSource? _job;
    private Window? _owner;
    private bool _refreshing;
    public AtlasRecordingsView(AtlasCatalogClient client)
    {
        _client = client;
        var root = new DockPanel(); var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Recordings", FontSize = 28, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "UI Atlas recordings, shared by both apps.", Margin = new Thickness(0, 8, 0, 8) });
        header.Children.Add(new TextBlock { Text = client.Root, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var actions = new WrapPanel();
        actions.Children.Add(Action("New recording", () => { _status.Text = _client.Start("recording", "start").Message; return Task.CompletedTask; }));
        actions.Children.Add(Action("Refresh", RefreshAsync));
        _build = Action("Build UI Map", BuildAsync); actions.Children.Add(_build);
        actions.Children.Add(Action("Open map", OpenAsync));
        _cancel = Action("Cancel build", () => { _job?.Cancel(); return Task.CompletedTask; }); _cancel.IsEnabled = false; actions.Children.Add(_cancel);
        actions.Children.Add(Action("Import…", ImportAsync));
        actions.Children.Add(Action("Find previous MapLock recordings…", FindLegacyAsync));
        header.Children.Add(actions); header.Children.Add(_status); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        foreach (var (title, property, width) in new[] { ("Application", "ProcessName", 150d), ("Recorded", "StartedUtc", 190d), ("Outcome", "Outcome", 100d), ("Frames", "FrameCount", 65d), ("Recording ID", "Id", 330d) })
            _list.Columns.Add(new DataGridTextColumn { Header = title, Binding = new System.Windows.Data.Binding(property), Width = width });
        root.Children.Add(_list); Content = root;
        Loaded += async (_, _) => { _owner = Window.GetWindow(this); if (_owner is not null) _owner.Activated += OwnerActivated; await RefreshAsync(); };
        Unloaded += (_, _) => { if (_owner is not null) _owner.Activated -= OwnerActivated; _owner = null; _job?.Cancel(); };
    }
    private async void OwnerActivated(object? sender, EventArgs args) => await RefreshAsync();
    private Button Action(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(12, 7, 12, 7) };
        button.Click += async (_, _) => { try { await action(); } catch (Exception error) { _status.Text = error.GetBaseException().Message; } };
        return button;
    }
    private async Task RefreshAsync()
    {
        if (_job is not null || _refreshing) return;
        _refreshing = true;
        try
        {
            var selected = (_list.SelectedItem as CatalogRecording)?.Id;
            var reply = await _client.RequestAsync(new("recordings", _client.Root));
            _list.ItemsSource = reply.Recordings; _list.SelectedItem = reply.Recordings.FirstOrDefault(item => item.Id == selected);
            _status.Text = $"{reply.Recordings.Length} recordings. Select one to build or open its Atlas map.";
        }
        catch (Exception error) { _status.Text = "Could not read recordings: " + error.GetBaseException().Message; }
        finally { _refreshing = false; }
    }
    private Task OpenAsync()
    {
        if (_list.SelectedItem is not CatalogRecording item) throw new InvalidOperationException("Select a recording first.");
        if (item.MapId is null) throw new InvalidOperationException("Build a map from this recording first.");
        _status.Text = _client.Start("map", "open", item.MapId).Message;
        return Task.CompletedTask;
    }
    private async Task BuildAsync()
    {
        if (_job is not null) return;
        if (_list.SelectedItem is not CatalogRecording item) throw new InvalidOperationException("Select a recording first.");
        _job = new(); _build.IsEnabled = false; _cancel.IsEnabled = true;
        try
        {
            _status.Text = $"UI Atlas is building {item.ProcessName} using your saved recording and map-build settings…";
            var reply = await _client.RequestAsync(new("build", _client.Root, RecordingId: item.Id, MapId: item.MapId), _job.Token);
            _status.Text = reply.Message;
            _list.SelectedItem = null; // The next refresh resolves the new map relationship.
        }
        catch (OperationCanceledException) { _status.Text = "Build cancelled. Previous maps and recordings were preserved."; }
        catch (Exception error) { _status.Text = "Build failed: " + error.GetBaseException().Message; }
        finally { _job.Dispose(); _job = null; _build.IsEnabled = true; _cancel.IsEnabled = false; }
        var message = _status.Text; await RefreshAsync(); _status.Text = message;
        _list.SelectedItem = (_list.ItemsSource as CatalogRecording[])?.FirstOrDefault(value => value.Id == item.Id);
    }
    private async Task ImportAsync()
    {
        if (_job is not null) return;
        var dialog = new OpenFileDialog { Title = "Import an Atlas recording or workspace", Filter = "Atlas artifacts (*.mlrec;*.atlasmap)|*.mlrec;*.atlasmap", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await ImportPathAsync(dialog.FileName);
    }
    private async Task ImportPathAsync(string path)
    {
        if (_job is not null) return;
        _job = new();
        string message;
        try { _status.Text = "Importing retained Atlas evidence. Source files will be kept…"; message = (await _client.RequestAsync(new("import", _client.Root, SourcePath: path), _job.Token)).Message; }
        finally { _job.Dispose(); _job = null; }
        await RefreshAsync(); _status.Text = message;
    }
    private async Task FindLegacyAsync()
    {
        var reply = await _client.RequestAsync(new("legacy-inventory", _client.Root));
        if (reply.Imports.Length == 0) { _status.Text = "No retained Atlas artifacts were found in previous MapLock storage. Older MapLock-only maps stay in their original storage."; return; }
        var list = new ListBox { ItemsSource = reply.Imports, DisplayMemberPath = "Path", Margin = new Thickness(0, 12, 0, 12) };
        var panel = new DockPanel { Margin = new Thickness(20) };
        var explanation = new TextBlock { Text = "Select retained Atlas evidence to copy into the shared library. Imports include captured text and screenshots. Source files stay unchanged; identical artifacts are not duplicated.", TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(explanation, Dock.Top); panel.Children.Add(explanation);
        var pick = new Button { Content = "Import selected", Padding = new Thickness(12, 8, 12, 8), HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(pick, Dock.Bottom); panel.Children.Add(pick); panel.Children.Add(list);
        var window = new Window { Title = "Previous MapLock recordings", Content = panel, Width = 850, Height = 460, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        pick.Click += (_, _) => { if (list.SelectedItem is CatalogImportCandidate) window.DialogResult = true; };
        if (window.ShowDialog() == true && list.SelectedItem is CatalogImportCandidate selected) await ImportPathAsync(selected.Path);
    }
}
