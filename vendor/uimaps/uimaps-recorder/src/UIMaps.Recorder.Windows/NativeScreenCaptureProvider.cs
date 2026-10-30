using System.Drawing;
using System.Drawing.Imaging;
using Google.Protobuf;
using UIMaps.Protocol.V1;

namespace UIMaps.Recorder.Windows;

public sealed class NativeScreenCaptureProvider(Func<Bitmap, ByteString>? encodePng = null) : IScreenCaptureProvider
{
    public Task<ScreenshotPacket> CaptureAsync(DisplayDescriptor display, RecorderPolicy policy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (display.WidthPx <= 0 || display.HeightPx <= 0)
        {
            throw new RecorderException("RECORDER_SCREEN_UNAVAILABLE", "Selected display has invalid bounds.");
        }

        using var bitmap = new Bitmap(display.WidthPx, display.HeightPx, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(display.BoundsPx.X, display.BoundsPx.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var png = encodePng is null ? Encode(bitmap) : encodePng(bitmap);
        return Task.FromResult(new ScreenshotPacket
        {
            Display = display.Clone(),
            ImageEncoding = "image/png",
            WidthPx = display.WidthPx,
            HeightPx = display.HeightPx,
            Stride = display.WidthPx * 4,
            PixelFormat = "BGRA32_PREMULTIPLIED",
            ImageBlob = png,
            Health = ProtocolHealth.Complete(),
        });
    }

    private static ByteString Encode(Bitmap bitmap)
    {
        using var buffer = new MemoryStream(); bitmap.Save(buffer, ImageFormat.Png);
        return ByteString.CopyFrom(buffer.ToArray());
    }
}
