using UiAtlas.Core.Contracts;
using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;

namespace UiAtlas.Core.Recording.Windows;

public sealed partial class NativeRecorderCapture
{
    /// <summary>Bounded, read-only field samples. No pixels, focus changes, or input.</summary>
    internal async Task<FrameObservation?> ObserveFieldsAsync(WindowTarget target, CancellationToken cancellationToken)
    {
        if (!WindowCatalog.IsSameProcessWindow(target, target.Hwnd))
            throw new InvalidOperationException("The selected application instance changed.");
        var windows = WindowCatalog.ListScopedWindows(target).Where(WindowSnapshotCapture.IsCapturable)
            .Take(RecordingContractLimits.MaxScopedWindows).ToArray();
        if (windows.Length == 0) return null;
        var runner = new ProviderProcessRunner(_providerPath);
        var controls = new List<AutomationObservation>();
        var observedWindows = new List<WindowObservation>();
        var streams = new List<RawDataStreamObservation>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        var partial = false;
        foreach (var window in windows)
        {
            if (budget.IsCancellationRequested) { partial = true; break; }
            try
            {
                var started = DateTimeOffset.UtcNow;
                var packet = await runner.RunAsync(new ProbeRequest("uia-cached", 0, 0, 0, 0,
                    window.Hwnd, "field-window:" + window.Hwnd, 2048, 64, 0),
                    AccessibilityPacket.Parser, TimeSpan.FromMilliseconds(1200), budget.Token).ConfigureAwait(false);
                // Revalidate after crossing the provider boundary, before using its data.
                if (!WindowCatalog.IsSameProcessWindow(target, window.Hwnd)) continue;
                observedWindows.Add(WindowSnapshotCapture.Observe(window));
                var windowControls = packet.Nodes.Select(node => ProjectControl(node, window.Hwnd)).ToArray();
                controls.AddRange(windowControls);
                var incomplete = packet.NodeLimited || packet.DepthLimited || packet.TimeLimited || packet.Health?.State != HealthState.Complete;
                streams.Add(new("field-uia:" + window.Hwnd, "uia", started, DateTimeOffset.UtcNow,
                    incomplete ? "partial" : "ok", windowControls, packet.NodesObserved,
                    packet.NodeLimited || packet.DepthLimited || packet.TimeLimited,
                    EvidenceKind: "native-automation", TargetWindowHwnd: window.Hwnd));
                partial |= incomplete;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { partial = true; break; }
            catch (RecorderException) { partial = true; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (observedWindows.Count == 0) return null;
        return new(0, DateTimeOffset.UtcNow, "", observedWindows.FirstOrDefault(window => window.Hwnd == target.Hwnd)
            ?? observedWindows[0], controls, false, partial ? "partial" : "ok", "manual-field-observation",
            observedWindows, Derivation: "manual-field-observation/1", RawStreams: streams, RecordKind: "supplementary");
    }
}
