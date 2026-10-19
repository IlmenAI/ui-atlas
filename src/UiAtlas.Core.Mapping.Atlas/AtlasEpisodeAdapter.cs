using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UIMaps.Protocol.V1;

namespace UiAtlas.Core.Mapping.Atlas;

/// <summary>Loss-aware transport from sealed Atlas provider responses to UI Maps.
/// The original bundle remains authority. Sequential provider reads are Surveys;
/// converted wall-clock intervals never masquerade as a native monotonic clock.</summary>
public static class AtlasEpisodeAdapter
{
    public static SensorEpisodeBundle Convert(RecordingBundle bundle, RecordingManifest manifest, FrameObservation frame)
    {
        var bounds = frame.ScreenshotBounds ?? frame.Window.Bounds;
        if (string.IsNullOrWhiteSpace(frame.FrameEntry) || !bundle.Entries.Contains(frame.FrameEntry))
            throw new InvalidDataException($"Frame {frame.Sequence} has no retained screenshot. Its source bundle remains readable as legacy evidence.");
        var id = "atlas:" + Hash(Encoding.UTF8.GetBytes(manifest.SessionId + ":" + frame.Sequence));
        var start = frame.Acquisition?.StartedUtc ?? frame.TimestampUtc;
        var end = frame.Acquisition?.SealedUtc ?? frame.TimestampUtc;
        var health = Partial("ATLAS_SURVEY_CONVERSION", "Sequential Atlas evidence; wall-clock-derived intervals and provider scope are retained in the original recording.");
        var display = new DisplayDescriptor
        {
            DisplayId = id + ":capture-region", BoundsPx = Rect(bounds), WidthPx = bounds.Width, HeightPx = bounds.Height,
            DpiX = frame.Window.Dpi, DpiY = frame.Window.Dpi, ScaleFactorX = frame.Window.Dpi / 96d,
            ScaleFactorY = frame.Window.Dpi / 96d, Health = health.Clone(),
        };
        var episode = new SensorEpisodeBundle
        {
            FormatVersion = "1.1", EpisodeId = id, Producer = "ui-atlas.sealed-provider-adapter/1",
            ClockMetadata = new ClockMetadata { ClockId = "atlas-wall-clock-derived:" + manifest.SessionId,
                MonotonicFrequencyHz = TimeSpan.TicksPerSecond, StartedAtUtc = manifest.StartedUtc.ToString("O") },
            StartedAtUtc = start.ToString("O"), EndedAtUtc = end.ToString("O"), DisplayDescriptor = display,
            Health = health, EffectiveCapturePolicy = new EffectiveCapturePolicy { CaptureSemantics = CaptureSemantics.Survey },
            CoverageReport = new CoverageReport(),
        };
        SensorPacket Packet(string stream, SensorStreamKind kind, DateTimeOffset from, DateTimeOffset to) => new()
        {
            EpisodeId = id, PacketId = id + ":" + stream, StreamId = stream, StreamKind = kind,
            ProtocolVersion = new ProtocolVersion { Major = 1 }, Health = health.Clone(),
            CaptureInterval = new CaptureInterval { StartedAtMonotonicTicks = from.UtcTicks,
                EndedAtMonotonicTicks = Math.Max(from.UtcTicks, to.UtcTicks), EndedAtUtc = to.ToString("O") },
        };
        var screen = Packet("atlas:retained-pixels", SensorStreamKind.Screenshot, start, end);
        screen.Screenshot = new ScreenshotPacket { Display = display.Clone(), WidthPx = bounds.Width, HeightPx = bounds.Height,
            ImageEncoding = "image/png", ImageBlob = ByteString.CopyFrom(bundle.ReadBytes(frame.FrameEntry)), Health = health.Clone() };
        episode.SensorPackets.Add(screen);
        var windows = (frame.ScopedWindows is { Count: > 0 } ? frame.ScopedWindows : [frame.Window])
            .DistinctBy(window => window.Hwnd).ToArray();
        string WindowId(long hwnd) => id + ":hwnd:" + hwnd;
        var topology = Packet("atlas:win32", SensorStreamKind.WindowTopology, start, end);
        topology.WindowTopology = new WindowTopologyPacket { Health = health.Clone(), WindowsObserved = windows.Length, WindowsIncluded = windows.Length };
        foreach (var window in windows)
        {
            var knownProcess = window.ProcessId == manifest.Target.ProcessId;
            var record = new WindowObservationRecord
            {
                WindowEvidenceId = WindowId(window.Hwnd), CaptureWindowId = WindowId(window.Hwnd),
                WindowHandleEvidence = window.Hwnd.ToString(), RootOwnerHandleEvidence = window.RootOwnerHwnd.ToString(),
                ProcessId = (uint)window.ProcessId, ProcessName = knownProcess ? manifest.Target.ProcessName : "",
                ProcessInstanceEvidence = knownProcess ? ProcessIdentity(manifest.Target) : "",
                ProcessStartTimeEvidence = knownProcess ? manifest.Target.ProcessStartedUtc.ToString("O") : "",
                ClassName = window.ClassName, Title = window.Title, BoundsPx = Rect(window.Bounds),
                IntersectionWithDisplayPx = Rect(Intersection(window.Bounds, bounds)), ZOrder = window.ZOrder,
                IsVisible = window.IsVisible, IsMinimized = window.IsMinimized,
                IsCloakedOrUnavailable = window.IsCloaked, IsToolWindow = window.IsToolWindow,
                IsOwnedWindow = window.OwnerHwnd != 0, Health = health.Clone(),
            };
            if (window.OwnerHwnd != 0) record.OwnerChainEvidence.Add(window.OwnerHwnd.ToString());
            topology.WindowTopology.Windows.Add(record);
            episode.CoverageReport.WindowOutcomes.Add(new WindowCaptureOutcome { CaptureWindowId = record.CaptureWindowId, TerminalStatus = "partial" });
        }
        episode.CoverageReport.EligibleWindows = windows.Length;
        episode.SensorPackets.Add(topology);
        // Old bundles without raw streams are explicitly identified as legacy-normalized;
        // never label their merged controls UIA, MSAA, or direct provider responses.
        var streams = frame.RawStreams is { Count: > 0 } ? frame.RawStreams :
            [new RawDataStreamObservation("legacy-normalized", "atlas:legacy-normalized", start, end,
                frame.AutomationStatus, frame.Automation, TargetWindowHwnd: frame.Window.Hwnd)];
        var ordinal = 0;
        foreach (var stream in streams.Where(value => value.Controls.Count > 0 && !RawObservationSemantics.IsImageSource(value.Source)))
        {
            var packet = Packet("atlas:provider:" + ordinal++, SensorStreamKind.Accessibility, stream.StartedUtc, stream.CompletedUtc);
            packet.Health.Issues.Add(new HealthIssue { State = HealthState.Partial, Code = "ATLAS_PROVIDER_STATUS", Source = stream.Source,
                SafeMessage = "Provider status: " + stream.Status + "; truncated: " + stream.Truncated });
            packet.Accessibility = new AccessibilityPacket { Health = packet.Health.Clone(), NodeLimited = stream.Truncated,
                TimeLimited = stream.Status.Contains("timeout", StringComparison.OrdinalIgnoreCase),
                NodesObserved = Math.Max(stream.ReturnedCount, stream.Controls.Count), NodesIncluded = stream.Controls.Count };
            var uniqueIds = stream.Controls.Select((control, index) => (control, index)).GroupBy(value => value.control.RuntimeId)
                .Where(group => group.Count() == 1 && !string.IsNullOrEmpty(group.Key)).ToDictionary(group => group.Key, group => packet.PacketId + ":node:" + group.Single().index);
            var index = 0;
            foreach (var control in stream.Controls)
            {
                // WindowHwnd is the provider's reported owner. Query/native handles are
                // separate facts in the original stream, never substituted for ownership.
                var node = new AccessibilityNodeRecord
                {
                    AccessibilityEvidenceId = packet.PacketId + ":node:" + index++,
                    ParentEvidenceId = uniqueIds.GetValueOrDefault(control.ParentRuntimeId, ""),
                    WindowEvidenceId = windows.Any(window => window.Hwnd == control.WindowHwnd) ? WindowId(control.WindowHwnd) : "",
                    AutomationId = control.AutomationId, Name = control.Name, ControlType = control.ControlType,
                    ClassName = control.ClassName, BoundsPx = Rect(control.Bounds), IsEnabled = control.IsEnabled,
                    IsOffscreen = control.IsOffscreen, ProviderId = stream.Source,
                    NativeWindowHandleEvidence = control.NativeWindowHwnd?.ToString() ?? "", Health = health.Clone(),
                };
                node.SupportedPatterns.AddRange(control.SupportedPatterns ?? []);
                packet.Accessibility.Nodes.Add(node);
            }
            episode.SensorPackets.Add(packet);
        }
        Seal(episode);
        return episode;
    }

