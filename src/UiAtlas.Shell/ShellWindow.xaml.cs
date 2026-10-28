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
    private IShellBackNavigation? _pageBackNavigation;
    private DiagnosticsHubView? _diagnosticsHubView;
    private HwndSource? _windowSource;
    private readonly UiAtlas.Presentation.AtlasSettingsNavigation _recorderSettingsNavigation;
    public string RecorderSettingsPipe => _recorderSettingsNavigation.PipeName;
    private bool _changingOrganization;
    private IReadOnlyList<ExperienceNavigationItem> Navigation => (IReadOnlyList<ExperienceNavigationItem>)NavigationList.ItemsSource;

    public ShellWindow(IShellHost host)
    {
        _host = host;
        _launcher = host.Launcher;
        _displayCatalog = host.Displays;
        _applicationDiscovery = host.Applications;
        _profiles = host.Profiles;
        _activeRoute = host.StartupRoute;
        InitializeComponent();
        _recorderSettingsNavigation = new(() => Dispatcher.Invoke(() =>
        {
            RestoreMainWindow(); Navigate("settings");
            if (_settingsView is IShellSettingsSectionNavigation settings) settings.ShowRecordingSettings();
        }));
        Title = host.ProductName;
        ProductTitle.Text = host.ProductName;
        ProductLogo.Wordmark = host.ProductName.ToUpperInvariant();
        ProfileSelector.ItemsSource = _profiles.Profiles;
        var startupMode = _profiles.GetDefinition(host.StartupProfile).Option.Id;
        _changingProfile = true;
        ProfileSelector.SelectedItem = _profiles.Profiles.Single(option => option.Id == startupMode);
        _changingProfile = false;
        ApplyProfile(startupMode);
        if (host is IShellOrganizationHost organizations)
        {
            RefreshOrganizations();
            organizations.OrganizationChanged += (_, _) => Dispatcher.Invoke(() =>
            {
                RefreshOrganizations(); _activeRoute = organizations.OrganizationId is null ? "organizations" : "skills";
                ApplyProfile(ActiveProfileId);
            });
        }
        Closing += (_, args) =>
        {
            if (!CanLeaveCurrentPage()) args.Cancel = true;
        };
        Closing += async (_, args) =>
        {
            if (args.Cancel || _workCloseApproved || _host is not IShellWorkSessionsHost work) return;
            args.Cancel = true;
            if (_workClosing) return;
            _workClosing = true;
            try
            {
                if (await work.TryCloseWorkSessionsAsync(this))
                {
                    _workCloseApproved = true;
                    _ = Dispatcher.BeginInvoke(new Action(Close));
                }
            }
            finally { _workClosing = false; }
        };
    }

    public event EventHandler? ProfileChanged;
    public void Navigate(string route)
    {
        var item = Navigation.FirstOrDefault(value => value.Route == route && value.IsAvailable)
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
        _recorderSettingsNavigation.Dispose();
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
        NavigationList.ItemsSource = _host is IShellOrganizationHost organizations
            ? OrganizationNavigation(organizations.OrganizationId, _activeProfile.Option.Id) : _activeProfile.Navigation;

        if (_host is IShellOrganizationHost && _activeRoute == "recordings" && _activeProfile.Option.Id != ExperienceProfileId.Diagnostics) _activeRoute = "maps";
        var retained = Navigation.FirstOrDefault(item =>
            item.IsAvailable && string.Equals(item.Route, _activeRoute, StringComparison.Ordinal));
        SelectNavigation(retained ?? Navigation.First(item => item.IsAvailable));
    }

    public static IReadOnlyList<ExperienceNavigationItem> OrganizationNavigation(string? organizationId, ExperienceProfileId profile)
    {
        List<ExperienceNavigationItem> items = organizationId is null ? [new("organizations", "Organizations", "\uE80F")]
            : [new("skills", "Skills", "\uE945"), new("organization-maps", "UI maps", "\uE81E"),
                new("organization-knowledge", "Knowledge", "\uE8F1"), new("organization-about", "About", "\uE946")];
        items.Add(new("maps", "Shared UI Maps", "\uE81E", StartsNewSection: true));
        if (profile == ExperienceProfileId.Diagnostics) items.Add(new("recordings", "UI Map Recordings", "\uE714"));
        items.Add(new("settings", "Settings", "\uE713", StartsNewSection: true)); return items;
    }
    private void RefreshOrganizations()
    {
        if (_host is not IShellOrganizationHost host) return;
        _changingOrganization = true;
        OrganizationSelector.Visibility = Visibility.Visible;
        OrganizationSelector.ItemsSource = new[] { new ShellOrganization("", "All organizations") }.Concat(host.Organizations()).ToArray();
        OrganizationSelector.SelectedItem = OrganizationSelector.Items.Cast<ShellOrganization>().FirstOrDefault(o => o.Id == (host.OrganizationId ?? ""));
        _changingOrganization = false;
    }
    private void OrganizationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingOrganization || _host is not IShellOrganizationHost host || OrganizationSelector.SelectedItem is not ShellOrganization selected) return;
        if (!CanLeaveCurrentPage()) { RefreshOrganizations(); return; }
        host.SelectOrganization(string.IsNullOrEmpty(selected.Id) ? null : selected.Id);
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

        if (!CanLeaveCurrentPage())
        {
            _changingProfile = true; ProfileSelector.SelectedItem = _activeProfile.Option; _changingProfile = false;
            return;
        }
        if (_host is not IShellOrganizationHost) _activeRoute = _profiles.GetDefinition(selected.Id).LandingRoute ?? _activeRoute;
        ApplyProfile(selected.Id);
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavigationList.SelectedItem is not ExperienceNavigationItem item || !item.IsAvailable)
            return;

        if (_restoringNavigation) return;
        ActivateNavigation(item);
    }

    private bool _restoringNavigation;
    private bool CanLeaveCurrentPage()
    {
        if (PageHost.Content is IShellNavigationGuard guard && !guard.CanLeave(this)) return false;
        return _host is not IShellOrganizationHost org || PageHost.Content is not FrameworkElement page || org.CanLeaveOrganizationView(page, this);
    }

    private void ActivateNavigation(ExperienceNavigationItem item)
    {
        if (item.Route != _activeRoute && !CanLeaveCurrentPage())
        {
            _restoringNavigation = true;
            NavigationList.SelectedItem = Navigation.FirstOrDefault(value => value.Route == _activeRoute);
            _restoringNavigation = false;
            return;
        }
        _activeRoute = item.Route;
        if (_host is IShellOrganizationHost organizations && item.Route is "organizations" or "skills" or "organization-maps" or "organization-knowledge" or "organization-about")
        {
            ShowPage(organizations.CreateOrganizationView(item.Route, this, Navigate)); return;
        }
        switch (item.Route)
        {
            case "business-knowledge" or "skills" or "execution-history" or "people-escalation" when _host is IShellSkillsHost skills:
                ShowPage(skills.CreateSkillView(item.Route));
                break;
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
        if (_host is IShellHomeHost homeHost)
        {
            ShowPage(homeHost.CreateHomeView(Navigate));
            return;
        }
        var home = new HomeView(heading, _host is IShellWorkSessionsHost);
        home.OpenUiMapRequested += (_, _) => NavigateToMaps();
        ShowPage(home);
    }

    private MapsView CreateMapsView()
    {
        var view = new MapsView(_launcher, _host.Maps, _applicationDiscovery);
        view.LaunchCompleted += (_, result) => ReportLaunch(result);
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
        if (_pageBackNavigation?.CanGoBack == true && CanLeaveCurrentPage())
            _pageBackNavigation.GoBack();
        UpdateBackButton();
    }

    private void UpdateBackButton()
    {
        if (BackButton is not null)
            BackButton.Visibility = _pageBackNavigation?.CanGoBack == true
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
            var hudProfile = _host is IShellAssistantHost
                ? _activeProfile with { HudCommands = _activeProfile.HudCommands.Where(command => command.Id != "hud.run-workflow").Append(new ExperienceCommand("hud.assistant", "Chat", "\uE8F2")).ToArray() }
                : _activeProfile;
            hud = new MicroHudWindow(scope.HudBounds, hudProfile) { Title = _host.ProductName + " Micro HUD" };
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
            if (_host is IShellAssistantHost assistant)
                hud.AssistantRequested += async (_, _) =>
                {
                    hud.Close(); RestoreMainWindow();
                    try { await assistant.OpenAssistantAsync(this); }
                    catch (Exception error) { MessageBox.Show(this, error.Message, "Open chat", MessageBoxButton.OK, MessageBoxImage.Error); }
                };
            hud.DiagnosticsRequested += (_, _) =>
            {
                hud.Close(); RestoreMainWindow();
                ActivateNavigation(new ExperienceNavigationItem("diagnostics", "Diagnostics", "\uE9D2"));
                if (_activeRoute == "diagnostics") NavigationList.SelectedItem = null;
            };
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

    private void PageBackChanged(object? sender, EventArgs e) => UpdateBackButton();

    private void ShowPage(FrameworkElement page)
    {
        if (_pageBackNavigation is not null) _pageBackNavigation.CanGoBackChanged -= PageBackChanged;
        PageHost.Content = page;
        _pageBackNavigation = page as IShellBackNavigation;
        if (_pageBackNavigation is not null) _pageBackNavigation.CanGoBackChanged += PageBackChanged;
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
