using System.Reflection;
using System.Text.Json;
using UIMaps.Protocol.V1;

namespace UIMaps.Recorder.Windows;

internal sealed record ProfileManifest(
    string Id, string Version, string Name, string Semantics,
    long MinMs, long MaxMs, long BudgetMs, long WindowMs,
    int MaxWindows, int MaxNodesPerWindow, int MaxNodesPerEpisode,
    int MaxDepth, int GlobalConcurrency, int PerProcessConcurrency,
    bool Progressive, int Retries, bool ExhaustiveCoverage = false);

internal sealed record ProfileCatalogManifest(string CatalogVersion, ProfileManifest[] Profiles);

public sealed class CaptureProfileCatalogService
{
    private readonly ProfileCatalogManifest _manifest;
    private readonly bool _deepSurveyEnabled;

    public CaptureProfileCatalogService(bool? deepSurveyEnabled = null)
    {
        _deepSurveyEnabled = deepSurveyEnabled ?? !string.Equals(Environment.GetEnvironmentVariable("UIMAPS_ENABLE_DEEP_SURVEY"), "0", StringComparison.Ordinal);
        var assembly = typeof(CaptureProfileCatalogService).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(x => x.EndsWith("capture-profiles.v1.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException("Capture profile catalog is unavailable.");
        _manifest = JsonSerializer.Deserialize<ProfileCatalogManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Capture profile catalog is invalid.");
        Validate(_manifest);
    }

    public CaptureProfileCatalog GetCatalog()
    {
        var catalog = new CaptureProfileCatalog
        {
            CatalogVersion = _manifest.CatalogVersion,
            GeneratedAt = "2026-08-27T00:00:00.0000000Z",
            RecorderVersion = "0.2.0",
            DefaultProfileRef = new ProfileRef { ProfileId = "balanced", ProfileVersion = "1.0.0" },
        };
        // Attended mapping performs additional verification after acquisition.
        // Its acquisition profile is not a generic snapshot/survey UI detent.
        catalog.Profiles.AddRange(_manifest.Profiles.Where(profile => profile.Id != "mapping-checkpoint").Select(ToDescriptor));
        return catalog;
    }

    internal ProfileManifest Resolve(string? id, string? version)
    {
        id = string.IsNullOrWhiteSpace(id) ? "balanced" : id;
        if (id == "deep-survey" && !_deepSurveyEnabled) throw new RecorderException("FEATURE_DISABLED", "Deep survey is disabled by the Recorder rollout feature flag.");
        var candidates = _manifest.Profiles.Where(x => string.Equals(x.Id, id, StringComparison.Ordinal)).ToList();
        if (candidates.Count == 0) throw new RecorderException("PROFILE_NOT_FOUND", "The requested capture profile is not in the Recorder catalog.");
        if (!string.IsNullOrWhiteSpace(version))
            return candidates.SingleOrDefault(x => string.Equals(x.Version, version, StringComparison.Ordinal))
                ?? throw new RecorderException("PROFILE_VERSION_UNSUPPORTED", "The requested capture profile version is not supported.");
        return candidates.OrderByDescending(x => Version.Parse(x.Version)).First();
    }

    private CaptureProfileDescriptor ToDescriptor(ProfileManifest value) => new()
    {
        ProfileId = value.Id, ProfileVersion = value.Version, DisplayName = value.Name,
        Description = value.Semantics == "survey" ? "Passive staged coverage with explicit intervals and omissions." : "Temporally coherent passive display snapshot.",
        CaptureSemantics = value.Semantics == "survey" ? CaptureSemantics.Survey : CaptureSemantics.Snapshot,
        IntendedDurationMinMs = value.MinMs, IntendedDurationMaxMs = value.MaxMs,
        SupportsProgressiveResults = value.Progressive,
        ProviderSummary = value.ExhaustiveCoverage
            ? "Native screenshot/topology, UIA and MSAA until the observable frontier is exhausted; cancellation and provider failures remain explicitly accounted."
            : $"Native screenshot/topology and UIA to depth {value.MaxDepth}.",
        RetryFallbackSummary = value.Retries > 1 ? "Fresh-worker retry; native evidence retained on UIA failure." : "No provider retry; native evidence retained.",
        IsDefaultCandidate = value.Id == "balanced", DeprecationState = value.Id == "deep-survey" && !_deepSurveyEnabled ? "disabled_by_feature_flag" : "active",
        DefaultLimits = DefaultLimitsFor(value),
        ExhaustiveCoverage = value.ExhaustiveCoverage,
    };

    internal static CaptureLimits DefaultLimitsFor(ProfileManifest value) => new()
    {
        OverallCaptureDeadlineMs = value.ExhaustiveCoverage ? 0 : value.BudgetMs,
        ScreenCaptureDeadlineMs = Math.Min(value.Id == "mapping-checkpoint" ? 3_000 : 1_000, value.BudgetMs),
        WindowTopologyDeadlineMs = value.Semantics == "survey"
            ? Math.Clamp(value.BudgetMs / 5, 1_000L, Math.Min(15_000L, value.BudgetMs))
            : Math.Clamp(value.BudgetMs / 5, 1_000L, Math.Min(3_000L, value.BudgetMs)),
        UiaPerWindowDeadlineMs = value.WindowMs,
        MaxWindows = value.ExhaustiveCoverage ? 0 : value.MaxWindows,
        MaxNodesPerWindow = value.ExhaustiveCoverage ? 0 : value.MaxNodesPerWindow,
        MaxNodesPerEpisode = value.ExhaustiveCoverage ? 0 : value.MaxNodesPerEpisode,
        MaxDepth = value.ExhaustiveCoverage ? 0 : value.MaxDepth,
        MaxConcurrentUiaWorkers = value.GlobalConcurrency,
        MaxAttempts = value.Retries,
        MaxBytesPerEpisode = CaptureSizeLimits.DefaultEpisodeBytes,
    };

    private static void Validate(ProfileCatalogManifest value)
    {
        if (string.IsNullOrWhiteSpace(value.CatalogVersion) || value.Profiles.Length < 6 || value.Profiles.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != value.Profiles.Length)
            throw new InvalidDataException("Capture profile catalog failed validation.");
        if (value.Profiles.Any(x => x.BudgetMs <= 0 || x.MaxNodesPerWindow <= 0 || x.MaxNodesPerEpisode < x.MaxNodesPerWindow || x.PerProcessConcurrency != 1))
            throw new InvalidDataException("Capture profile policy values failed validation.");
    }
}

public sealed class CapturePolicyResolver(CaptureProfileCatalogService catalog)
{
    public EffectiveCapturePolicy Resolve(CapturePolicyRequest request, ConsumerLimits consumer)
    {
        ValidateScope(request.RequestedScope);
        var profile = catalog.Resolve(request.RequestedProfileId, request.RequestedProfileVersion);
        var minimumDepth = request.RequestedScope?.MinimumUiaDepth ?? 0;
        if (minimumDepth < 0 || minimumDepth > 100)
            throw new RecorderException("CAPTURE_SCOPE_INVALID", "The requested minimum UIA depth is outside the supported range.");
        var maxSegment = Math.Min(16L * 1024 * 1024, consumer.MaxSegmentBytes <= 0 ? 16L * 1024 * 1024 : consumer.MaxSegmentBytes);
        var maxEpisode = Math.Min(CaptureSizeLimits.MaximumEpisodeBytes,
            consumer.MaxEpisodeBytes <= 0 ? CaptureSizeLimits.DefaultEpisodeBytes : consumer.MaxEpisodeBytes);
        if (profile.Progressive && !consumer.SupportsSegments)
            throw new RecorderException("CAPABILITY_INCOMPATIBLE", "The requested Survey profile requires segmented downstream ingest.");
        if (profile.Semantics == "survey" && !consumer.SupportsSurvey)
            throw new RecorderException("CAPABILITY_INCOMPATIBLE", "The downstream endpoint does not support Survey publication semantics.");
        if (maxSegment < 64 * 1024 || maxEpisode < maxSegment)
            throw new RecorderException("CAPABILITY_INCOMPATIBLE", "Downstream ingest limits cannot represent the requested profile.");

        var exhaustive = profile.ExhaustiveCoverage;
        var limits = ResolveLimits(profile, request, maxEpisode);
        var visualControlDetectionEnabled = request.VisualControlDetectionEnabled;
        var ocrOnVisualControlsEnabled = visualControlDetectionEnabled && request.OcrOnVisualControlsEnabled;
        if (!exhaustive && limits.MaxDepth < minimumDepth)
            throw new RecorderException("PROFILE_DEPTH_INCOMPATIBLE", $"Profile '{profile.Id}' reaches UIA depth {limits.MaxDepth}, below the requested depth {minimumDepth}.");

        var scope = request.RequestedScope?.Clone() ?? PassiveScope(string.Empty);
        var policy = new EffectiveCapturePolicy
        {
            RequestedProfileRef = new ProfileRef { ProfileId = request.RequestedProfileId, ProfileVersion = request.RequestedProfileVersion },
            ResolvedProfileRef = new ProfileRef { ProfileId = profile.Id, ProfileVersion = profile.Version },
            CatalogVersion = catalog.GetCatalog().CatalogVersion, ResolverVersion = "phase1-resolver/1",
            CaptureSemantics = profile.Semantics == "survey" ? CaptureSemantics.Survey : CaptureSemantics.Snapshot,
            CaptureScope = scope, HardWallClockBudgetMs = limits.OverallCaptureDeadlineMs,
            MaxWindows = limits.MaxWindows, MaxNodesPerWindow = limits.MaxNodesPerWindow,
            MaxNodesPerEpisode = limits.MaxNodesPerEpisode, MaxDepth = limits.MaxDepth,
            MaxBytesPerSegment = maxSegment, MaxBytesPerEpisode = limits.MaxBytesPerEpisode,
            SchedulingPolicy = new SchedulingPolicy { StrategyId = exhaustive ? "coverage-exhaustive/1" : "coverage-staged/1", GlobalConcurrency = limits.MaxConcurrentUiaWorkers, PerProcessConcurrency = profile.PerProcessConcurrency, ForegroundPriority = 100, VisiblePriority = 50, DisplayIntersectionPriority = 25, UnfinishedFrontierPriority = 75, RecoveryPriority = 10 },
            SynchronizationPolicy = new SynchronizationPolicy { FrameAnchorSource = "screenshot", TargetToleranceMs = profile.Id == "live" ? 100 : 250, HardAttachmentToleranceMs = profile.Id == "balanced" ? 1000 : 500, StaleEvidencePolicy = "retain_in_episode_exclude_from_frame" },
            RetryPolicy = new RetryPolicy { MaxAttempts = limits.MaxAttempts, RequireFreshWorker = true, StopRetryAtRemainingBudgetMs = exhaustive ? 0 : 500 },
            FallbackPolicy = new FallbackPolicy { MaxFallbackAttempts = 0, PreservePrimaryFailure = true },
            CoverageTarget = profile.Semantics == "survey" ? "all_eligible_passive_evidence_accounted" : "temporally_coherent_priority_evidence",
            CheckpointPolicy = new CheckpointPolicy { Enabled = profile.Progressive, AfterEachPass = profile.Progressive, IntervalMs = 10_000 },
            ResolvedAt = DateTimeOffset.UtcNow.ToString("O"),
            ExhaustiveCoverage = exhaustive,
            VisualControlDetectionEnabled = visualControlDetectionEnabled,
            OcrOnVisualControlsEnabled = ocrOnVisualControlsEnabled,
        };
        policy.ProviderBudgets.Add(new ProviderBudget { ProviderId = "screen", HardDurationMs = limits.ScreenCaptureDeadlineMs, MaxAttempts = 1, MaxBytes = maxSegment });
        policy.ProviderBudgets.Add(new ProviderBudget { ProviderId = "native-window-topology", HardDurationMs = limits.WindowTopologyDeadlineMs, MaxAttempts = 1 });
        policy.ProviderBudgets.Add(new ProviderBudget { ProviderId = "uia", HardDurationMs = limits.UiaPerWindowDeadlineMs, MaxAttempts = limits.MaxAttempts, MaxConcurrentGlobalContribution = limits.MaxConcurrentUiaWorkers, MaxNodes = limits.MaxNodesPerWindow, MaxDepth = limits.MaxDepth });
        if (profile.Id == "mapping-checkpoint" || profile.ExhaustiveCoverage)
            policy.ProviderBudgets.Add(new ProviderBudget { ProviderId = "msaa", HardDurationMs = limits.UiaPerWindowDeadlineMs,
                MaxAttempts = 1, MaxConcurrentGlobalContribution = limits.MaxConcurrentUiaWorkers, MaxNodes = limits.MaxNodesPerWindow, MaxDepth = limits.MaxDepth });
        if (string.IsNullOrWhiteSpace(request.RequestedProfileId))
            policy.Adjustments.Add(new EffectivePolicyAdjustment { Code = "legacy_request_compatibility", SafeDescription = "An unnamed request resolved to the documented Balanced compatibility profile." });
        if (maxSegment < 16L * 1024 * 1024)
            policy.Adjustments.Add(new EffectivePolicyAdjustment { Code = "consumer_segment_limit", SafeDescription = "Segment size was reduced to the negotiated downstream limit." });
        if (scope.TargetWindowHandleEvidence.Count > 0)
            policy.Adjustments.Add(new EffectivePolicyAdjustment { Code = "target_window_priority", SafeDescription = "The passively observed target window is prioritized for provider scheduling." });
        if (request.UseCustomLimits)
            policy.Adjustments.Add(new EffectivePolicyAdjustment { Code = "user_custom_limits", SafeDescription = "Validated user capture-limit overrides were applied." });
        if (request.OcrOnVisualControlsEnabled && !visualControlDetectionEnabled)
            policy.Adjustments.Add(new EffectivePolicyAdjustment { Code = "visual_ocr_dependency_coerced_off", SafeDescription = "OCR-on-visual-controls was disabled because visual control detection is Off." });
        if (visualControlDetectionEnabled)
            policy.Adjustments.Add(new EffectivePolicyAdjustment { Code = "capture_wide_visual_control_detection_opt_in", SafeDescription = ocrOnVisualControlsEnabled
                ? "Capture-wide visual control detection and OCR were explicitly enabled."
                : "Capture-wide visual control detection was explicitly enabled without OCR." });
        if (exhaustive)
            policy.Adjustments.Add(new EffectivePolicyAdjustment { Code = "exhaustive_coverage", SafeDescription = "Window, node, depth, and overall-duration profile ceilings are disabled; cancellation and accounted provider outcomes terminate the survey." });
        if (limits.MaxBytesPerEpisode < (request.CustomLimits?.MaxBytesPerEpisode ?? maxEpisode))
            policy.Adjustments.Add(new EffectivePolicyAdjustment { Code = "consumer_episode_limit", SafeDescription = "The episode byte ceiling was reduced to the negotiated downstream transport limit." });
        return policy;
    }

    private static CaptureLimits ResolveLimits(ProfileManifest profile, CapturePolicyRequest request, long consumerEpisodeLimit)
    {
        var defaults = CaptureProfileCatalogService.DefaultLimitsFor(profile);
        var requested = request.UseCustomLimits
            ? request.CustomLimits ?? throw new RecorderException("CAPTURE_LIMITS_INVALID", "Custom limits were enabled without a limit configuration.")
            : defaults;
        ValidateLimits(requested, profile.ExhaustiveCoverage);
        var resolved = requested.Clone();
        resolved.MaxBytesPerEpisode = Math.Min(resolved.MaxBytesPerEpisode, consumerEpisodeLimit);
        if (profile.ExhaustiveCoverage)
        {
            resolved.OverallCaptureDeadlineMs = 0;
            resolved.MaxWindows = 0;
            resolved.MaxNodesPerWindow = 0;
            resolved.MaxNodesPerEpisode = 0;
            resolved.MaxDepth = 0;
        }
        return resolved;
    }

    private static void ValidateLimits(CaptureLimits value, bool exhaustive)
    {
        static bool Between(long value, long minimum, long maximum) => value >= minimum && value <= maximum;
        if ((!exhaustive && !Between(value.OverallCaptureDeadlineMs, 250, 86_400_000))
            || !Between(value.ScreenCaptureDeadlineMs, 100, 300_000)
            || !Between(value.WindowTopologyDeadlineMs, 100, 300_000)
            || (!(exhaustive && value.UiaPerWindowDeadlineMs == 0) && !Between(value.UiaPerWindowDeadlineMs, 100, 300_000))
            || (!exhaustive && (value.MaxWindows is < 1 or > 100_000
                || value.MaxNodesPerWindow is < 1 or > 2_000_000
                || value.MaxNodesPerEpisode < value.MaxNodesPerWindow
                || value.MaxDepth is < 1 or > 512))
            || value.MaxConcurrentUiaWorkers is < 1 or > 16
            || value.MaxAttempts is < 1 or > 5
            || !Between(value.MaxBytesPerEpisode, CaptureSizeLimits.MiB, CaptureSizeLimits.MaximumEpisodeBytes))
            throw new RecorderException("CAPTURE_LIMITS_INVALID", "One or more custom capture limits are outside the supported range.");
    }

    public static CaptureScope PassiveScope(string displayId) => new()
    {
        SelectedDisplayId = displayId, InteractiveSessionEvidence = Environment.ProcessId.ToString(),
        ObservableStateOnly = true, PassiveReadOnly = true,
    };

    private static void ValidateScope(CaptureScope? scope)
    {
        if (scope is null) return;
        if (!scope.ObservableStateOnly || !scope.PassiveReadOnly || scope.AllowElevation || scope.AllowInputInjection || scope.AllowNavigation || scope.AllowScrolling || scope.AllowExpansion || scope.AllowTabSwitching)
            throw new RecorderException("CAPTURE_SCOPE_UNSUPPORTED", "Phase 1 supports only passive, read-only observation of current observable state.");
    }
}

public sealed record ConsumerLimits(long MaxSegmentBytes, long MaxEpisodeBytes, bool SupportsSegments, bool SupportsSurvey = true);
