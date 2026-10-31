using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Runtime.InteropServices;
using UIMaps.Application.Core;

namespace UIMaps.Application.Views;

public partial class MapsView : UserControl, UiAtlas.Shell.IShellBackNavigation
{
    private readonly IUiMapsCapabilityLauncher _launcher;
    private readonly IRecordableApplicationDiscovery _applicationDiscovery;
    private CancellationTokenSource? _load;
    private bool _loaded;
    private MapApplicationPresentation? _completedRecording;
    private string? _selectedApplicationRef;
    private bool _detailsShowArchived;
    private Window? _owner;

    public MapsView(
        IUiMapsCapabilityLauncher launcher,
        IMapsLibrarySource source,
        IRecordableApplicationDiscovery? applicationDiscovery = null)
    {
        _launcher = launcher;
        _applicationDiscovery = applicationDiscovery ?? new RecordableApplicationDiscovery();
        ViewModel = new MapsLibraryViewModel(source);
        DataContext = ViewModel;
        InitializeComponent();
        Loaded += MapsView_Loaded;
        Unloaded += MapsView_Unloaded;
    }

    internal MapsLibraryViewModel ViewModel { get; }
    public event EventHandler<CapabilityLaunchResult>? LaunchCompleted;
    public event EventHandler? CanGoBackChanged;
    public string? InitialApplicationRef { get; init; }
    public bool AllowRecording { get; init; } = true;
    public bool CanGoBack => !string.IsNullOrWhiteSpace(_selectedApplicationRef);

