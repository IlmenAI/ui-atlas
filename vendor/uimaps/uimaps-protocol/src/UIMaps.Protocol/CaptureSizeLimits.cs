namespace UIMaps.Protocol.V1;

/// <summary>Capture defaults are distinct from the capacity of readers and transports.</summary>
public static class CaptureSizeLimits
{
    public const int MiB = 1024 * 1024;
    public const int DefaultEpisodeBytes = 256 * MiB;
    public const int MaximumEpisodeBytes = 512 * MiB;
    // Whole-episode RPCs also carry policy, coverage and envelope metadata.
    public const int MaximumMessageBytes = MaximumEpisodeBytes + 16 * MiB;
    public const int MaximumMessageChunks = MaximumMessageBytes / MiB;
}
