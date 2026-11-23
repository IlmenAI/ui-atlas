using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UiAtlas.Core.Recording.Windows;

internal static class WindowEvidencePixels
{
    internal static BitmapSource DecodeOpaquePng(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        using var input = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource bitmap = decoder.Frames[0];
        if (bitmap.Format != PixelFormats.Bgr32)
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
            converted.Freeze();
            bitmap = converted;
        }
        else
        {
            bitmap.Freeze();
        }

        return bitmap;
    }
}
