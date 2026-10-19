using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using UIMaps.Application.Core;
using MediaBrushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace UIMaps.Application;

public partial class MapViewerWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private double _topologyZoom = 1;
    private double _surfaceZoom = 1;
    private double _graphZoom = 1;
    private ScrollViewer? _panningViewport;
    private Point _panPointerOrigin;
    private double _panHorizontalOrigin;
    private double _panVerticalOrigin;
    private HwndSource? _windowSource;

    internal MapViewerWindow(MapsLibraryRecord record, string? selectedEntityRef = null)
    {
        ViewModel = new MapViewerModel(record);
        DataContext = ViewModel;
        InitializeComponent();
        if (!string.IsNullOrEmpty(selectedEntityRef)) ViewModel.SelectEntity(selectedEntityRef);
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Viewport_MouseDown), handledEventsToo: true);
        AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(Viewport_MouseMove), handledEventsToo: true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Viewport_MouseUp), handledEventsToo: true);
        AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(Viewport_MouseWheel), handledEventsToo: true);
    }

    internal MapViewerModel ViewModel { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(MapViewerWindowProc);
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(MapViewerWindowProc);
        _windowSource = null;
        base.OnClosed(e);
    }

    private static nint MapViewerWindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo || lParam == 0) return 0;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == 0) return 0;
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return 0;

        var maximizeBounds = WindowMaximizeBounds.Calculate(
            monitorInfo.Monitor.ToRectangle(), monitorInfo.WorkArea.ToRectangle());
        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMaxInfo.MaxPosition.X = maximizeBounds.Position.X;
        minMaxInfo.MaxPosition.Y = maximizeBounds.Position.Y;
        minMaxInfo.MaxSize.X = maximizeBounds.Size.Width;
        minMaxInfo.MaxSize.Y = maximizeBounds.Size.Height;
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
        handled = true;
        return 0;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeRestoreGlyph is not null)
            MaximizeRestoreGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void World_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<MapViewerWorld>(tag, out var world)) return;
        ViewModel.World = world;
        ViewModel.SurfaceViewMode = world switch
        {
            MapViewerWorld.Observed => MapSurfaceViewModes.Screenshot,
            MapViewerWorld.Structured => MapSurfaceViewModes.Structure,
            MapViewerWorld.Interpreted => MapSurfaceViewModes.StructureOverlay,
            MapViewerWorld.Atlas => MapSurfaceViewModes.StructureOverlay,
            _ => ViewModel.SurfaceViewMode,
        };
        foreach (var button in new[] { ObservedWorldButton, StructuredWorldButton, InterpretedWorldButton, AtlasWorldButton })
        {
            var active = string.Equals(button.Tag as string, tag, StringComparison.Ordinal);
            button.Background = active ? Brush("#E3EFFB") : MediaBrushes.Transparent;
            button.Foreground = active ? Brush("#0F6CBD") : Brush("#5F6B7A");
            button.BorderBrush = active ? Brush("#0F6CBD") : MediaBrushes.Transparent;
        }
    }

    private void SurfaceAnalysis_Click(object sender, RoutedEventArgs e) => SelectPerspective(MapViewerPerspective.SurfaceAnalysis);
    private void InteractionGraph_Click(object sender, RoutedEventArgs e) => SelectPerspective(MapViewerPerspective.InteractionGraph);

    private void SelectPerspective(MapViewerPerspective perspective)
    {
        ViewModel.Perspective = perspective;
        var analysis = perspective == MapViewerPerspective.SurfaceAnalysis;
        SurfaceAnalysisPanel.Visibility = analysis ? Visibility.Visible : Visibility.Collapsed;
        InteractionGraphPanel.Visibility = analysis ? Visibility.Collapsed : Visibility.Visible;
        SurfaceAnalysisRail.Background = analysis ? Brush("#D9EAFB") : MediaBrushes.Transparent;
        SurfaceAnalysisRail.Foreground = analysis ? Brush("#0F6CBD") : Brush("#1B1F27");
        InteractionGraphRail.Background = analysis ? MediaBrushes.Transparent : Brush("#D9EAFB");
        InteractionGraphRail.Foreground = analysis ? Brush("#1B1F27") : Brush("#0F6CBD");
        PerspectiveTitle.Text = analysis ? "Surface Analysis" : "Interaction Graph";
    }

    private void TopologyCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MapViewerStageCard card } && !string.IsNullOrWhiteSpace(card.EntityRef))
            ViewModel.SelectEntity(card.EntityRef);
    }

    private void SurfaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string entityRef }) ViewModel.SelectEntity(entityRef);
    }

    private void GraphSurface_Selected(object? sender, string entityRef) => ViewModel.SelectEntity(entityRef);

    private void Hierarchy_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is MapViewerHierarchyNode node) ViewModel.SelectEntity(node.EntityRef);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SurfaceModeList is not null) SurfaceModeList.Tag = e.NewSize.Width >= 1250;
        if (Breadcrumb is not null) Breadcrumb.Visibility = e.NewSize.Width >= 1320 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var panRequested = e.ChangedButton == MouseButton.Middle
            || (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        if (!panRequested || FindAncestor<ScrollViewer>(e.OriginalSource as DependencyObject) is not { } viewport) return;
        _panningViewport = viewport;
        _panPointerOrigin = e.GetPosition(viewport);
        _panHorizontalOrigin = viewport.HorizontalOffset;
        _panVerticalOrigin = viewport.VerticalOffset;
        viewport.CaptureMouse();
        viewport.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panningViewport is not { IsMouseCaptured: true } viewport) return;
        var point = e.GetPosition(viewport);
        viewport.ScrollToHorizontalOffset(_panHorizontalOrigin - (point.X - _panPointerOrigin.X));
        viewport.ScrollToVerticalOffset(_panVerticalOrigin - (point.Y - _panPointerOrigin.Y));
        e.Handled = true;
    }

    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panningViewport is not { } viewport) return;
        viewport.ReleaseMouseCapture();
        viewport.ClearValue(CursorProperty);
        _panningViewport = null;
        e.Handled = true;
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || e.OriginalSource is not DependencyObject source) return;
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        if (IsDescendantOf(source, TopologyViewport)) SetTopologyZoom(_topologyZoom * factor);
        else if (IsDescendantOf(source, PrimarySurfacePreview)) SetSurfaceZoom(_surfaceZoom * factor);
        else if (IsDescendantOf(source, InteractionGraphPanel)) SetGraphZoom(_graphZoom * factor);
        else return;
        e.Handled = true;
    }
    private void Topology_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        SetTopologyZoom(_topologyZoom * (e.Delta > 0 ? 1.15 : 1 / 1.15)); e.Handled = true;
    }
    private void TopologyZoomOut_Click(object sender, RoutedEventArgs e) => SetTopologyZoom(_topologyZoom / 1.2);
    private void TopologyZoomIn_Click(object sender, RoutedEventArgs e) => SetTopologyZoom(_topologyZoom * 1.2);
    private void TopologyReset_Click(object sender, RoutedEventArgs e) => SetTopologyZoom(1);
    private void SurfaceZoomOut_Click(object sender, RoutedEventArgs e) => SetSurfaceZoom(_surfaceZoom / 1.2);
    private void SurfaceZoomIn_Click(object sender, RoutedEventArgs e) => SetSurfaceZoom(_surfaceZoom * 1.2);
    private void SurfaceReset_Click(object sender, RoutedEventArgs e) { SetSurfaceZoom(1); PrimarySurfacePreview.ResetPan(); }
    private void GraphZoomOut_Click(object sender, RoutedEventArgs e) => SetGraphZoom(_graphZoom / 1.2);
    private void GraphZoomIn_Click(object sender, RoutedEventArgs e) => SetGraphZoom(_graphZoom * 1.2);
    private void GraphReset_Click(object sender, RoutedEventArgs e) => SetGraphZoom(1);

    private void SetTopologyZoom(double value) { _topologyZoom = Clamp(value); TopologyScale.ScaleX = TopologyScale.ScaleY = _topologyZoom; TopologyZoomText.Content = $"{_topologyZoom:P0}"; }
    private void SetSurfaceZoom(double value) { _surfaceZoom = Clamp(value); SurfaceScale.ScaleX = SurfaceScale.ScaleY = _surfaceZoom; SurfaceZoomText.Content = $"{_surfaceZoom:P0}"; }
    private void SetGraphZoom(double value) { _graphZoom = Clamp(value); GraphScale.ScaleX = GraphScale.ScaleY = _graphZoom; GraphZoomText.Content = $"{_graphZoom:P0}"; }
    private static double Clamp(double value) => Math.Clamp(value, .25, 4);
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
    private static System.Windows.Media.Brush Brush(string value) =>
        (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(value)!;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

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
