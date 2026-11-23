using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace UiAtlas.Core.Desktop;

internal static class MapProjectionVisual
{
    internal static readonly Brush SemanticStroke = FrozenInk(0x2D, 0xCE, 0x91);
    private static readonly Brush LabelInk = FrozenInk(0x25, 0x29, 0x36);
    private static Brush FrozenInk(byte red, byte green, byte blue)
    {
        var ink = new SolidColorBrush(Color.FromRgb(red, green, blue));
        ink.Freeze();
        return ink;
    }

    internal static ImageBrush? Crop(BitmapSource bitmap, RectI pixels) =>
        TryClipBitmapRect(pixels, bitmap, out var crop) ? new ImageBrush(new CroppedBitmap(bitmap, crop)) { Stretch = Stretch.Fill } : null;

    internal static Rectangle AddControl(Canvas canvas, UiMapControlView control, UiMapSurfaceView ownerSurface,
        RectI local, UiMapProjectionPolicy projection, ImageBrush? controlCrop, bool isSelected,
        UiUnderstandingLevel level, Brush stroke, bool isUnverified = false, bool isButtonCandidate = false,
        bool isCompactPopupAction = false, bool showMissingCropLabel = false)
    {
        var highlightColor = isUnverified ? Color.FromRgb(0xFF, 0x8B, 0x3D) : Color.FromRgb(0x2D, 0xCE, 0x91);
        var rectangle = new Rectangle { Width = Math.Max(1, local.Width), Height = Math.Max(1, local.Height),
            Stroke = isSelected || isButtonCandidate || isCompactPopupAction
                ? new SolidColorBrush(highlightColor)
                : stroke,
            StrokeThickness = isSelected ? 4 : isButtonCandidate || isCompactPopupAction ? 2 : 1.2,
            StrokeDashArray = !isSelected && isButtonCandidate && !isCompactPopupAction ? [4, 2] : null,
            Fill = (Brush?)controlCrop ?? (isSelected
                ? new SolidColorBrush(Color.FromArgb(92, highlightColor.R, highlightColor.G, highlightColor.B))
                : isButtonCandidate || isCompactPopupAction
                    ? new SolidColorBrush(Color.FromArgb(28, highlightColor.R, highlightColor.G, highlightColor.B))
                : ResolveBlueprintFill(control, ownerSurface, level, projection.BlueprintFillOpacity)),
            Effect = isSelected
                ? new DropShadowEffect { Color = highlightColor, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.8 }
                : null,
            Cursor = Cursors.Hand
        };
        Canvas.SetLeft(rectangle, local.X); Canvas.SetTop(rectangle, local.Y); canvas.Children.Add(rectangle);
        if ((projection.ShowsControlLabels || showMissingCropLabel && projection.ShowsControlCrops && controlCrop is null) &&
            !(ownerSurface.SurfaceKind.Contains("Popup", StringComparison.OrdinalIgnoreCase) &&
              (control.CanonicalKind is "Window" or "Menu" or "Pane" or "List" ||
               control.CanonicalKind == "Group" && string.IsNullOrWhiteSpace(control.Source.Properties.FirstOrDefault(p => p.Name == "name")?.Value))))
        {
            var label = new TextBlock { Text = control.DisplayName, FontSize = 9,
                Foreground = LabelInk, TextTrimming = TextTrimming.CharacterEllipsis };
            label.MaxWidth = Math.Max(30, local.Width - 4);
            label.IsHitTestVisible = false;
            Canvas.SetLeft(label, local.X + 2); Canvas.SetTop(label, local.Y + 1); canvas.Children.Add(label);
        }
        return rectangle;
    }

    internal static bool TryClipBitmapRect(RectI bounds, BitmapSource bitmap, out Int32Rect result)
    {
        var left = Math.Clamp(bounds.X, 0, bitmap.PixelWidth);
        var top = Math.Clamp(bounds.Y, 0, bitmap.PixelHeight);
        var right = Math.Clamp((long)bounds.X + bounds.Width, 0, bitmap.PixelWidth);
        var bottom = Math.Clamp((long)bounds.Y + bounds.Height, 0, bitmap.PixelHeight);
        if (right <= left || bottom <= top)
        {
            result = default;
            return false;
        }
        result = new Int32Rect(left, top, checked((int)(right - left)), checked((int)(bottom - top)));
        return true;
    }

    private static Brush ResolveBlueprintFill(
        UiMapControlView control,
        UiMapSurfaceView surface,
        UiUnderstandingLevel level,
        double opacity)
    {
        if (opacity <= 0 || UiMapPresentation.IsLargeStructuralControl(control, surface)) return Brushes.Transparent;
        var color = level switch
        {
            UiUnderstandingLevel.RawWorld => Color.FromRgb(0xF5, 0xF0, 0xFF),
            UiUnderstandingLevel.SemanticWorld => Color.FromRgb(0xEC, 0xFD, 0xF5),
            _ => Color.FromRgb(0xEF, 0xF6, 0xFF)
        };
        var alpha = checked((byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255));
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }
}
