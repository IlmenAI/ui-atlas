using UIMaps.Protocol.V1;

namespace UIMaps.Recorder.Windows;

public interface IRecorderClock
{
    long Timestamp { get; }
    long Frequency { get; }
    DateTimeOffset UtcNow { get; }
}
public sealed class SystemRecorderClock : IRecorderClock
{
    public long Timestamp => System.Diagnostics.Stopwatch.GetTimestamp();
    public long Frequency => System.Diagnostics.Stopwatch.Frequency;
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IDisplayCatalog
{
    Task<IReadOnlyList<DisplayDescriptor>> ListAsync(CancellationToken cancellationToken);
}

public interface IScreenCaptureProvider
{
    Task<ScreenshotPacket> CaptureAsync(DisplayDescriptor display, RecorderPolicy policy, CancellationToken cancellationToken);
}

public interface IWindowTopologyProvider
{
    Task<WindowTopologyPacket> CaptureAsync(DisplayDescriptor display, RecorderPolicy policy, CancellationToken cancellationToken);
}

public interface IAccessibilityProbe
{
    Task<AccessibilityPacket> CaptureAsync(IReadOnlyList<WindowObservationRecord> windows, RecorderPolicy policy, CancellationToken cancellationToken);
}

public sealed class RecorderException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public static class ProtocolHealth
{
    public static Health Complete() => new() { State = HealthState.Complete };

    public static Health Single(HealthState state, string code, string safeMessage, string source, bool retryable = false)
    {
        var health = new Health { State = state };
        health.Issues.Add(new HealthIssue
        {
            State = state,
            Code = code,
            SafeMessage = safeMessage,
            Source = source,
            Retryable = retryable,
        });
        return health;
    }

    public static Health Aggregate(IEnumerable<Health> values)
    {
        var issues = values.Where(x => x is not null).SelectMany(x => x.Issues).Select(x => x.Clone()).ToList();
        var state = issues.Count == 0 ? HealthState.Complete : issues.Any(x => x.State == HealthState.Invalid) ? HealthState.Invalid : HealthState.Partial;
        var health = new Health { State = state };
        health.Issues.AddRange(issues);
        return health;
    }
}
