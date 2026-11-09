using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using UiAtlas.Core.Contracts;
using UiAtlas.Presentation;

namespace UiAtlas.Core.Cli;

internal sealed class MapperLegendWindow : Window
{
    internal bool IsCollapsed { get; private set; }
    internal bool UserPositioned { get; private set; }
    private RectI _target = new(0, 0, 0, 0);
    private bool _hasPlacement;
    private readonly Func<MapperHighlightKind, bool> _isOverlayVisible;
    private readonly Action<MapperHighlightKind, bool> _setOverlayVisible;

    internal MapperLegendWindow(Func<MapperHighlightKind, bool> isOverlayVisible,
        Action<MapperHighlightKind, bool> setOverlayVisible)
    {
        _isOverlayVisible = isOverlayVisible;
        _setOverlayVisible = setOverlayVisible;
        Title = "UiAtlas highlight legend";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AtlasFluent.Apply(this);
        RenderContent();
    }

    internal void SetCollapsed(bool collapsed)
    {
        IsCollapsed = collapsed;
        RenderContent();
        if (IsVisible)
        {
            UpdateLayout();
            // Keep the same anchor on hide/restore; a handle dragged to an edge must still reopen on-screen.
            var bounds = RecordingHighlightOverlay.ReadPhysicalBounds(this);
            var area = WorkAreas().OrderByDescending(a => IntersectionArea(a, bounds)).FirstOrDefault();
            if (area is not null) MovePhysical(ClampToWorkArea(bounds, area));
        }
    }

    internal void Place(RectI target)
    {
        var targetChanged = target != _target;
        _target = target;
        if (!IsVisible || _hasPlacement && (UserPositioned || !targetChanged)) return;
        var areas = WorkAreas();
        var bounds = RecordingHighlightOverlay.ReadPhysicalBounds(this);
        var position = FindExteriorPosition(target, areas, bounds.Width, bounds.Height);
        if (position is null && !IsCollapsed && !_hasPlacement)
        {
            // A nearly maximized target may leave room only for the small restore handle.
            IsCollapsed = true;
            RenderContent(); UpdateLayout();
            bounds = RecordingHighlightOverlay.ReadPhysicalBounds(this);
            position = FindExteriorPosition(target, areas, bounds.Width, bounds.Height);
        }
        // With no exterior desktop space at all, retain a reachable handle at the screen edge.
        var area = areas.OrderByDescending(a => IntersectionArea(a, target)).FirstOrDefault();
        if (area is null || !area.IsValid) return;
        position ??= new RectI(area.X, area.Y, bounds.Width, bounds.Height);
        MovePhysical(position);
        _hasPlacement = true;
    }

