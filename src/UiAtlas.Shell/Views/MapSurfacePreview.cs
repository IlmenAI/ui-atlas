using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using UIMaps.Application.Core;
using UIMaps.Protocol.V1;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;
using MediaPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;

namespace UIMaps.Application.Views;

public sealed class MapSurfacePreview : FrameworkElement
{
    private readonly TranslateTransform _panTransform = new();
    private WpfPoint _panPointerOrigin;
    private WpfPoint _panTransformOrigin;
    private bool _isPanning;

    public MapSurfacePreview()
    {
        RenderTransform = _panTransform;
    }

    public static readonly DependencyProperty SurfaceProperty = DependencyProperty.Register(
        nameof(Surface), typeof(MapViewerSurface), typeof(MapSurfacePreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(MapViewerVariant), typeof(MapSurfacePreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ViewModeProperty = DependencyProperty.Register(
        nameof(ViewMode), typeof(string), typeof(MapSurfacePreview),
        new FrameworkPropertyMetadata(MapSurfaceViewModes.Screenshot, FrameworkPropertyMetadataOptions.AffectsRender));

    public MapViewerSurface? Surface { get => (MapViewerSurface?)GetValue(SurfaceProperty); set => SetValue(SurfaceProperty, value); }
    public MapViewerVariant? Variant { get => (MapViewerVariant?)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }
    public string ViewMode { get => (string)GetValue(ViewModeProperty); set => SetValue(ViewModeProperty, value); }

    internal static bool RequiresRetainedPixels(string viewMode) =>
        viewMode is MapSurfaceViewModes.Screenshot or MapSurfaceViewModes.Controls
            or MapSurfaceViewModes.Overlay or MapSurfaceViewModes.StructureOverlay;

    internal static bool DrawsFullScreenshot(string viewMode) =>
        viewMode is MapSurfaceViewModes.Screenshot or MapSurfaceViewModes.Overlay or MapSurfaceViewModes.StructureOverlay;

    internal static bool DrawsControlPixels(string viewMode) =>
        viewMode is MapSurfaceViewModes.Controls or MapSurfaceViewModes.Overlay;

    internal static bool DrawsControlGeometry(string viewMode) =>
        viewMode is MapSurfaceViewModes.Controls or MapSurfaceViewModes.Overlay or MapSurfaceViewModes.Structure or MapSurfaceViewModes.StructureOverlay;

    public void ResetPan()
    {
        _panTransform.X = 0;
        _panTransform.Y = 0;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle
            && (e.ChangedButton != MouseButton.Left || !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))) return;
        _isPanning = CaptureMouse();
        _panPointerOrigin = e.GetPosition(this);
        _panTransformOrigin = new WpfPoint(_panTransform.X, _panTransform.Y);
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isPanning) return;
        var point = e.GetPosition(this);
        _panTransform.X = _panTransformOrigin.X + point.X - _panPointerOrigin.X;
        _panTransform.Y = _panTransformOrigin.Y + point.Y - _panPointerOrigin.Y;
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_isPanning) return;
        _isPanning = false;
        ReleaseMouseCapture();
        ClearValue(CursorProperty);
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(MediaColor.FromRgb(250, 252, 254)), new MediaPen(new SolidColorBrush(MediaColor.FromRgb(197, 207, 219)), 1), bounds, 10, 10);
        if (Surface is null)
        {
            DrawText(drawingContext, "Select a surface", 14, MediaBrushes.SlateGray, new WpfPoint(18, 18));
            return;
        }

        var compact = ActualHeight < 100;
        if (!compact)
        {
            var header = new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Min(42, ActualHeight - 2));
            drawingContext.DrawRoundedRectangle(new SolidColorBrush(MediaColor.FromRgb(242, 246, 250)), null, header, 9, 9);
            DrawText(drawingContext, Variant?.Name ?? Surface.Name, 13, new SolidColorBrush(MediaColor.FromRgb(30, 41, 55)), new WpfPoint(16, 13), FontWeights.SemiBold);
        }

        // An observed variant with no UIA evidence must stay empty. Falling back to
        // the aggregate surface controls would attribute another variant's controls
        // to this checkpoint.
        var controls = Variant is null ? Surface.Controls : Variant.Controls;
        var source = Variant?.Bounds ?? Surface.Bounds ?? Union(controls.Select(value => value.Bounds));
        var content = CalculateContentRect(ActualWidth, ActualHeight);
        var renderTarget = source is { Width: > 0, Height: > 0 }
            ? CalculateRenderTarget(source, content)
            : content;
        var evidence = Variant?.VisualEvidence ?? Surface.Variants.Select(value => value.VisualEvidence).FirstOrDefault(value => value is not null);
        var wantsPixels = RequiresRetainedPixels(ViewMode);
        var evidenceImage = wantsPixels && evidence is not null ? TryLoadEvidenceImage(evidence) : null;
        if (wantsPixels && (source is not { Width: > 0, Height: > 0 } || evidence is null || evidenceImage is null))
        {
            DrawUnavailable(drawingContext, content, ViewMode);
            return;
        }

        var drewFullScreenshot = DrawsFullScreenshot(ViewMode)
            && DrawEvidenceRegion(drawingContext, evidenceImage!, evidence!, source!, renderTarget);
        if (DrawsFullScreenshot(ViewMode) && !drewFullScreenshot)
        {
            DrawUnavailable(drawingContext, content, ViewMode);
            return;
        }
        if (ViewMode == MapSurfaceViewModes.Screenshot) return;
        if (source is not { Width: > 0, Height: > 0 } || controls.Count == 0)
        {
            if (!drewFullScreenshot) DrawSkeleton(drawingContext, content);
            DrawText(drawingContext, drewFullScreenshot ? "No bounded controls" : "No bounded control geometry", 10, MediaBrushes.SlateGray, new WpfPoint(18, Math.Max(48, ActualHeight - 18)));
            return;
        }

        if (ViewMode is MapSurfaceViewModes.Controls or MapSurfaceViewModes.Structure)
            drawingContext.DrawRectangle(new SolidColorBrush(MediaColor.FromRgb(248, 250, 252)), null, renderTarget);

        foreach (var control in controls.Where(value => DrawsControlGeometry(ViewMode)
            && value.Bounds is { Width: > 0, Height: > 0 }))
        {
            var clipped = ClipToSource(control.Bounds!, source);
            if (clipped is null) continue;
            var rect = Project(clipped, source, renderTarget);
            if (DrawsControlPixels(ViewMode))
                DrawEvidenceRegion(drawingContext, evidenceImage!, evidence!, clipped, rect);

            var stroke = new SolidColorBrush(MediaColor.FromRgb(16, 185, 129));
            drawingContext.DrawRectangle(null, new MediaPen(stroke, 1), rect);
            if (ViewMode == MapSurfaceViewModes.Structure && rect.Width > 48 && rect.Height > 15)
                DrawText(drawingContext, control.Name, 8, new SolidColorBrush(MediaColor.FromRgb(52, 64, 80)), new WpfPoint(rect.X + 3, rect.Y + 2));
        }
    }

    internal static Rect CalculateRenderTarget(RectPx source, Rect target)
    {
        if (source.Width <= 0 || source.Height <= 0 || target.Width <= 0 || target.Height <= 0) return Rect.Empty;
        var scale = Math.Min(target.Width / source.Width, target.Height / source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;
        return new Rect(target.X + (target.Width - width) / 2, target.Y + (target.Height - height) / 2, width, height);
    }

    internal static Rect CalculateContentRect(double width, double height) => height < 100
        ? new Rect(3, 3, Math.Max(1, width - 6), Math.Max(1, height - 6))
        : new Rect(16, 55, Math.Max(1, width - 32), Math.Max(1, height - 71));

    internal static RectPx? ClipToSource(RectPx value, RectPx source)
    {
        var x = Math.Max(value.X, source.X);
        var y = Math.Max(value.Y, source.Y);
        var right = Math.Min(value.X + value.Width, source.X + source.Width);
        var bottom = Math.Min(value.Y + value.Height, source.Y + source.Height);
        return right <= x || bottom <= y ? null : new RectPx { X = x, Y = y, Width = right - x, Height = bottom - y };
    }

    private static void DrawUnavailable(DrawingContext context, Rect content, string viewMode)
    {
        if (content.Height < 80)
        {
            DrawText(context, "Screenshot unavailable", 9, MediaBrushes.SlateGray,
                new WpfPoint(content.X + 4, Math.Max(42, Math.Min(content.Y + 4, content.Bottom - 11))));
            return;
        }

        var message = viewMode switch
        {
            MapSurfaceViewModes.Screenshot => "Screenshot was not retained for this recording.",
            MapSurfaceViewModes.Controls => "Screenshot was not retained; Controls is unavailable.",
            MapSurfaceViewModes.Overlay => "Screenshot was not retained; Overlay is unavailable.",
            _ => "Screenshot was not retained; Structure Overlay is unavailable.",
        };
        DrawText(context, message, 12, new SolidColorBrush(MediaColor.FromRgb(71, 85, 105)),
            new WpfPoint(content.X + 8, content.Y + Math.Max(16, content.Height / 2 - 12)), FontWeights.SemiBold);
        DrawText(context, "Record a new UI Map to retain screenshot evidence.", 10, MediaBrushes.SlateGray,
            new WpfPoint(content.X + 8, content.Y + Math.Max(36, content.Height / 2 + 9)));
    }

    private static BitmapSource? TryLoadEvidenceImage(MapVisualEvidence evidence)
    {
        try
        {
            using var stream = new MemoryStream(evidence.PngBytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException) { return null; }
    }

    private static bool DrawEvidenceRegion(DrawingContext context, BitmapSource image, MapVisualEvidence evidence, RectPx cropBounds, Rect target)
    {
        var crop = CalculatePixelCrop(cropBounds, evidence.ScreenshotBounds, image.PixelWidth, image.PixelHeight);
        if (crop is null) return false;
        context.DrawImage(new CroppedBitmap(image, crop.Value), target);
        return true;
    }

    internal static Int32Rect? CalculatePixelCrop(RectPx cropBounds, RectPx screenshotBounds, int pixelWidth, int pixelHeight)
    {
        if (screenshotBounds.Width <= 0 || screenshotBounds.Height <= 0 || pixelWidth <= 0 || pixelHeight <= 0) return null;
        var clipped = ClipToSource(cropBounds, screenshotBounds);
        if (clipped is null) return null;
        var x = (int)Math.Round((clipped.X - screenshotBounds.X) * pixelWidth / (double)screenshotBounds.Width);
        var y = (int)Math.Round((clipped.Y - screenshotBounds.Y) * pixelHeight / (double)screenshotBounds.Height);
        var right = (int)Math.Round((clipped.X + clipped.Width - screenshotBounds.X) * pixelWidth / (double)screenshotBounds.Width);
        var bottom = (int)Math.Round((clipped.Y + clipped.Height - screenshotBounds.Y) * pixelHeight / (double)screenshotBounds.Height);
        x = Math.Clamp(x, 0, pixelWidth - 1);
        y = Math.Clamp(y, 0, pixelHeight - 1);
        right = Math.Clamp(right, x + 1, pixelWidth);
        bottom = Math.Clamp(bottom, y + 1, pixelHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private static RectPx? Union(IEnumerable<RectPx?> source)
    {
        var bounds = source.Where(value => value is { Width: > 0, Height: > 0 }).Cast<RectPx>().ToList();
        if (bounds.Count == 0) return null;
        var left = bounds.Min(value => value.X); var top = bounds.Min(value => value.Y);
        var right = bounds.Max(value => value.X + value.Width); var bottom = bounds.Max(value => value.Y + value.Height);
        return new RectPx { X = left, Y = top, Width = right - left, Height = bottom - top };
    }

    internal static Rect Project(RectPx value, RectPx source, Rect target)
    {
        var scale = Math.Min(target.Width / source.Width, target.Height / source.Height);
        var width = Math.Max(2, value.Width * scale); var height = Math.Max(2, value.Height * scale);
        return new Rect(target.X + (value.X - source.X) * scale, target.Y + (value.Y - source.Y) * scale, width, height);
    }

    private static void DrawSkeleton(DrawingContext context, Rect bounds)
    {
        var line = new SolidColorBrush(MediaColor.FromRgb(221, 228, 235));
        var panel = new SolidColorBrush(MediaColor.FromRgb(240, 244, 248));
        context.DrawRoundedRectangle(panel, null, new Rect(bounds.X, bounds.Y, bounds.Width * .2, bounds.Height), 4, 4);
        for (var row = 0; row < 5; row++)
            context.DrawRoundedRectangle(line, null, new Rect(bounds.X + bounds.Width * .25, bounds.Y + 8 + row * Math.Max(13, bounds.Height / 6), bounds.Width * .68, 5), 2, 2);
    }

    private static void DrawText(DrawingContext context, string value, double size, System.Windows.Media.Brush brush, System.Windows.Point origin, FontWeight? weight = null)
    {
        var text = new FormattedText(value, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new MediaFontFamily("Segoe UI Variable Text"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, brush, 1.0)
        { MaxTextWidth = Math.Max(1, 600), Trimming = TextTrimming.CharacterEllipsis };
        context.DrawText(text, origin);
    }
}
