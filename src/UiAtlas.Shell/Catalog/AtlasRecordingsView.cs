using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using UiAtlas.Catalog;

namespace UiAtlas.Shell.Catalog;

/// <summary>The shared recordings library; all data mutations belong to Atlas.</summary>
public sealed partial class AtlasRecordingsView : UserControl
{
    private readonly AtlasCatalogClient _client;
    private CatalogRecording[] _recordings = [];
    private CancellationTokenSource? _job;
    private Window? _owner;
    private bool _refreshing;
    private bool _initialized;
    private bool _changingFilters;
    public AtlasRecordingsView(AtlasCatalogClient client)
    {
        _client = client;
        InitializeComponent();
        ApplicationFilter.ItemsSource = new[] { "All applications" };
        ApplicationFilter.SelectedIndex = 0;
        _initialized = true;
        Loaded += async (_, _) =>
        {
            _owner = Window.GetWindow(this);
            if (_owner is not null) _owner.Activated += OwnerActivated;
            await RefreshAsync();
        };
        Unloaded += (_, _) => { if (_owner is not null) _owner.Activated -= OwnerActivated; _owner = null; _job?.Cancel(); };
    }
    private async void OwnerActivated(object? sender, EventArgs args) => await RefreshAsync();
    private void Scope_Click(object sender, RoutedEventArgs args) => ApplyFilters();
    private void Search_Changed(object sender, TextChangedEventArgs args) => ApplyFilters();
    private void Filter_Changed(object sender, SelectionChangedEventArgs args) => ApplyFilters();
    private void ApplyFilters()
    {
        if (!_initialized || _changingFilters) return;
        var archived = ArchiveTab.IsChecked == true;
        var items = RecordingLibraryItem.Filter(_recordings, archived, SearchBox.Text,
            ApplicationFilter.SelectedIndex > 0 ? ApplicationFilter.SelectedItem as string : null,
            StatusFilter.SelectedIndex, SortFilter.SelectedIndex);
        RecordingList.ItemsSource = items;
        var total = _recordings.Count(item => item.Archived == archived);
        CountText.Text = $"{items.Length} of {total} {(archived ? "archived" : "active")} recordings";
        EmptyMessage.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage.Text = total == 0 ? archived ? "No archived recordings." : "No recordings yet. Import a recording or record a UI Map from Maps."
            : "No recordings match your search and filters.";
    }
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { StatusText.Text = error.GetBaseException().Message; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs args) => await RefreshAsync();
    private async void Import_Click(object sender, RoutedEventArgs args) => await RunAsync(ImportAsync);
    private async void FindLegacy_Click(object sender, RoutedEventArgs args) => await RunAsync(FindLegacyAsync);
    private void Cancel_Click(object sender, RoutedEventArgs args) => _job?.Cancel();
    private void Utilities_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { ContextMenu: { } menu } button) { menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; }
    }
    private async Task RefreshAsync()
    {
        if (_job is not null || _refreshing) return;
        _refreshing = true;
        try
        {
            var reply = await _client.RequestAsync(new("recordings", _client.Root));
            SetRecordings(reply.Recordings);
            StatusText.Text = "";
        }
        catch (Exception error)
        {
            StatusText.Text = "Could not read recordings: " + error.GetBaseException().Message;
            if (_recordings.Length == 0) EmptyMessage.Text = "Recordings could not be loaded. Try Refresh.";
        }
        finally { _refreshing = false; }
    }
    internal void SetRecordings(CatalogRecording[] recordings)
    {
        _recordings = recordings;
        var selected = ApplicationFilter.SelectedItem as string;
        _changingFilters = true;
        try
        {
            ApplicationFilter.ItemsSource = new[] { "All applications" }.Concat(_recordings.Select(item => item.ProcessName)
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.OrdinalIgnoreCase)).ToArray();
            ApplicationFilter.SelectedItem = selected;
            if (ApplicationFilter.SelectedIndex < 0) ApplicationFilter.SelectedIndex = 0;
        }
        finally { _changingFilters = false; }
        ApplyFilters();
    }
    private async void OpenRecording_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: RecordingLibraryItem item }) return;
        if (item.HasMap) await RunAsync(() => OpenAsync(item.Recording));
        else ShowRecordingMenu((Button)sender, item);
    }
    private void RecordingMenu_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: RecordingLibraryItem item } button) ShowRecordingMenu(button, item);
    }
    private void ShowRecordingMenu(Button button, RecordingLibraryItem item)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        void Add(string label, Func<Task> action, bool enabled = true)
        {
            var entry = new MenuItem { Header = label, IsEnabled = enabled };
            entry.Click += async (_, _) => await RunAsync(action);
            menu.Items.Add(entry);
        }
        Add("Open map", () => OpenAsync(item.Recording), item.HasMap);
        Add(item.HasMap ? "Rebuild UI Map" : "Build UI Map", () => BuildAsync(item.Recording), _job is null && !item.IsInvalid && !item.Recording.Archived);
        menu.Items.Add(new Separator());
        Add(item.Recording.Archived ? "Restore recording" : "Archive recording", () => SetArchivedAsync(item.Recording), _job is null);
        Add("Recording details", () =>
        {
            MessageBox.Show(Window.GetWindow(this), item.Details + $"\nFrames: {item.Recording.FrameCount:N0}", "Recording details", MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.CompletedTask;
        });
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }
    private Task OpenAsync(CatalogRecording item)
    {
        if (item.MapId is null) throw new InvalidOperationException("Build a map from this recording first.");
        StatusText.Text = _client.Start("map", "open", item.MapId).Message;
        return Task.CompletedTask;
    }
    private async Task SetArchivedAsync(CatalogRecording item)
    {
        if (_job is not null || _refreshing) return;
        _job = new();
        string message;
        try { message = (await _client.RequestAsync(new(item.Archived ? "recording-restore" : "recording-archive", _client.Root, RecordingId: item.Id), _job.Token)).Message; }
        finally { _job.Dispose(); _job = null; }
        await RefreshAsync(); StatusText.Text = message;
    }
    private async Task BuildAsync(CatalogRecording item)
    {
        if (_job is not null || _refreshing) return;
        _job = new(); CancelButton.Visibility = Visibility.Visible;
        try
        {
            StatusText.Text = $"UI Atlas is building {item.ProcessName} using your saved recording and map-build settings…";
            var reply = await _client.RequestAsync(new("build", _client.Root, RecordingId: item.Id, MapId: item.MapId), _job.Token);
            StatusText.Text = reply.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Build cancelled. Previous maps and recordings were preserved."; }
        catch (Exception error) { StatusText.Text = "Build failed: " + error.GetBaseException().Message; }
        finally { _job.Dispose(); _job = null; CancelButton.Visibility = Visibility.Collapsed; }
        var message = StatusText.Text; await RefreshAsync(); StatusText.Text = message;
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
        try { StatusText.Text = "Importing retained Atlas evidence. Source files will be kept…"; message = (await _client.RequestAsync(new("import", _client.Root, SourcePath: path), _job.Token)).Message; }
        finally { _job.Dispose(); _job = null; }
        await RefreshAsync(); StatusText.Text = message;
    }
    private async Task FindLegacyAsync()
    {
        var reply = await _client.RequestAsync(new("legacy-inventory", _client.Root));
        if (reply.Imports.Length == 0) { StatusText.Text = "No retained Atlas artifacts were found in previous MapLock storage. Older MapLock-only maps stay in their original storage."; return; }
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
