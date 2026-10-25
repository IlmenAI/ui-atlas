using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using UIMaps.Application.Core;
using UIMaps.Application.Views;
using UIMaps.Application;

namespace UiAtlas.Shell;

public partial class ShellWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private readonly IUiMapsCapabilityLauncher _launcher;
    private readonly IShellHost _host;
    private readonly IRecordingDisplayCatalog _displayCatalog;
    private readonly IRecordableApplicationDiscovery _applicationDiscovery;
    private readonly ExperienceProfileCatalog _profiles;
    private ExperienceProfileDefinition _activeProfile = null!;
    private bool _changingProfile;
    private string _activeRoute = "home";
    private MicroHudWindow? _microHud;
    private RecordingDisplayScope? _activeRecordingDisplayScope;
    private bool _suppressHudCloseRestore;
    private WindowState _windowStateBeforeHud;
    private Rect _normalBoundsBeforeHud;
    private FrameworkElement? _settingsView;
    private bool _workClosing, _workCloseApproved;
    private MapsView? _mapsView;
    private DiagnosticsHubView? _diagnosticsHubView;
    private HwndSource? _windowSource;

    public ShellWindow(IShellHost host)
    {
        _host = host;
        _launcher = host.Launcher;
        _displayCatalog = host.Displays;
        _applicationDiscovery = host.Applications;
        _profiles = host.Profiles;
        _activeRoute = host.StartupRoute;
        InitializeComponent();
        Title = host.ProductName;
        ProductTitle.Text = host.ProductName;
        ProductLogo.Wordmark = host.ProductName.ToUpperInvariant();
        ProfileSelector.ItemsSource = _profiles.Profiles;
        var startupMode = _profiles.GetDefinition(host.StartupProfile).Option.Id;
        _changingProfile = true;
        ProfileSelector.SelectedItem = _profiles.Profiles.Single(option => option.Id == startupMode);
        _changingProfile = false;
        ApplyProfile(startupMode);
        Closing += (_, args) =>
        {
            if (_settingsView is IShellNavigationGuard guard && !guard.CanLeave(this)) args.Cancel = true;
        };
        Closing += async (_, args) =>
        {
            if (args.Cancel || _workCloseApproved || _host is not IShellWorkSessionsHost work) return;
            args.Cancel = true;
            if (_workClosing) return;
            _workClosing = true;
            try
            {
                if (await work.TryCloseWorkSessionsAsync(this)) { _workCloseApproved = true; Close(); }
            }
            finally { _workClosing = false; }
        };
    }

    public event EventHandler? ProfileChanged;
    public void Navigate(string route)
    {
        var item = _activeProfile.Navigation.FirstOrDefault(value => value.Route == route && value.IsAvailable)
            ?? throw new ArgumentException("This workspace does not expose that page.", nameof(route));
        SelectNavigation(item);
    }

    public ExperienceProfileId ActiveProfileId => _activeProfile.Option.Id;
    public string ActiveRoute => _activeRoute;
    internal MicroHudWindow? ActiveMicroHud => _microHud;
    internal RecordingDisplayScope? ActiveRecordingDisplayScope => _activeRecordingDisplayScope;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(MainWindowWindowProc);
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(MainWindowWindowProc);
        _windowSource = null;
        _suppressHudCloseRestore = true;
        _microHud?.Close();
        base.OnClosed(e);
    }

    private static nint MainWindowWindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo || lParam == 0)
            return 0;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == 0)
            return 0;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return 0;

        var maximizeBounds = WindowMaximizeBounds.Calculate(
            monitorInfo.Monitor.ToRectangle(),
            monitorInfo.WorkArea.ToRectangle());
        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMaxInfo.MaxPosition.X = maximizeBounds.Position.X;
        minMaxInfo.MaxPosition.Y = maximizeBounds.Position.Y;
        minMaxInfo.MaxSize.X = maximizeBounds.Size.Width;
        minMaxInfo.MaxSize.Y = maximizeBounds.Size.Height;
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
        handled = true;
        return 0;
    }

    private void ApplyProfile(ExperienceProfileId requestedProfile)
    {
        _activeProfile = _profiles.GetDefinition(requestedProfile);
        NavigationList.ItemsSource = _activeProfile.Navigation;

        var retained = _activeProfile.Navigation.FirstOrDefault(item =>
            item.IsAvailable && string.Equals(item.Route, _activeRoute, StringComparison.Ordinal));
        SelectNavigation(retained ?? _activeProfile.Navigation.First(item => item.IsAvailable));
    }

    private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingProfile || ProfileSelector.SelectedItem is not ExperienceProfileOption selected)
            return;

        if (!selected.IsAvailable)
        {
            _changingProfile = true;
            ProfileSelector.SelectedItem = _activeProfile.Option;
            _changingProfile = false;
            return;
        }

        if (_activeRoute == "settings" && _settingsView is IShellNavigationGuard guard && !guard.CanLeave(this))
        {
            _changingProfile = true; ProfileSelector.SelectedItem = _activeProfile.Option; _changingProfile = false;
            return;
        }
        _activeRoute = _profiles.GetDefinition(selected.Id).LandingRoute ?? _activeRoute;
        ApplyProfile(selected.Id);
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavigationList.SelectedItem is not ExperienceNavigationItem item || !item.IsAvailable)
            return;

        ActivateNavigation(item);
    }

    private void ActivateNavigation(ExperienceNavigationItem item)
    {
        if (_activeRoute == "settings" && item.Route != "settings" && _settingsView is IShellNavigationGuard guard && !guard.CanLeave(this))
        {
            NavigationList.SelectedItem = _activeProfile.Navigation.First(value => value.Route == "settings");
            return;
        }
        _activeRoute = item.Route;
        switch (item.Route)
        {
            case "work-sessions" when _host is IShellWorkSessionsHost work:
                ShowPage(work.CreateWorkSessionsView(this));
                break;
            case "recordings":
                ShowPage(_host.CreateRecordingsView());
                break;
            case "home":
                ShowHome("Home");
                break;
            case "maps":
                _mapsView ??= CreateMapsView();
                ShowPage(_mapsView);
                break;
            case "recents":
                ShowPage(new PlaceholderView("Recents", "Recent UI Map activity will appear here when a product-level activity contract is available.", "\uE823"));
                break;
            case "settings":
                _settingsView ??= _host.CreateSettingsView();
                ShowPage(_settingsView);
                break;
            case "diagnostics":
                _diagnosticsHubView ??= CreateDiagnosticsHubView();
                ShowPage(_diagnosticsHubView);
                break;
        }
    }

    private void ShowHome(string heading)
    {
        var home = new HomeView(heading, _host is IShellWorkSessionsHost);
        home.OpenUiMapRequested += (_, _) => NavigateToMaps();
        ShowPage(home);
    }

    private MapsView CreateMapsView()
    {
        var view = new MapsView(_launcher, _host.Maps, _applicationDiscovery);
        view.LaunchCompleted += (_, result) => ReportLaunch(result);
        view.CanGoBackChanged += (_, _) => UpdateBackButton();
        return view;
    }

    private DiagnosticsHubView CreateDiagnosticsHubView()
    {
        var view = new DiagnosticsHubView(_applicationDiscovery, _launcher.SupportsFullDiagnosticsWorkspace);
        view.ExploreApplicationRequested += (_, args) =>
        {
            var result = _launcher.OpenAmbientDiagnostics(args.Target);
            ReportLaunch(result);
            if (result.Success)
                WindowState = WindowState.Minimized;
        };
        view.OpenFullDiagnosticsRequested += (_, _) => ReportLaunch(_launcher.OpenDiagnostics());
        return view;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeRoute == "maps" && _mapsView?.CanGoBack == true)
            _mapsView.GoBack();
        UpdateBackButton();
    }

    private void UpdateBackButton()
    {
        if (BackButton is not null)
            BackButton.Visibility = _activeRoute == "maps" && _mapsView?.CanGoBack == true
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private string ReportLaunch(CapabilityLaunchResult result)
    {
        if (!result.Success && !result.Cancelled)
            MessageBox.Show(this, result.Message, _host.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
        return result.Message;
    }

    private void MicroHudButton_Click(object sender, RoutedEventArgs e)
    {
        if (_microHud is { IsVisible: true })
        {
            _microHud.Activate();
            return;
        }

        var scopes = _displayCatalog.List(new WindowInteropHelper(this).Handle);
        if (scopes.Count == 0)
        {
            MessageBox.Show(this, "No display is available for the Micro HUD.", _host.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (scopes.Count == 1)
        {
            OpenMicroHud(scopes[0]);
            return;
        }

        ShowDisplayScopeMenu(scopes);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeRestoreGlyph is not null)
            MaximizeRestoreGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    internal void OpenMicroHud()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var scopes = _displayCatalog.List(handle);
        var scope = scopes.FirstOrDefault(value => !value.IncludesAllDisplays && scopes[0].HudBounds == value.HudBounds)
            ?? scopes.FirstOrDefault(value => !value.IncludesAllDisplays)
            ?? scopes.FirstOrDefault();
        if (scope is null)
        {
            MessageBox.Show(this, "No display is available for the Micro HUD.", _host.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenMicroHud(scope);
    }

    internal void OpenMicroHud(RecordingDisplayScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (_microHud is { IsVisible: true })
        {
            _microHud.Activate();
            return;
        }

        _windowStateBeforeHud = WindowState;
        _normalBoundsBeforeHud = RestoreBounds;
        _suppressHudCloseRestore = false;
        _activeRecordingDisplayScope = scope;
        MicroHudWindow? hud = null;
        try
        {
            hud = new MicroHudWindow(scope.HudBounds, _activeProfile) { Title = _host.ProductName + " Micro HUD" };
            _microHud = hud;
            hud.RecordUiMapRequested += MicroHud_RecordUiMapRequested;
            if (_host is IShellWorkSessionsHost work)
                hud.RecordWorkSessionRequested += async (_, _) =>
                {
                    _suppressHudCloseRestore = true;
                    hud.Close();
                    try { await work.StartWorkSessionAsync(this, scope); }
                    finally { _suppressHudCloseRestore = false; }
                };
            if (_host is IShellWorkflowRunHost runtime)
                hud.RunWorkflowRequested += async (_, _) => { hud.Close(); RestoreMainWindow(); await runtime.RunWorkflowAsync(this); };
            hud.Closed += MicroHud_Closed;
            hud.Show();
            Hide();
            hud.Activate();
        }
        catch (Exception ex)
        {
            if (hud is not null)
            {
                hud.RecordUiMapRequested -= MicroHud_RecordUiMapRequested;
                hud.Closed -= MicroHud_Closed;
                hud.Close();
            }
            _microHud = null;
            _activeRecordingDisplayScope = null;
            RestoreMainWindow();
            MessageBox.Show(this, ex.GetBaseException().Message, "Micro HUD", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MicroHud_Closed(object? sender, EventArgs e)
    {
        if (sender is MicroHudWindow hud)
        {
            hud.RecordUiMapRequested -= MicroHud_RecordUiMapRequested;
            hud.Closed -= MicroHud_Closed;
        }
        if (ReferenceEquals(_microHud, sender))
            _microHud = null;
        if (ReferenceEquals(_microHud, null))
            _activeRecordingDisplayScope = null;
        if (!_suppressHudCloseRestore)
            RestoreMainWindow();
    }

    private async void MicroHud_RecordUiMapRequested(object? sender, EventArgs e)
    {
        if (sender is not MicroHudWindow hud || !ReferenceEquals(_microHud, hud))
            return;

        var displayScope = _activeRecordingDisplayScope;
        _suppressHudCloseRestore = true;
        hud.Close();
        try
        {
            var maps = NavigateToMaps();
            if (maps is not null)
                await maps.StartNewMapRecordingAsync(standalonePicker: true, displayScope: displayScope);
        }
        finally
        {
            _suppressHudCloseRestore = false;
            RestoreMainWindow();
        }
    }

    private void ShowDisplayScopeMenu(IReadOnlyList<RecordingDisplayScope> scopes)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = MicroHudButton,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -250,
            MinWidth = 296,
        };
        AutomationProperties.SetName(menu, "Choose displays for UI Map recording");
        foreach (var scope in scopes)
        {
            var header = new Grid { MinWidth = 250, Margin = new Thickness(3, 1, 3, 1) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock
            {
                Text = scope.HudMenuLabel,
                FontWeight = scope.IsCurrentDisplay ? FontWeights.Bold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var code = new TextBlock
            {
                Text = scope.DeviceCode,
                Margin = new Thickness(16, 0, 0, 0),
                Foreground = (System.Windows.Media.Brush)FindResource("MutedInkBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(code, 1);
            header.Children.Add(code);
            var item = new MenuItem { Header = header, Tag = scope, Padding = new Thickness(8, 7, 8, 7) };
            AutomationProperties.SetName(item, scope.HudMenuLabel);
            item.Click += (_, _) => OpenMicroHud(scope);
            menu.Items.Add(item);
        }
        MicroHudButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void RestoreMainWindow()
    {
        if (!IsLoaded)
            return;

        WindowState = WindowState.Normal;
        if (!_normalBoundsBeforeHud.IsEmpty && _normalBoundsBeforeHud.Width > 0 && _normalBoundsBeforeHud.Height > 0)
        {
            Left = _normalBoundsBeforeHud.Left;
            Top = _normalBoundsBeforeHud.Top;
            Width = _normalBoundsBeforeHud.Width;
            Height = _normalBoundsBeforeHud.Height;
        }
        Show();
        WindowState = _windowStateBeforeHud == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
    }

    private void SelectNavigation(ExperienceNavigationItem item)
    {
        if (Equals(NavigationList.SelectedItem, item))
            ActivateNavigation(item);
        else
            NavigationList.SelectedItem = item;
    }

    private MapsView? NavigateToMaps()
    {
        var maps = _activeProfile.Navigation.FirstOrDefault(item => item.IsAvailable && item.Route == "maps");
        if (maps is null)
            return null;

        SelectNavigation(maps);
        return _mapsView;
    }

    private void ShowPage(FrameworkElement page)
    {
        PageHost.Content = page;
        UpdateBackButton();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitorHandle, ref MonitorInfo monitorInfo);
}
