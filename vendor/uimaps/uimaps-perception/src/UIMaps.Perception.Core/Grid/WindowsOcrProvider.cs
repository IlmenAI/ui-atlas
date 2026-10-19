using System.Runtime.InteropServices.WindowsRuntime;
using UIMaps.Protocol.V1;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace UIMaps.Perception.Core.Grid;

public sealed class WindowsOcrProvider : IGridOcrProvider
{
    public async Task<IReadOnlyList<GridOcrToken>> ReadAsync(byte[] exactCropPng, CancellationToken cancellationToken)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? throw new GridAnalysisException("GRID_OCR_UNAVAILABLE", "Windows OCR is unavailable for the current language profile.");
        var observations = new List<GridOcrToken>();
        var successfulPasses = 0;
        foreach (var (scale, pass) in new[] { (1d, "native"), (2d, "2x") })
        {
            try
            {
                observations.AddRange(await RecognizeAsync(engine, exactCropPng, scale, pass, cancellationToken).ConfigureAwait(false));
                successfulPasses++;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A successful pass remains authoritative evidence. The provider
                // fails only when every deterministic pass fails.
            }
        }
        if (successfulPasses == 0) throw new GridAnalysisException("GRID_OCR_FAILED", "Every local OCR pass failed for the exact crop.");
        return Deduplicate(observations);
    }

    private static async Task<IReadOnlyList<GridOcrToken>> RecognizeAsync(OcrEngine engine, byte[] png, double requestedScale, string pass, CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
        var maximumDimension = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
        var scale = Math.Min(requestedScale, maximumDimension == 0 ? 1d : (double)OcrEngine.MaxImageDimension / maximumDimension);
        if (scale < 1.15) scale = 1d;
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Cubic,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        return result.Lines.SelectMany(x => x.Words).Select(word => new GridOcrToken(
            word.Text,
            new RectPx
            {
                X = (int)Math.Floor(word.BoundingRect.X / scale),
                Y = (int)Math.Floor(word.BoundingRect.Y / scale),
                Width = Math.Max(1, (int)Math.Ceiling(word.BoundingRect.Width / scale)),
                Height = Math.Max(1, (int)Math.Ceiling(word.BoundingRect.Height / scale)),
            },
            scale > 1 ? .84 : .80,
            $"windows-media-ocr:{pass}",
            "10.0-multipass-v1")).ToList();
    }

    internal static IReadOnlyList<GridOcrToken> Deduplicate(IEnumerable<GridOcrToken> values)
    {
        var retained = new List<GridOcrToken>();
        foreach (var candidate in values.Where(x => !string.IsNullOrWhiteSpace(x.OriginalText) && x.Bounds.Width > 0 && x.Bounds.Height > 0)
                     .OrderByDescending(x => x.Confidence).ThenByDescending(x => x.OriginalText.Trim().Length))
        {
            var duplicateIndex = retained.FindIndex(existing => SameObservation(existing, candidate));
            if (duplicateIndex < 0)
            {
                retained.Add(candidate);
                continue;
            }
            var existing = retained[duplicateIndex];
            if (candidate.Confidence > existing.Confidence || candidate.OriginalText.Trim().Length > existing.OriginalText.Trim().Length)
                retained[duplicateIndex] = candidate;
        }
        return retained.OrderBy(x => x.Bounds.Y + x.Bounds.Height / 2).ThenBy(x => x.Bounds.X).ToList();
    }

    private static bool SameObservation(GridOcrToken left, GridOcrToken right)
    {
        var intersectionWidth = Math.Max(0, Math.Min(left.Bounds.X + left.Bounds.Width, right.Bounds.X + right.Bounds.Width) - Math.Max(left.Bounds.X, right.Bounds.X));
        var intersectionHeight = Math.Max(0, Math.Min(left.Bounds.Y + left.Bounds.Height, right.Bounds.Y + right.Bounds.Height) - Math.Max(left.Bounds.Y, right.Bounds.Y));
        var intersection = intersectionWidth * intersectionHeight;
        var minimumArea = Math.Min(left.Bounds.Width * left.Bounds.Height, right.Bounds.Width * right.Bounds.Height);
        if (minimumArea <= 0 || (double)intersection / minimumArea < .62) return false;
        var normalizedLeft = string.Concat(left.OriginalText.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        var normalizedRight = string.Concat(right.OriginalText.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        return normalizedLeft == normalizedRight
               || normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal)
               || normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal)
               || SimilarText(normalizedLeft, normalizedRight);
    }

    private static bool SimilarText(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0 || Math.Abs(left.Length - right.Length) > 2) return false;
        var commonPrefix = 0;
        while (commonPrefix < Math.Min(left.Length, right.Length) && left[commonPrefix] == right[commonPrefix]) commonPrefix++;
        return commonPrefix >= Math.Max(2, Math.Min(left.Length, right.Length) * 3 / 5);
    }
}
