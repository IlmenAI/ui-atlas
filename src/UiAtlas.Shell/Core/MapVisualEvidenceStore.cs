using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using UIMaps.Protocol.V1;

namespace UIMaps.Application.Core;

public sealed record MapVisualEvidence(string SourceObservationSetRef, byte[] PngBytes, RectPx ScreenshotBounds);

public sealed class MapVisualEvidenceStore(string? root = null)
{
    private readonly string _root = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UIMaps", "map-visual-evidence");

    public MapVisualEvidence? TryLoad(string sourceObservationSetRef)
    {
        if (string.IsNullOrWhiteSpace(sourceObservationSetRef)) return null;
        try
        {
            var stem = Stem(sourceObservationSetRef);
            var pngPath = Path.Combine(_root, stem + ".png");
            var metadataPath = Path.Combine(_root, stem + ".json");
            if (!File.Exists(pngPath) || !File.Exists(metadataPath)) return null;
            var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(metadataPath));
            var png = File.ReadAllBytes(pngPath);
            if (metadata is null || metadata.SourceObservationSetRef != sourceObservationSetRef
                || metadata.Width <= 0 || metadata.Height <= 0 || png.Length is 0 or > 64 * 1024 * 1024) return null;
            return new MapVisualEvidence(sourceObservationSetRef, png, new RectPx { X = metadata.X, Y = metadata.Y, Width = metadata.Width, Height = metadata.Height });
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    internal static string Stem(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed record Metadata(string SourceObservationSetRef, int X, int Y, int Width, int Height);
}
