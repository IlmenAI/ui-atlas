using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UIMaps.Application.Core;
using UIMaps.Protocol.V1;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace UIMaps.Application.Views;

public sealed class MapInteractionGraphCanvas : FrameworkElement
{
    private const double NodeWidth = 250;
    private const double NodeHeight = 176;
    private const double CellWidth = 320;
    private const double CellHeight = 235;
    private readonly Dictionary<string, Rect> _nodeBounds = new(StringComparer.Ordinal);

    public static readonly DependencyProperty SurfacesProperty = DependencyProperty.Register(
        nameof(Surfaces), typeof(IEnumerable<MapViewerSurface>), typeof(MapInteractionGraphCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TransitionsProperty = DependencyProperty.Register(
        nameof(Transitions), typeof(IEnumerable<MapViewerTransition>), typeof(MapInteractionGraphCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedSurfaceRefProperty = DependencyProperty.Register(
        nameof(SelectedSurfaceRef), typeof(string), typeof(MapInteractionGraphCanvas),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<MapViewerSurface>? Surfaces { get => (IEnumerable<MapViewerSurface>?)GetValue(SurfacesProperty); set => SetValue(SurfacesProperty, value); }
    public IEnumerable<MapViewerTransition>? Transitions { get => (IEnumerable<MapViewerTransition>?)GetValue(TransitionsProperty); set => SetValue(TransitionsProperty, value); }
    public string SelectedSurfaceRef { get => (string)GetValue(SelectedSurfaceRefProperty); set => SetValue(SelectedSurfaceRefProperty, value); }
    public event EventHandler<string>? SurfaceSelected;

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Surfaces?.Count() ?? 0;
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Max(1, count))));
        var rows = Math.Max(1, (int)Math.Ceiling(count / (double)columns));
        return new Size(Math.Max(760, 48 + columns * CellWidth), Math.Max(480, 48 + rows * CellHeight));
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var surfaces = Surfaces?.ToArray() ?? [];
        _nodeBounds.Clear();
        DrawGrid(context);
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Max(1, surfaces.Length))));
        for (var index = 0; index < surfaces.Length; index++)
        {
            var row = index / columns;
            var column = index % columns;
            _nodeBounds[surfaces[index].EntityRef] = new Rect(30 + column * CellWidth, 30 + row * CellHeight, NodeWidth, NodeHeight);
        }
        DrawEdges(context);
        foreach (var surface in surfaces) DrawNode(context, surface, _nodeBounds[surface.EntityRef]);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Left || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        var point = e.GetPosition(this);
        var hit = _nodeBounds.FirstOrDefault(value => value.Value.Contains(point));
        if (string.IsNullOrWhiteSpace(hit.Key)) return;
        SurfaceSelected?.Invoke(this, hit.Key);
        e.Handled = true;
    }

    private void DrawGrid(DrawingContext context)
    {
        var dot = new SolidColorBrush(Color.FromRgb(214, 222, 231));
        for (var y = 14d; y < ActualHeight; y += 24)
            for (var x = 14d; x < ActualWidth; x += 24)
                context.DrawEllipse(dot, null, new Point(x, y), 1, 1);
    }

    private void DrawEdges(DrawingContext context)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(113, 91, 207)), 2);
        foreach (var edge in Transitions ?? [])
        {
            if (!_nodeBounds.TryGetValue(edge.SourceSurfaceRef, out var source)
                || !_nodeBounds.TryGetValue(edge.DestinationSurfaceRef, out var destination)) continue;
            var start = new Point(source.Right, source.Top + source.Height / 2);
            var end = new Point(destination.Left, destination.Top + destination.Height / 2);
            if (source == destination)
            {
                var loop = new StreamGeometry();
                using (var sink = loop.Open())
                {
                    sink.BeginFigure(new Point(source.Right - 18, source.Top + 8), false, false);
                    sink.BezierTo(
                        new Point(source.Right + 36, source.Top - 38),
                        new Point(source.Right + 48, source.Top + 58),
                        new Point(source.Right - 2, source.Top + 56),
                        true,
                        false);
                }
                loop.Freeze();
                context.DrawGeometry(null, pen, loop);
                continue;
            }
            var middle = (start.X + end.X) / 2;
            var geometry = new StreamGeometry();
            using (var sink = geometry.Open())
            {
                sink.BeginFigure(start, false, false);
                sink.BezierTo(new Point(middle, start.Y), new Point(middle, end.Y), end, true, false);
            }
            geometry.Freeze();
            context.DrawGeometry(null, pen, geometry);
            var direction = end.X >= start.X ? -1 : 1;
            context.DrawLine(pen, end, new Point(end.X + direction * 9, end.Y - 6));
            context.DrawLine(pen, end, new Point(end.X + direction * 9, end.Y + 6));
            DrawText(context, edge.Label, 10, Color.FromRgb(91, 65, 180), new Point(middle - 36, (start.Y + end.Y) / 2 - 18), FontWeights.SemiBold);
        }
    }

    private void DrawNode(DrawingContext context, MapViewerSurface surface, Rect bounds)
    {
        var selected = string.Equals(SelectedSurfaceRef, surface.EntityRef, StringComparison.Ordinal);
        var border = selected ? Color.FromRgb(76, 59, 220) : Color.FromRgb(190, 201, 214);
        context.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(border), selected ? 2 : 1), bounds, 10, 10);
        DrawText(context, surface.Name, 14, Color.FromRgb(28, 36, 48), new Point(bounds.X + 14, bounds.Y + 12), FontWeights.SemiBold);
        DrawText(context, surface.Detail, 9, Color.FromRgb(99, 113, 131), new Point(bounds.X + 14, bounds.Y + 34));
        var preview = new Rect(bounds.X + 14, bounds.Y + 56, bounds.Width - 28, bounds.Height - 70);
        context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(246, 249, 252)), new Pen(new SolidColorBrush(Color.FromRgb(220, 227, 235)), 1), preview, 5, 5);
        var source = Union(surface.Controls.Select(value => value.Bounds));
        if (source is null)
        {
            for (var row = 0; row < 5; row++)
                context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(224, 230, 237)), null, new Rect(preview.X + 18, preview.Y + 16 + row * 14, preview.Width - 36, 5), 2, 2);
            return;
        }
        foreach (var control in surface.Controls.Where(value => value.Bounds is { Width: > 0, Height: > 0 }).Take(120))
        {
            var projected = Project(control.Bounds!, source, preview);
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(32, 76, 59, 220)), new Pen(new SolidColorBrush(Color.FromRgb(76, 59, 220)), .7), projected, 2, 2);
        }
    }

    private static RectPx? Union(IEnumerable<RectPx?> source)
    {
        var values = source.Where(value => value is { Width: > 0, Height: > 0 }).Cast<RectPx>().ToArray();
        if (values.Length == 0) return null;
        var left = values.Min(value => value.X); var top = values.Min(value => value.Y);
        var right = values.Max(value => value.X + value.Width); var bottom = values.Max(value => value.Y + value.Height);
        return new RectPx { X = left, Y = top, Width = right - left, Height = bottom - top };
    }

    private static Rect Project(RectPx value, RectPx source, Rect target)
    {
        var scale = Math.Min(target.Width / source.Width, target.Height / source.Height);
        return new Rect(target.X + (value.X - source.X) * scale, target.Y + (value.Y - source.Y) * scale,
            Math.Max(2, value.Width * scale), Math.Max(2, value.Height * scale));
    }

    private static void DrawText(DrawingContext context, string value, double size, Color color, Point origin, FontWeight? weight = null)
    {
        var text = new FormattedText(value, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, new SolidColorBrush(color), 1.0)
        { MaxTextWidth = NodeWidth - 28, Trimming = TextTrimming.CharacterEllipsis };
        context.DrawText(text, origin);
    }
}