    public static string ProcessIdentity(TargetScope target) => $"pid:{target.ProcessId}:started:{target.ProcessStartedUtc:O}";
    public static string Hash(byte[] bytes) => System.Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static RectPx Rect(RectI value) => new() { X = value.X, Y = value.Y, Width = value.Width, Height = value.Height };
    public static RectI Rect(RectPx? value) => value is null ? new(0, 0, 0, 0) : new(value.X, value.Y, value.Width, value.Height);
    private static RectI Intersection(RectI a, RectI b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y),
        Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)),
        Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y)));
    private static Health Partial(string code, string message) => new() { State = HealthState.Partial,
        Issues = { new HealthIssue { State = HealthState.Partial, Code = code, SafeMessage = message, Source = "atlas-adapter" } } };
    public static void Seal(SensorEpisodeBundle episode)
    {
        var manifest = new SensorManifest { PacketCount = episode.SensorPackets.Count };
        foreach (var packet in episode.SensorPackets)
        {
            var bytes = packet.ToByteArray();
            manifest.Entries.Add(new SensorManifestEntry { PacketId = packet.PacketId, StreamKind = packet.StreamKind,
                SerializedSize = bytes.Length, Sha256 = Hash(bytes), Health = packet.Health.Clone() });
        }
        episode.SensorManifest = manifest;
        episode.IntegrityMetadata = new IntegrityMetadata { Algorithm = "SHA-256", ManifestSha256 = Hash(manifest.ToByteArray()) };
    }
}