    private void RenderContent()
    {
        Width = IsCollapsed ? 34 : 292;
        Height = IsCollapsed ? 34 : 312;
        var shell = new Border { Background = MapperGridInspector.Brush("#F9F9FA"),
            BorderBrush = MapperGridInspector.Brush("#DCE1E9"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(IsCollapsed ? 12 : 20) };
        if (IsCollapsed)
        {
            var restore = new Button { Content = "▦", FontSize = 20, Padding = new Thickness(0),
                Foreground = MapperGridInspector.Brush("#0A84FF"), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
                ToolTip = "Show legend · drag to move" };
            System.Windows.Automation.AutomationProperties.SetName(restore, "Show highlight legend");
            var start = new Point();
            var dragging = false;
            restore.PreviewMouseLeftButtonDown += (_, e) => { start = PointToScreen(e.GetPosition(this)); dragging = false; };
            restore.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || dragging) return;
                var point = PointToScreen(e.GetPosition(this));
                if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragging = true; UserPositioned = true;
                restore.ReleaseMouseCapture();
                DragMove();
                e.Handled = true;
            };
            restore.Click += (_, _) => { if (!dragging) SetCollapsed(false); };
            shell.Child = restore;
        }
        else
        {
            var stack = new StackPanel { Margin = new Thickness(16, 10, 16, 14) };
            var header = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            var hide = new Button { Content = "−", Width = 28, Height = 28, MinHeight = 0, Padding = new(0), FontSize = 19,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
                ToolTip = "Hide legend" };
            System.Windows.Automation.AutomationProperties.SetName(hide, "Hide highlight legend");
            hide.Click += (_, _) => SetCollapsed(true);
            DockPanel.SetDock(hide, Dock.Right); header.Children.Add(hide);
            header.Children.Add(AtlasFluent.Text("⠿  Highlights", 14));
            header.MouseLeftButtonDown += (_, _) => { UserPositioned = true; DragMove(); };
            stack.Children.Add(header);
            stack.Children.Add(AtlasFluent.Text("Show on screen", 12, AtlasFluent.Muted));
            foreach (var (kind, symbol, label, color) in new[]
            {
                (MapperHighlightKind.Uia, "━", "UIA / MSAA", "#0A84FF"),
                (MapperHighlightKind.ComputerVision, "⌜", "Computer vision", "#8B5CF6"),
                (MapperHighlightKind.NativeGrid, "▦", "Native grid", "#008F83"),
                (MapperHighlightKind.GridCandidate, "┄", "Grid candidate", "#C98208")
            })
            {
                var text = new TextBlock { Text = label, Foreground = AtlasFluent.Brush(AtlasFluent.Ink), FontSize = 13 };
                var row = new DockPanel();
                var swatch = new Grid { Width = 26, Height = 20, Margin = new(0, 0, 10, 0), IsHitTestVisible = false };
                var outline = new System.Windows.Shapes.Rectangle { Stroke = AtlasFluent.Brush(color), StrokeThickness = 1.5,
                    Width = 24, Height = 16, RadiusX = 1, RadiusY = 1 };
                if (kind == MapperHighlightKind.GridCandidate) outline.StrokeDashArray = new DoubleCollection { 3, 2 };
                swatch.Children.Add(outline);
                if (kind == MapperHighlightKind.NativeGrid)
                {
                    foreach (var x in new[] { 9d, 17d }) swatch.Children.Add(new System.Windows.Shapes.Line
                        { X1 = x, X2 = x, Y1 = 2, Y2 = 18, Stroke = AtlasFluent.Brush(color), StrokeThickness = 1 });
                    swatch.Children.Add(new System.Windows.Shapes.Line { X1 = 1, X2 = 25, Y1 = 10, Y2 = 10, Stroke = AtlasFluent.Brush(color), StrokeThickness = 1 });
                }
                else if (kind == MapperHighlightKind.ComputerVision)
                {
                    outline.StrokeDashArray = new DoubleCollection { 4, 3 };
                }
                row.Children.Add(swatch); row.Children.Add(text);
                var toggle = new CheckBox { Content = row, IsChecked = _isOverlayVisible(kind),
                    Margin = new Thickness(0, 5, 0, 5), Cursor = Cursors.Hand,
                    VerticalContentAlignment = VerticalAlignment.Center };
                System.Windows.Automation.AutomationProperties.SetName(toggle, $"{label} overlays");
                void UpdateAppearance()
                {
                    text.Opacity = toggle.IsChecked == true ? 1 : 0.45;
                    toggle.ToolTip = $"{(toggle.IsChecked == true ? "Hide" : "Show")} {label} overlays";
                }
                toggle.Checked += (_, _) => { UpdateAppearance(); _setOverlayVisible(kind, true); };
                toggle.Unchecked += (_, _) => { UpdateAppearance(); _setOverlayVisible(kind, false); };
                UpdateAppearance();
                stack.Children.Add(toggle);
            }
            stack.Children.Add(AtlasFluent.Text("Visibility only · Mapping continues", 11, AtlasFluent.Muted));
            shell.Child = stack;
        }
        Content = shell;
    }

    internal static RectI? FindExteriorPosition(RectI target, IReadOnlyList<RectI> areas, int width, int height)
    {
        const int gap = 12;
        foreach (var area in areas.OrderByDescending(a => IntersectionArea(a, target)))
        {
            if (width > area.Width || height > area.Height) continue;
            int X(int value) => Math.Clamp(value, area.X, area.X + area.Width - width);
            int Y(int value) => Math.Clamp(value, area.Y, area.Y + area.Height - height);
            foreach (var position in new[]
            {
                new RectI(X(target.X + target.Width + gap), Y(target.Y), width, height),
                new RectI(X(target.X - width - gap), Y(target.Y), width, height),
                new RectI(X(target.X), Y(target.Y + target.Height + gap), width, height),
                new RectI(X(target.X), Y(target.Y - height - gap), width, height)
            })
                if (IntersectionArea(position, target) == 0) return position;
        }
        return null;
    }

    internal static RectI ClampToWorkArea(RectI bounds, RectI area) => bounds with
    {
        X = Math.Clamp(bounds.X, area.X, Math.Max(area.X, area.X + area.Width - bounds.Width)),
        Y = Math.Clamp(bounds.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - bounds.Height))
    };

    private static long IntersectionArea(RectI a, RectI b) =>
        Math.Max(0L, Math.Min((long)a.X + a.Width, (long)b.X + b.Width) - Math.Max(a.X, b.X)) *
        Math.Max(0L, Math.Min((long)a.Y + a.Height, (long)b.Y + b.Height) - Math.Max(a.Y, b.Y));

    private void MovePhysical(RectI bounds)
    {
        var previous = SetThreadDpiAwarenessContext((nint)(-4));
        // Let WPF maintain the panel's DIP size when moving between monitor scales.
        try { _ = SetWindowPos(new WindowInteropHelper(this).Handle, 0,
            bounds.X, bounds.Y, 0, 0, 0x0015); }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    private static IReadOnlyList<RectI> WorkAreas()
    {
        var result = new List<RectI>();
        var previous = SetThreadDpiAwarenessContext((nint)(-4));
        try
        {
            EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref NativeRect rect, nint data) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info)) result.Add(new(info.Work.Left, info.Work.Top,
                    info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
                return true;
            }, 0);
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(nint monitor, nint hdc, ref NativeRect rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
