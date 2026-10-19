using System.Text.Json;
using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;

namespace UiAtlas.Core.Recording.Windows;

public sealed record RecordingProfileSettings(string ActiveProfileId = "deep-survey", Dictionary<string, string>? Overrides = null);

/// <summary>Atlas-local overrides over the shared Recorder catalog; never changes shipped profiles.</summary>
public sealed class RecordingProfileSettingsStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UiAtlas", "recording-profiles.json");
    public static ConsumerLimits Consumer { get; } = new(16 * 1024 * 1024, CaptureSizeLimits.MaximumEpisodeBytes, true);

    public static IReadOnlyList<CaptureProfileDescriptor> Profiles()
    {
        var catalog = new CaptureProfileCatalogService();
        var mapping = new CapturePolicyResolver(catalog).Resolve(new CapturePolicyRequest { RequestedProfileId = "mapping-checkpoint" }, Consumer);
        return new[] { new CaptureProfileDescriptor { ProfileId = "mapping-checkpoint", ProfileVersion = mapping.ResolvedProfileRef.ProfileVersion,
            DisplayName = "Mapping checkpoint", Description = "Bounded snapshot of the current application state. Use Deep survey for complete UI mapping coverage.",
            DefaultLimits = new CaptureLimits {
                OverallCaptureDeadlineMs = mapping.HardWallClockBudgetMs,
                ScreenCaptureDeadlineMs = mapping.ProviderBudgets.Single(b => b.ProviderId == "screen").HardDurationMs,
                WindowTopologyDeadlineMs = mapping.ProviderBudgets.Single(b => b.ProviderId == "native-window-topology").HardDurationMs,
                UiaPerWindowDeadlineMs = mapping.ProviderBudgets.Single(b => b.ProviderId == "uia").HardDurationMs,
                MaxWindows = mapping.MaxWindows, MaxNodesPerWindow = mapping.MaxNodesPerWindow,
                MaxNodesPerEpisode = mapping.MaxNodesPerEpisode, MaxDepth = mapping.MaxDepth,
                MaxConcurrentUiaWorkers = mapping.SchedulingPolicy.GlobalConcurrency, MaxAttempts = mapping.RetryPolicy.MaxAttempts,
                MaxBytesPerEpisode = mapping.MaxBytesPerEpisode }, CaptureSemantics = CaptureSemantics.Snapshot, DeprecationState = "active" } }
            .Concat(catalog.GetCatalog().Profiles).ToArray();
    }

    public RecordingProfileSettings Load()
    {
        var value = File.Exists(_path) ? JsonSerializer.Deserialize<RecordingProfileSettings>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("Recording profile settings are empty.") : new();
        Validate(value);
        return value;
    }

    public static CaptureLimits LimitsFor(RecordingProfileSettings settings, CaptureProfileDescriptor profile) =>
        settings.Overrides?.TryGetValue(profile.ProfileId, out var json) == true
            ? CaptureLimits.Parser.ParseJson(json) : profile.DefaultLimits.Clone();

    public static CapturePolicyRequest Request(RecordingProfileSettings settings)
    {
        var request = new CapturePolicyRequest { RequestedProfileId = settings.ActiveProfileId,
            VisualControlDetectionEnabled = true, OcrOnVisualControlsEnabled = true };
        if (settings.Overrides?.TryGetValue(settings.ActiveProfileId, out var json) == true)
        { request.UseCustomLimits = true; request.CustomLimits = CaptureLimits.Parser.ParseJson(json); }
        return request;
    }

    public static void Validate(RecordingProfileSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ActiveProfileId)) throw new InvalidDataException("Choose a recording profile.");
        var resolver = new CapturePolicyResolver(new CaptureProfileCatalogService());
        _ = resolver.Resolve(Request(settings), Consumer);
        var catalogResolver = new CapturePolicyResolver(new CaptureProfileCatalogService(deepSurveyEnabled: true));
        foreach (var pair in settings.Overrides ?? [])
            _ = catalogResolver.Resolve(new CapturePolicyRequest { RequestedProfileId = pair.Key, UseCustomLimits = true,
                CustomLimits = CaptureLimits.Parser.ParseJson(pair.Value) }, Consumer);
    }

    public void Save(RecordingProfileSettings settings)
    {
        Validate(settings);
        var destination = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
