using UIMaps.Protocol.V1;

namespace UIMaps.Perception.Core.Grid;

// Extracted from GridAnalysisEngine.cs at the pinned source revision.
// Keeping the local OCR contract separate avoids importing the remote grid engine.
public sealed record GridOcrToken(string OriginalText, RectPx Bounds, double Confidence, string ProviderId = "local-ocr", string ProviderVersion = "1");

public interface IGridOcrProvider
{
    Task<IReadOnlyList<GridOcrToken>> ReadAsync(byte[] exactCropPng, CancellationToken cancellationToken);
}

public sealed class GridAnalysisException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
