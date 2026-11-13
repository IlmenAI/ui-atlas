using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using UIMaps.Application.Core;

namespace UIMaps.Application;

public partial class NewUiMapWindow : Window
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;

    private readonly IRecordableApplicationDiscovery _discovery;
    private readonly RecordingDisplayScope? _displayScope;
    private bool _positioned;
    private readonly MapsLibraryRecord? _appendMap;

    public NewUiMapWindow() : this(new RecordableApplicationDiscovery()) { }

    internal NewUiMapWindow(
        IRecordableApplicationDiscovery discovery,
        RecordingDisplayScope? displayScope = null, MapsLibraryRecord? appendMap = null)
    {
        _discovery = discovery;
        _displayScope = displayScope;
        _appendMap = appendMap;
        InitializeComponent();
        if (appendMap is not null)
        {
            Title = Heading.Text = "Record more observations";
            Description.Text = "Add to " + appendMap.Entry.ApplicationDisplayName + " · " + appendMap.CatalogMapId
                + ". Choose the matching application window. Existing tasks keep their current map.";
        }
        DisplayScopeText.Text = displayScope?.PickerDescription ?? "Showing visible apps on all displays";
        Loaded += (_, _) => RefreshTargets();
        if (displayScope is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            ContentRendered += NewUiMapWindow_ContentRendered;
        }
    }

    public RecordableApplicationTarget? SelectedTarget { get; private set; }

    private void NewUiMapWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_positioned || _displayScope is null)
            return;
        _positioned = true;

        var dpi = VisualTreeHelper.GetDpi(this);
        var requestedSize = new System.Drawing.Size(
            Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)));
        var placement = DisplayWindowPlacement.Center(_displayScope.HudBounds, requestedSize);
        SetWindowPos(
            new WindowInteropHelper(this).Handle,
            0,
            placement.X,
            placement.Y,
            placement.Width,
            placement.Height,
            SwpNoZOrder | SwpShowWindow);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshTargets();

    private void RefreshTargets()
    {
        var targets = _discovery.Discover(_displayScope);
        if (_appendMap is not null)
        {
            var executable = _appendMap.ApplicationInfo?.Executable ?? _appendMap.Session?.Target.ExecutableName
                ?? _appendMap.Revision?.Target.ExecutableName ?? "";
            targets = targets.Where(t => UIMaps.Protocol.WindowsExecutableIdentity.ProcessName(t.ExecutableName).Equals(
                UIMaps.Protocol.WindowsExecutableIdentity.ProcessName(executable), StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        var view = new ListCollectionView(targets.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RecordableApplicationTarget.Category)));
        ApplicationsList.ItemsSource = view;
        ApplicationsList.SelectedIndex = targets.Count > 0 ? 0 : -1;
        EmptyState.Visibility = targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DisplayScopeText.Visibility = targets.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateStartState();
    }

    private void ApplicationsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStartState();
    private void ScopeApproval_Changed(object sender, RoutedEventArgs e) => UpdateStartState();

    private void UpdateStartState()
    {
        if (StartButton is not null)
            StartButton.IsEnabled = ApplicationsList.SelectedItem is RecordableApplicationTarget && ScopeApproval.IsChecked == true;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ApplicationsList.SelectedItem is not RecordableApplicationTarget target || ScopeApproval.IsChecked != true)
            return;
        SelectedTarget = _appendMap is null ? target : target with { AppendDestination =
            new UiAtlas.Catalog.CatalogRequest("recording-append", "", MapId: _appendMap.CatalogMapId,
                Generation: _appendMap.CatalogGeneration, Entry: Convert.ToBase64String(Google.Protobuf.MessageExtensions.ToByteArray(_appendMap.Entry))) };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
