using UIMaps.Protocol.V1;

namespace UIMaps.Perception.Core;

public sealed record PerceptionPolicy(
    TimeSpan FrameSynchronizationTolerance,
    int SnapshotRetentionLimit,
    int MaximumPacketCount,
    int MaximumObjectCount,
    long MaximumBundleBytes)
{
    public static PerceptionPolicy Default { get; } = new(
        TimeSpan.FromMilliseconds(250),
        32,
        10_000,
        200_000,
        // Ingest every supported saved capture, even if new recordings use a lower limit.
        CaptureSizeLimits.MaximumEpisodeBytes);
}
public sealed class PerceptionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
