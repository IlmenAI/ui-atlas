using UIMaps.Protocol.V1;

namespace UIMaps.Recorder.Windows;

public sealed record RecorderPolicy(
    TimeSpan OverallCaptureDeadline,
    TimeSpan ScreenCaptureDeadline,
    TimeSpan WindowTopologyDeadline,
    TimeSpan UiaPerWindowDeadline,
    TimeSpan UiaAggregateDeadline,
    int MaxUiaNodes,
    int MaxUiaDepth,
    int MaxWindows,
    int MaxConcurrentUiaWorkers,
    long MaxBundleSize)
{
    public CaptureSemantics CaptureSemantics { get; init; } = CaptureSemantics.Snapshot;
    public bool ExhaustiveCoverage { get; init; }
    public bool MappingCheckpoint { get; init; }
    public bool CaptureMsaa { get; init; }
    public int MaxNodesPerEpisode { get; init; } = 10_000;
    public int PerProcessConcurrency { get; init; } = 1;
    public int MaxAttempts { get; init; } = 1;
    public long MaxBytesPerSegment { get; init; } = 16L * 1024 * 1024;
    public IReadOnlySet<string> TargetWindowHandleEvidence { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public string TargetProcessInstanceEvidence { get; init; } = string.Empty;
    public static RecorderPolicy Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromSeconds(3),
        2_500,
        12,
        20,
        2,
        CaptureSizeLimits.DefaultEpisodeBytes);

    public static RecorderPolicy FromProtocol(CapturePolicy? value)
    {
        if (value is null)
        {
            return Default;
        }

        static TimeSpan Bound(int value, TimeSpan fallback, int maximumMs) =>
            value <= 0 ? fallback : TimeSpan.FromMilliseconds(Math.Min(value, maximumMs));

        return new RecorderPolicy(
            Bound(value.OverallCaptureDeadlineMs, Default.OverallCaptureDeadline, 30_000),
            Bound(value.ScreenCaptureDeadlineMs, Default.ScreenCaptureDeadline, 10_000),
            Bound(value.WindowTopologyDeadlineMs, Default.WindowTopologyDeadline, 10_000),
            Bound(value.UiaPerWindowDeadlineMs, Default.UiaPerWindowDeadline, 10_000),
            Bound(value.UiaAggregateDeadlineMs, Default.UiaAggregateDeadline, 20_000),
            Math.Clamp(value.MaxUiaNodes == 0 ? Default.MaxUiaNodes : value.MaxUiaNodes, 1, 100_000),
            Math.Clamp(value.MaxUiaDepth == 0 ? Default.MaxUiaDepth : value.MaxUiaDepth, 1, 100),
            Math.Clamp(value.MaxWindows == 0 ? Default.MaxWindows : value.MaxWindows, 1, 1_000),
            Math.Clamp(value.MaxConcurrentUiaWorkers == 0 ? Default.MaxConcurrentUiaWorkers : value.MaxConcurrentUiaWorkers, 1, 16),
            Math.Clamp(value.MaxBundleSize == 0 ? Default.MaxBundleSize : value.MaxBundleSize, CaptureSizeLimits.MiB, CaptureSizeLimits.MaximumEpisodeBytes));
    }

    public CapturePolicy ToProtocol() => new()
    {
        OverallCaptureDeadlineMs = ExhaustiveCoverage ? 0 : (int)OverallCaptureDeadline.TotalMilliseconds,
        ScreenCaptureDeadlineMs = (int)ScreenCaptureDeadline.TotalMilliseconds,
        WindowTopologyDeadlineMs = (int)WindowTopologyDeadline.TotalMilliseconds,
        UiaPerWindowDeadlineMs = UiaPerWindowDeadline == Timeout.InfiniteTimeSpan ? 0 : (int)UiaPerWindowDeadline.TotalMilliseconds,
        UiaAggregateDeadlineMs = (int)UiaAggregateDeadline.TotalMilliseconds,
        MaxUiaNodes = ExhaustiveCoverage ? 0 : MaxUiaNodes,
        MaxUiaDepth = ExhaustiveCoverage ? 0 : MaxUiaDepth,
        MaxWindows = ExhaustiveCoverage ? 0 : MaxWindows,
        MaxConcurrentUiaWorkers = MaxConcurrentUiaWorkers,
        MaxBundleSize = MaxBundleSize,
    };

    public static RecorderPolicy FromEffective(EffectiveCapturePolicy value)
    {
        var screen = value.ProviderBudgets.FirstOrDefault(x => x.ProviderId == "screen")?.HardDurationMs ?? 1_000;
        var topology = value.ProviderBudgets.FirstOrDefault(x => x.ProviderId == "native-window-topology")?.HardDurationMs ?? 1_000;
        var uia = value.ProviderBudgets.FirstOrDefault(x => x.ProviderId == "uia")?.HardDurationMs ?? 750;
        var exhaustive = value.ExhaustiveCoverage;
        // Zero is the exhaustive profile's explicit completion-or-cancellation policy,
        // not an immediate timeout. Bounded profiles cannot resolve this setting.
        return new RecorderPolicy(
            exhaustive ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(value.HardWallClockBudgetMs),
            TimeSpan.FromMilliseconds(screen), TimeSpan.FromMilliseconds(topology),
            exhaustive && uia == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(uia),
            exhaustive ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(Math.Max(uia, value.HardWallClockBudgetMs - 1_000)),
            exhaustive ? int.MaxValue : value.MaxNodesPerWindow,
            exhaustive ? int.MaxValue : value.MaxDepth,
            exhaustive ? int.MaxValue : value.MaxWindows,
            Math.Max(1, value.SchedulingPolicy?.GlobalConcurrency ?? 1), value.MaxBytesPerEpisode)
        {
            CaptureSemantics = value.CaptureSemantics,
            MappingCheckpoint = value.ResolvedProfileRef?.ProfileId == "mapping-checkpoint",
            CaptureMsaa = value.ProviderBudgets.Any(budget => budget.ProviderId == "msaa"),
            ExhaustiveCoverage = exhaustive,
            MaxNodesPerEpisode = exhaustive ? int.MaxValue : value.MaxNodesPerEpisode,
            PerProcessConcurrency = Math.Max(1, value.SchedulingPolicy?.PerProcessConcurrency ?? 1),
            MaxAttempts = Math.Max(1, value.RetryPolicy?.MaxAttempts ?? 1),
            MaxBytesPerSegment = value.MaxBytesPerSegment,
            TargetWindowHandleEvidence = value.CaptureScope?.TargetWindowHandleEvidence.ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal),
            TargetProcessInstanceEvidence = value.CaptureScope?.TargetProcessInstanceEvidence ?? string.Empty,
        };
    }
}