    private async void MapsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _load = new CancellationTokenSource();
        _owner = Window.GetWindow(this);
        if (_owner is not null) _owner.Activated += OwnerActivated;
        await ViewModel.RefreshAsync(_load.Token);
        if (InitialApplicationRef is { } applicationRef && ViewModel.FindApplication(applicationRef, false) is { } application)
            ShowApplicationDetails(application);
    }

    private void MapsView_Unloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        if (_owner is not null) _owner.Activated -= OwnerActivated;
        _owner = null;
        _load?.Cancel();
        _load?.Dispose();
        _load = null;
    }
    private async void NewMap_Click(object sender, RoutedEventArgs e) => await StartNewMapRecordingAsync();
    private async void OwnerActivated(object? sender, EventArgs args)
    {
        await ViewModel.RefreshAsync(_load?.Token ?? CancellationToken.None);
        RefreshSelectedApplication();
    }

    internal async Task StartNewMapRecordingAsync(
        bool standalonePicker = false,
        RecordingDisplayScope? displayScope = null)
    {
        var owner = Window.GetWindow(this);
        var dialog = new NewUiMapWindow(_applicationDiscovery, displayScope);
        if (!standalonePicker)
            dialog.Owner = owner;
        if (dialog.ShowDialog() != true || dialog.SelectedTarget is not { } target)
            return;

        await RunUiMapRecordingAsync(target);
    }

    private async Task RunUiMapRecordingAsync(RecordableApplicationTarget target)
    {
        var owner = Window.GetWindow(this);

        CapabilityLaunchResult result;
        try
        {
            owner?.Hide();
            ActivateTargetWindow(target.WindowHandle);
            result = await _launcher.RunUiMapRecordingAsync(target with
            {
                RecordingDiagnosticsEnabled = owner is UiAtlas.Shell.ShellWindow shell && shell.ActiveProfileId == ExperienceProfileId.Diagnostics,
            }, CancellationToken.None);
        }
        finally
        {
            owner?.Show();
            owner?.Activate();
        }

        if (result.Cancelled)
            return;

        if (!result.Success)
        {
            Launch(result);
            return;
        }

        await ViewModel.RefreshAsync(_load?.Token ?? CancellationToken.None);
        if (!ViewModel.LastRefreshSucceeded)
            return;
        var completed = result.MapDraftRefs is { Count: > 0 } draftRefs
            ? ViewModel.FindTransferredApplication(draftRefs) : null;
        if (completed is null)
        {
            Launch(new CapabilityLaunchResult(false, "The recording process finished, but its saved draft was not present in the Map library yet. Refresh Maps to retry."));
            return;
        }
        RecordingCompletePanel.DataContext = completed;
        _completedRecording = completed;
        RecordingCompletePanel.Visibility = Visibility.Visible;
    }

    private void ApplicationContextMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MapApplicationPresentation application } anchor)
            PrepareApplicationContextMenu(anchor, application);
    }

    private void ApplicationMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MapApplicationPresentation application, ContextMenu: { } menu } anchor) return;
        PrepareApplicationContextMenu(anchor, application);
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void PrepareApplicationContextMenu(FrameworkElement anchor, MapApplicationPresentation application)
    {
        var menu = anchor.ContextMenu;
        if (menu is null) return;
        menu.DataContext = application;
        RecordableApplicationTarget? target = null;
        string? discoveryError = null;
        try
        {
            target = RunningApplicationMatcher.Find(application, _applicationDiscovery.Discover());
        }
        catch (Exception ex)
        {
            discoveryError = ex.GetBaseException().Message;
        }
        menu.Tag = target;
        var record = menu.Items.OfType<MenuItem>().First(value => Equals(value.Tag, "RecordNewMap"));
        record.Visibility = AllowRecording ? Visibility.Visible : Visibility.Collapsed;
        record.IsEnabled = target is not null;
        record.ToolTip = target is not null
            ? $"Record the launched {application.DisplayName} window."
            : discoveryError is null
                ? "Launch the application and make its window visible to record a new map."
                : $"Running applications could not be checked · {discoveryError}";
        menu.Items.OfType<MenuItem>().First(value => Equals(value.Tag, "ArchiveApplication")).Visibility = application.IsArchived ? Visibility.Collapsed : Visibility.Visible;
        menu.Items.OfType<MenuItem>().First(value => Equals(value.Tag, "RestoreApplication")).Visibility = application.IsArchived ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ContextOpenApplication_Click(object sender, RoutedEventArgs e)
    {
        if (ContextMenuFor(sender)?.DataContext is MapApplicationPresentation application)
            ShowApplicationDetails(application);
    }

    private async void ContextRecordNewMap_Click(object sender, RoutedEventArgs e)
    {
        if (ContextMenuFor(sender)?.Tag is RecordableApplicationTarget target)
            await RunUiMapRecordingAsync(target);
    }

    private async void ContextArchiveApplication_Click(object sender, RoutedEventArgs e)
    {
        if (ContextMenuFor(sender)?.DataContext is not MapApplicationPresentation application) return;
        if (!MapLockConfirmDialog.ShowConfirmation(
                Window.GetWindow(this),
                "Archive application",
                $"Archive {application.DisplayName}?",
                $"{application.Maps.Count:N0} current UI Map {(application.Maps.Count == 1 ? "recording will" : "recordings will")} move out of your active library.",
                "Future recordings for this application will start fresh. Archives remain available until you purge them in Settings › Storage.",
                "Archive application"))
            return;
        await RunApplicationActionAsync(token => ViewModel.ArchiveApplicationAsync(application, token));
    }

    private async void ContextDeleteApplication_Click(object sender, RoutedEventArgs e)
    {
        if (ContextMenuFor(sender)?.DataContext is not MapApplicationPresentation application) return;
        if (!UiAtlas.Shell.DeleteConfirmationDialog.Confirm(Window.GetWindow(this), "UI Maps", application.DisplayName,
            $"This removes {application.Maps.Count} UI Map entries from this library. Published revisions, source recordings and evidence are retained for existing skills and run history.")) return;
        await RunApplicationActionAsync(token => ViewModel.DeleteApplicationAsync(application, token));
    }
    private void MapMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MapRowPresentation map } anchor) return;
        e.Handled = true;
        ShowMapMenu(anchor, map, PlacementMode.Bottom);
    }
    private void Map_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MapRowPresentation map } anchor) return;
        e.Handled = true; ShowMapMenu(anchor, map, e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint);
    }
    private void ShowMapMenu(FrameworkElement anchor, MapRowPresentation map, PlacementMode placement)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = placement };
        var open = new MenuItem { Header = "Open UI Map" }; open.Click += (_, _) => OpenMapViewer(map.Record); menu.Items.Add(open); menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "Delete UI Map…", Foreground = System.Windows.Media.Brushes.Firebrick };
        delete.Click += async (_, _) =>
        {
            if (!UiAtlas.Shell.DeleteConfirmationDialog.Confirm(Window.GetWindow(this), "UI Map", map.Record.Entry.ApplicationDisplayName + " · " + map.Updated,
                "This removes this UI Map from the library. Published revisions, source recordings and evidence are retained for existing skills and run history.")) return;
            await RunMapActionAsync(token => ViewModel.DeleteAsync(map, token));
        };
        menu.Items.Add(delete); menu.IsOpen = true;
    }
    private async void ContextRestoreApplication_Click(object sender, RoutedEventArgs e)
    {
        if (ContextMenuFor(sender)?.DataContext is MapApplicationPresentation application)
            await RunApplicationActionAsync(token => ViewModel.RestoreApplicationAsync(application, token));
    }

    private static ContextMenu? ContextMenuFor(object sender) =>
        sender is MenuItem item ? ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu : null;
    private void OpenApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MapApplicationPresentation application }) return;
        e.Handled = true;
        ShowApplicationDetails(application);
    }

    private void ShowApplicationDetails(MapApplicationPresentation application)
    {
        _selectedApplicationRef = application.ApplicationRef;
        _detailsShowArchived = application.IsArchived;
        DetailsActiveTab.IsChecked = !_detailsShowArchived;
        DetailsArchiveTab.IsChecked = _detailsShowArchived;
        ShowSelectedApplicationProjection(application);
        ApplicationsPanel.Visibility = Visibility.Collapsed;
        ApplicationDetailsPanel.Visibility = Visibility.Visible;
        CanGoBackChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplicationsActiveTab_Click(object sender, RoutedEventArgs e) => ViewModel.ShowArchived = false;
    private void ApplicationsArchiveTab_Click(object sender, RoutedEventArgs e) => ViewModel.ShowArchived = true;

    private void DetailsActiveTab_Click(object sender, RoutedEventArgs e)
    {
        _detailsShowArchived = false;
        RefreshSelectedApplication();
    }

    private void DetailsArchiveTab_Click(object sender, RoutedEventArgs e)
    {
        _detailsShowArchived = true;
        RefreshSelectedApplication();
    }

    private void PrepareApplicationRecordingAction(MapApplicationPresentation application)
    {
        RecordableApplicationTarget? target = null;
        string? discoveryError = null;
        try
        {
            target = RunningApplicationMatcher.Find(application, _applicationDiscovery.Discover());
        }
        catch (Exception ex)
        {
            discoveryError = ex.GetBaseException().Message;
        }
        RecordNewMapForApplicationButton.Visibility = AllowRecording ? Visibility.Visible : Visibility.Collapsed;
        RecordNewMapForApplicationButton.Tag = target;
        RecordNewMapForApplicationButton.IsEnabled = target is not null;
        RecordNewMapForApplicationButton.ToolTip = target is not null
            ? $"Record the launched {application.DisplayName} window."
            : discoveryError is null
                ? "Launch the application and make its window visible to record a new map."
                : $"Running applications could not be checked · {discoveryError}";
    }

    private async void RecordNewMapForApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecordableApplicationTarget target })
            await RunUiMapRecordingAsync(target);
    }

    public void GoBack()
    {
        if (!CanGoBack) return;
        _selectedApplicationRef = null;
        ApplicationDetailsPanel.DataContext = null;
        ApplicationDetailsPanel.Visibility = Visibility.Collapsed;
        ApplicationsPanel.Visibility = Visibility.Visible;
        CanGoBackChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenArtifact_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MapRowPresentation map }) return;
        OpenMapViewer(map.Record);
    }

    private void OpenMapViewer(MapsLibraryRecord record)
    {
        if (_launcher.OpenMap(record) is { } result)
        {
            Launch(result);
            return;
        }
        var viewer = new MapViewerWindow(record)
        {
            Owner = Window.GetWindow(this),
            WindowState = WindowState.Maximized,
        };
        viewer.Show();
    }

    private async void MarkReady_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MapRowPresentation map })
            await RunMapActionAsync(token => ViewModel.MarkReadyAsync(map, token));
    }

    private async void MarkDraft_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MapRowPresentation map })
            await RunMapActionAsync(token => ViewModel.MarkDraftAsync(map, token));
    }

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MapRowPresentation map })
            await RunMapActionAsync(token => ViewModel.ArchiveAsync(map, token));
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MapRowPresentation map })
            await RunMapActionAsync(token => ViewModel.RestoreAsync(map, token));
    }

    private async void RestoreApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MapApplicationPresentation application }) return;
        await RunMapActionAsync(token => ViewModel.RestoreApplicationAsync(application, token));
        _detailsShowArchived = false;
        DetailsActiveTab.IsChecked = true;
        RefreshSelectedApplication();
    }

    private async void ArchiveAllDrafts_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MapApplicationPresentation application } || application.DraftCount == 0) return;
        var noun = application.DraftCount == 1 ? "draft UI Map" : "draft UI Maps";
        if (!MapLockConfirmDialog.ShowConfirmation(
                Window.GetWindow(this),
                "Archive all drafts",
                "Archive all drafts?",
                $"{application.DraftCount:N0} {noun} for {application.DisplayName} will move out of your active library.",
                "Ready maps will not be affected. Archives remain available until you purge them in Settings › Storage.",
                "Archive drafts"))
            return;
        await RunMapActionAsync(token => ViewModel.ArchiveAllDraftsAsync(application, token));
    }

    private async Task RunMapActionAsync(Func<CancellationToken, Task> action)
    {
        ApplicationDetailsPanel.IsEnabled = false;
        try
        {
            await action(_load?.Token ?? CancellationToken.None);
            RefreshSelectedApplication();
        }
        catch (Exception ex)
        {
            Launch(new CapabilityLaunchResult(false, ex.GetBaseException().Message));
        }
        finally
        {
            ApplicationDetailsPanel.IsEnabled = true;
        }
    }

    private async Task RunApplicationActionAsync(Func<CancellationToken, Task> action)
    {
        ApplicationsPanel.IsEnabled = false;
        try
        {
            await action(_load?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            Launch(new CapabilityLaunchResult(false, ex.GetBaseException().Message));
        }
        finally
        {
            ApplicationsPanel.IsEnabled = true;
        }
    }

    private void RefreshSelectedApplication()
    {
        if (string.IsNullOrWhiteSpace(_selectedApplicationRef)) return;
        var application = ViewModel.FindApplication(_selectedApplicationRef, _detailsShowArchived);
        if (application is null)
        {
            VersionsItemsControl.ItemsSource = null;
            DetailsEmptyState.Visibility = Visibility.Visible;
            ActiveDetailsActions.Visibility = _detailsShowArchived ? Visibility.Collapsed : Visibility.Visible;
            RestoreApplicationButton.Visibility = _detailsShowArchived ? Visibility.Visible : Visibility.Collapsed;
            RestoreApplicationButton.IsEnabled = false;
            return;
        }
        ShowSelectedApplicationProjection(application);
    }

    private void ShowSelectedApplicationProjection(MapApplicationPresentation application)
    {
        ApplicationDetailsPanel.DataContext = application;
        VersionsItemsControl.ItemsSource = application.Versions;
        DetailsEmptyState.Visibility = application.Maps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActiveDetailsActions.Visibility = _detailsShowArchived ? Visibility.Collapsed : Visibility.Visible;
        RestoreApplicationButton.Visibility = _detailsShowArchived ? Visibility.Visible : Visibility.Collapsed;
        RestoreApplicationButton.IsEnabled = application.IsArchived;
        PrepareApplicationRecordingAction(application);
    }
    private async void RecordingReady_Click(object sender, RoutedEventArgs e) =>
        await CompleteRecordingAsync((map, token) => ViewModel.PublishCompletedRecordingAsync(map, token));
    private async void RecordingArchive_Click(object sender, RoutedEventArgs e) =>
        await CompleteRecordingAsync((map, token) => ViewModel.ArchiveCompletedRecordingAsync(map, token));
    private void RecordingDraft_Click(object sender, RoutedEventArgs e) => DismissRecordingCompletion();
    private void RecordingReview_Click(object sender, RoutedEventArgs e)
    {
        var map = _completedRecording?.Maps
            .OrderByDescending(value => value.UpdatedAt)
            .FirstOrDefault();
        if (map is not null) OpenMapViewer(map.Record);
    }

    private async Task CompleteRecordingAsync(Func<MapApplicationPresentation, CancellationToken, Task> action)
    {
        if (_completedRecording is null) return;
        RecordingCompletePanel.IsEnabled = false;
        try
        {
            await action(_completedRecording, CancellationToken.None);
            DismissRecordingCompletion();
        }
        catch (Exception ex)
        {
            Launch(new CapabilityLaunchResult(false, ex.GetBaseException().Message));
        }
        finally
        {
            RecordingCompletePanel.IsEnabled = true;
        }
    }

    private void DismissRecordingCompletion()
    {
        RecordingCompletePanel.Visibility = Visibility.Collapsed;
        RecordingCompletePanel.DataContext = null;
        _completedRecording = null;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAsync(_load?.Token ?? CancellationToken.None);
        RefreshSelectedApplication();
    }
    private void Launch(CapabilityLaunchResult result) => LaunchCompleted?.Invoke(this, result);

    private static void ActivateTargetWindow(long windowHandle)
    {
        var handle = (nint)windowHandle;
        if (handle == 0 || !IsWindow(handle)) return;
        if (IsIconic(handle)) _ = ShowWindowAsync(handle, 9); // SW_RESTORE
        _ = SetForegroundWindow(handle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);
}
