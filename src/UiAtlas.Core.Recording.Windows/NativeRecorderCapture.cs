using Google.Protobuf;
using UiAtlas.Core.Contracts;
using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>The native episode is the acquisition authority. Atlas observations
/// are only its presentation projection.</summary>
public sealed class NativeRecorderCapture
{
    private readonly string _providerPath;
    private readonly CapturePolicyRequest _profileRequest;
    public NativeRecorderCapture(string? providerPath = null, RecordingProfileSettingsStore? settingsStore = null)
    {
        _profileRequest = RecordingProfileSettingsStore.Request((settingsStore ?? new()).Load());
        _providerPath = Path.GetFullPath(providerPath ?? Path.Combine(AppContext.BaseDirectory,
            "native-recorder", "UIMaps.Recorder.ProviderProbe.dll"));
        if (!File.Exists(_providerPath)) throw new FileNotFoundException("The UI Maps native provider worker is missing. Build or install the complete Atlas package.", _providerPath);
    }

    internal CapturePolicyRequest CreateRequest(CaptureScope scope)
    {
        var request = _profileRequest.Clone();
        request.RequestedScope = scope.Clone();
        return request;
    }

    public async Task<SensorEpisodeBundle> CaptureAsync(WindowTarget target, CancellationToken cancellationToken,
        Action<string>? progress = null,
        Func<DateTimeOffset, bool>? inputObserved = null,
        Func<Func<CancellationToken, Task<ScreenshotPacket>>, CancellationToken, Task<ScreenshotPacket>>? screenshotGuard = null)
    {
        if (!WindowCatalog.IsSameProcessWindow(target, target.Hwnd)) throw new InvalidOperationException("The selected application instance changed.");
        var scoped = WindowCatalog.ListScopedWindows(target).Where(WindowSnapshotCapture.IsCapturable).ToArray();
        if (scoped.Length == 0) throw new InvalidOperationException("No visible target window is available.");
        var bounds = ManualRecordingSession.CompositeBounds(scoped);
        var dpi = WindowSnapshotCapture.Observe(target).Dpi;
        var display = new DisplayDescriptor { DisplayId = "atlas-target:" + target.Hwnd,
            BoundsPx = new RectPx { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height },
            WidthPx = bounds.Width, HeightPx = bounds.Height, DpiX = dpi, DpiY = dpi,
            ScaleFactorX = dpi / 96d, ScaleFactorY = dpi / 96d, Health = ProtocolHealth.Complete() };
        var scope = CapturePolicyResolver.PassiveScope(display.DisplayId);
        scope.TargetProcessInstanceEvidence = $"pid:{target.ProcessId}:started:{target.ProcessStartedUtc.UtcDateTime:O}";
        scope.TargetWindowHandleEvidence.AddRange(scoped.Select(window => window.Hwnd.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var request = CreateRequest(scope);
        var catalog = new CaptureProfileCatalogService();
        var effective = new CapturePolicyResolver(catalog).Resolve(request, RecordingProfileSettingsStore.Consumer);
        var runner = new ProviderProcessRunner(_providerPath);
        var screen = new GuardedScreen(new IsolatedScreenCaptureProvider(runner), screenshotGuard);
        var topology = new TargetTopology(new IsolatedWindowTopologyProvider(runner), target);
        var service = new EpisodeCaptureService(new TargetDisplayCatalog(display), screen,
            topology, new IsolatedAccessibilityProbe(runner), new SystemRecorderClock());
        var episode = await service.CaptureAsync(display.DisplayId, request, effective, cancellationToken,
            stage => progress?.Invoke(stage.Pass == "visual_topology" ? "Capturing the application..." : "Reading the application structure...")).ConfigureAwait(false);
        if (!episode.SensorPackets.Any(packet => packet.Screenshot?.ImageBlob.Length > 0))
            throw new InvalidDataException("The native recorder returned no pixels; the observation was not committed. "
                + string.Join("; ", episode.SensorPackets.Where(packet => packet.StreamKind == SensorStreamKind.Screenshot)
                    .SelectMany(packet => packet.Health.Issues).Select(reason => reason.Code + ": " + reason.SafeMessage)));
        await MappingObservationEvidence.CompleteAsync(episode, screen, topology,
            inputObserved ?? (_ => true), cancellationToken).ConfigureAwait(false);
        return episode;
    }

    private sealed class TargetDisplayCatalog(DisplayDescriptor display) : IDisplayCatalog
    {
        public Task<IReadOnlyList<DisplayDescriptor>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DisplayDescriptor>>([display.Clone()]);
    }

    private sealed class GuardedScreen(IScreenCaptureProvider native,
        Func<Func<CancellationToken, Task<ScreenshotPacket>>, CancellationToken, Task<ScreenshotPacket>>? guard) : IScreenCaptureProvider
    {
        public Task<ScreenshotPacket> CaptureAsync(DisplayDescriptor display, RecorderPolicy policy, CancellationToken token) =>
            guard is null ? native.CaptureAsync(display, policy, token) : guard(ct => native.CaptureAsync(display, policy, ct), token);
    }

    // Restrict the native census before accessibility workers are scheduled;
    // Atlas consent is application-scoped, not permission to inspect other apps.
    private sealed class TargetTopology(IWindowTopologyProvider native, WindowTarget target) : IWindowTopologyProvider
    {
        public async Task<WindowTopologyPacket> CaptureAsync(DisplayDescriptor display, RecorderPolicy policy, CancellationToken cancellationToken)
        {
            var packet = await native.CaptureAsync(display, policy with { ExhaustiveCoverage = true }, cancellationToken).ConfigureAwait(false);
            var identity = $"pid:{target.ProcessId}:started:{target.ProcessStartedUtc.UtcDateTime:O}";
            var members = packet.Windows.Where(window => window.ProcessId == target.ProcessId
                && window.ProcessInstanceEvidence == identity
                && (window.WindowHandleEvidence == target.RootOwnerHwnd.ToString()
                    || window.RootOwnerHandleEvidence == target.RootOwnerHwnd.ToString()
                    || window.WindowHandleEvidence == target.Hwnd.ToString())).ToArray();
            packet.Windows.Clear(); packet.Windows.AddRange(members);
            packet.WindowsIncluded = members.Length;
            if (!members.Any(window => window.WindowHandleEvidence == target.Hwnd.ToString()))
                throw new RecorderException("RECORDER_TARGET_CHANGED", "The selected application could not be verified by the native recorder.");
            return packet;
        }
    }

    public static FrameObservation Project(SensorEpisodeBundle episode, WindowTarget target, long sequence,
        string trigger, FrameCaptureOptions options)
    {
        var started = DateTimeOffset.Parse(episode.StartedAtUtc, System.Globalization.CultureInfo.InvariantCulture);
        var ended = DateTimeOffset.Parse(episode.EndedAtUtc, System.Globalization.CultureInfo.InvariantCulture);
        var windows = episode.SensorPackets.SelectMany(packet => packet.WindowTopology?.Windows ?? []).ToArray();
        var byId = windows.ToDictionary(window => window.WindowEvidenceId, StringComparer.Ordinal);
        var observations = windows.Select(window => new WindowObservation(Handle(window.WindowHandleEvidence),
            Handle(window.RootOwnerHandleEvidence), checked((int)window.ProcessId), Text(window.ClassName, 512), Text(window.Title, 4096),
            Rect(window.BoundsPx), window.IsVisible, !window.HasIsEnabled || window.IsEnabled, window.IsMinimized, window.IsCloakedOrUnavailable,
            checked((int)(window.Dpi > 0 ? window.Dpi : episode.DisplayDescriptor.DpiX)), Handle(window.OwnerChainEvidence.FirstOrDefault()), window.ZOrder,
            Style: window.WindowStyle, ExStyle: window.WindowExStyle, IsToolWindow: window.IsToolWindow, IsTopMost: (window.WindowExStyle & 8) != 0)).ToArray();
        var selected = observations.Single(window => window.Hwnd == target.Hwnd);
        var frameEntry = $"raw/frames/frame-{sequence:D6}.png";
        var nativeEntry = $"raw/native/frame-{sequence:D6}.episode.pb";
        var streams = new List<RawDataStreamObservation>();
        foreach (var packet in episode.SensorPackets)
        {
            var packetEnd = DateTimeOffset.TryParse(packet.CaptureInterval?.EndedAtUtc, out var to) ? to : ended;
            var packetStart = packet.CaptureInterval is { } interval && episode.ClockMetadata.MonotonicFrequencyHz > 0
                ? packetEnd - TimeSpan.FromSeconds((interval.EndedAtMonotonicTicks - interval.StartedAtMonotonicTicks) / (double)episode.ClockMetadata.MonotonicFrequencyHz)
                : started;
            if (packetStart < started) packetStart = started;
            if (packet.Accessibility is { } accessibility)
            {
                if (accessibility.RawProviderResponses.Count > 0)
                {
                    foreach (var response in accessibility.RawProviderResponses)
                    {
                        var attempt = response.Attempt;
                        var from = DateTimeOffset.TryParse(attempt.StartedAt, out var fromTime) ? fromTime : packetStart;
                        var toTime = DateTimeOffset.TryParse(attempt.EndedAt, out var parsedEnd) ? parsedEnd : packetEnd;
                        AddAccessibility(response.Packet, packet.PacketId + ":" + attempt.AttemptId, attempt.ProviderId,
                            response.WindowEvidenceId, from < started ? started : from, toTime, attempt.Status);
                    }
                }
                else
                {
                    foreach (var group in accessibility.Nodes.GroupBy(node => (node.ProviderId, node.WindowEvidenceId)))
                    {
                        var response = accessibility.Clone(); response.Nodes.Clear(); response.Nodes.AddRange(group);
                        AddAccessibility(response, packet.PacketId, group.Key.ProviderId, group.Key.WindowEvidenceId, packetStart, packetEnd);
                    }
                    if (accessibility.Nodes.Count == 0) AddAccessibility(accessibility, packet.PacketId, packet.StreamId, "", packetStart, packetEnd);
                }
                void AddAccessibility(AccessibilityPacket response, string attemptId, string provider, string windowId,
                    DateTimeOffset from, DateTimeOffset to, string? attemptStatus = null)
                {
                    byId.TryGetValue(windowId, out var owner);
                    var controls = response.Nodes.Select(node => new AutomationObservation(node.AccessibilityEvidenceId, node.ParentEvidenceId,
                        Text(node.AutomationId, 512), Text(node.Name, 4096), Text(node.ControlType.StartsWith("ControlType.", StringComparison.Ordinal)
                            ? node.ControlType : "ControlType." + node.ControlType, 512),
                        Text(node.ClassName, 512), Rect(node.BoundsPx), node.IsEnabled, node.IsOffscreen, "",
                        WindowHwnd: Handle(owner?.WindowHandleEvidence), SupportedPatterns: node.SupportedPatterns.ToArray(),
                        QueryWindowHwnd: owner is null ? null : Handle(owner.WindowHandleEvidence),
                        NativeWindowHwnd: Handle(node.NativeWindowHandleEvidence), ScopeStatus: owner is null ? "unverified" : null)).ToArray();
                    streams.Add(new(attemptId + ":" + provider + ":" + windowId, provider, from, to,
                        attemptStatus switch { "timed_out" => "timeout", "provider_failed" => "failed", "cancelled" => "cancelled", _ => Status(response.Health) }, controls, response.NodesObserved,
                        response.NodeLimited || response.DepthLimited || response.TimeLimited || response.WindowLimited,
                        EvidenceKind: "native-automation", TargetWindowHwnd: owner is null ? null : Handle(owner.WindowHandleEvidence)));
                }
            }
            else if (packet.Screenshot is { } screenshot)
                streams.Add(new(packet.PacketId, "screenshot", packetStart, packetEnd, Status(packet.Health), [],
                    ScreenshotEntry: frameEntry, EvidenceKind: "pixels", ImageBounds: Rect(screenshot.Display.BoundsPx)));
            else if (packet.WindowTopology is { })
                streams.Add(new(packet.PacketId, "native-window-topology", packetStart, packetEnd, Status(packet.Health), [],
                    observations.Length, Windows: observations, EvidenceKind: "native-windows"));
        }
        var allControls = streams.SelectMany(stream => stream.Controls).ToArray();
        var stability = episode.ObservationConsistency?.Status ?? "unverified";
        var status = stability == "changed" ? "interrupted" : stability != "stable" || episode.Health.State != HealthState.Complete
            ? "partial" : "ok";
        var images = episode.WindowImages.Select(image => new WindowImageObservation(Handle(image.WindowHandleEvidence),
            Rect(image.BoundsPx), image.Png.IsEmpty ? null : $"raw/frames/frame-{sequence:D6}-window-{Handle(image.WindowHandleEvidence):X}.png",
            DateTimeOffset.Parse(image.StartedAtUtc), DateTimeOffset.Parse(image.EndedAtUtc), image.Method, image.Status)).ToArray();
        return new(sequence, ended, frameEntry, selected, allControls,
            episode.SensorPackets.Any(packet => packet.Accessibility?.TimeLimited == true), status, trigger, observations,
            options.EpisodeSequence, options.PostTriggerDelayMs, options.CapturePhase, options.ActionObservedUtc,
            "full-root", observations.Select(window => window.Hwnd).ToArray(), Rect(episode.DisplayDescriptor.BoundsPx),
            InteractionSource: options.InteractionSource, InteractionId: options.InteractionId,
            Derivation: "uimaps-native-presentation/1", RawStreams: streams, RecordKind: options.RecordKind,
            Acquisition: new(RawObservationSemantics.Version, $"observation:{sequence}", started, ended, status,
                "UIMaps.Recorder.Windows", stability, episode.ObservationConsistency?.Reason ?? "legacy-native-survey-unverified",
                observations.Select(window => new RawWindowCoverage(window.Hwnd, window.Bounds,
                    RawObservationSemantics.Coverage(window.Bounds, Rect(episode.DisplayDescriptor.BoundsPx)))).ToArray()), WindowImages: images.Length > 0 ? images : null, NativeEpisodeEntry: nativeEntry);
    }

    private static string Status(Health? health) => health?.State == HealthState.Complete ? "ok" : "partial";
    // Only the compatibility presentation is bounded. Original provider text
    // remains intact in NativeEpisodeEntry and its sensor-packet inspector.
    private static string Text(string value, int limit) => value.Length <= limit ? value : value[..limit];
    private static long Handle(string? value) => long.TryParse(value, out var handle) ? handle : 0;
    private static RectI Rect(RectPx? value) => value is null ? new(0, 0, 0, 0) : new(value.X, value.Y, value.Width, value.Height);
}
