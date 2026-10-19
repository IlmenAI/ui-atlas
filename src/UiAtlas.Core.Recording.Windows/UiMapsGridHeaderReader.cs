using UiAtlas.Core.Contracts;
using UIMaps.Perception.Core.Grid;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Uses the same local OCR provider as UI Maps Perception. Only the
/// explicitly selected header cells are passed to it.</summary>
public sealed class UiMapsGridHeaderReader : IGridSchemaHeaderReader
{
    public string Name => "uimaps-windows-ocr";
    public async Task<IReadOnlyList<GridHeaderText>> ReadAsync(IReadOnlyList<GridHeaderImage> images, CancellationToken token)
    {
        var provider = new WindowsOcrProvider();
        var values = new List<GridHeaderText>();
        foreach (var image in images)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var words = await provider.ReadAsync(image.Png, token).ConfigureAwait(false);
                if (words.Count == 0)
                    words = await provider.ReadAsync(EnlargeHeader(image.Png), token).ConfigureAwait(false);
                var text = string.Join(" ", words.OrderBy(word => word.Bounds.Y).ThenBy(word => word.Bounds.X).Select(word => word.OriginalText));
                // No OCR tokens does not prove that a header is blank.
                values.Add(new(image.ColumnKey, string.IsNullOrWhiteSpace(text) ? GridCellReadStatus.Unreadable : GridCellReadStatus.Text,
                    string.IsNullOrWhiteSpace(text) ? null : text));
            }
            catch (GridAnalysisException) { values.Add(new(image.ColumnKey, GridCellReadStatus.Unreadable, null)); }
        }
        return values;
    }

    private static byte[] EnlargeHeader(byte[] png)
    {
        // Deterministic enlargement for small header text. It does not replace
        // retained source pixels or infer a label when the shared OCR is silent.
        using var input = new System.IO.MemoryStream(png);
        var source = System.Windows.Media.Imaging.BitmapFrame.Create(input,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var scale = Math.Min(4d, (4000d - 16) / Math.Max(source.PixelWidth, source.PixelHeight));
        scale = Math.Max(1, scale);
        var width = checked((int)Math.Ceiling(source.PixelWidth * scale) + 16);
        var height = checked((int)Math.Ceiling(source.PixelHeight * scale) + 16);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(0, 0, width, height));
            drawing.DrawImage(source, new System.Windows.Rect(8, 8, source.PixelWidth * scale, source.PixelHeight * scale));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = new System.IO.MemoryStream(); encoder.Save(output); return output.ToArray();
    }
}
