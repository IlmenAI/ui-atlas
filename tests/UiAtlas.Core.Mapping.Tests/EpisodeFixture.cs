using System.Security.Cryptography;
using Google.Protobuf;
using UIMaps.Protocol.V1;

namespace UiAtlas.Core.Mapping.Tests;

internal static class EpisodeFixture
{
    public static SensorEpisodeBundle Create(bool survey = false)
    {
        var display = new DisplayDescriptor
        {
            DisplayId = "synthetic-display", WidthPx = 1, HeightPx = 1,
            BoundsPx = Bounds(), DpiX = 96, DpiY = 96, ScaleFactorX = 1, ScaleFactorY = 1,
        };
        var bundle = new SensorEpisodeBundle
        {
            FormatVersion = "1.1", EpisodeId = "synthetic-episode", Producer = "ui-atlas-test",
            ClockMetadata = new ClockMetadata { ClockId = "synthetic-clock", MonotonicFrequencyHz = 1000 },
            DisplayDescriptor = display, Health = Complete(),
            StartedAtUtc = DateTimeOffset.UnixEpoch.ToString("O"),
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1).ToString("O"),
            EffectiveCapturePolicy = new EffectiveCapturePolicy
            {
                CaptureSemantics = survey ? CaptureSemantics.Survey : CaptureSemantics.Snapshot,
            },
            CoverageReport = new CoverageReport
            {
                EligibleWindows = 1, WindowsCompleted = 1,
                ConvergenceState = ConvergenceState.ConvergenceConverged,
                WindowOutcomes = { new WindowCaptureOutcome { CaptureWindowId = "selected-window", TerminalStatus = "completed" } },
            },
        };
        var pixels = Packet(SensorStreamKind.Screenshot, "pixels");
        pixels.Screenshot = new ScreenshotPacket
        {
            Display = display.Clone(), WidthPx = 1, HeightPx = 1, ImageEncoding = "image/png", Health = Complete(),
            ImageBlob = ByteString.FromBase64("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aA1cAAAAASUVORK5CYII="),
        };
        var windows = Packet(SensorStreamKind.WindowTopology, "topology");
        windows.WindowTopology = new WindowTopologyPacket
        {
            WindowsObserved = 2, WindowsIncluded = 2, Health = Complete(),
            Windows =
            {
                new WindowObservationRecord
                {
                    WindowEvidenceId = "selected-window", CaptureWindowId = "selected-window",
                    ProcessId = 10, ProcessName = "synthetic-app", ProcessInstanceEvidence = "pid:10:started:synthetic",
                    WindowHandleEvidence = "123", RootOwnerHandleEvidence = "123", ClassName = "SyntheticWindow",
                    BoundsPx = Bounds(), IntersectionWithDisplayPx = Bounds(), IsVisible = true, Health = Complete(),
                },
                new WindowObservationRecord
                {
                    WindowEvidenceId = "unrelated-window", ProcessId = 20, ProcessName = "unrelated-app",
                    ProcessInstanceEvidence = "pid:20:started:synthetic", WindowHandleEvidence = "456", RootOwnerHandleEvidence = "456",
                    ClassName = "UnrelatedWindow", BoundsPx = Bounds(), IntersectionWithDisplayPx = Bounds(), IsVisible = true, Health = Complete(),
                },
            },
        };
        var accessibility = Packet(SensorStreamKind.Accessibility, "accessibility");
        accessibility.Accessibility = new AccessibilityPacket
        {
            NodesObserved = 1, NodesIncluded = 1, Health = Complete(),
            Nodes = { new AccessibilityNodeRecord
            {
                AccessibilityEvidenceId = "save-button", WindowEvidenceId = "selected-window", AutomationId = "SaveButton",
                ControlType = "Button", LocalizedControlType = "button", Name = "Save", BoundsPx = Bounds(),
                IsEnabled = true, ProviderId = "synthetic-uia", Health = Complete(),
            } },
        };
        bundle.SensorPackets.AddRange([pixels, windows, accessibility]);
        Seal(bundle);
        return bundle;
    }

    public static void Seal(SensorEpisodeBundle bundle)
    {
        var manifest = new SensorManifest { PacketCount = bundle.SensorPackets.Count };
        foreach (var packet in bundle.SensorPackets)
        {
            var bytes = packet.ToByteArray();
            manifest.Entries.Add(new SensorManifestEntry
            {
                PacketId = packet.PacketId, StreamKind = packet.StreamKind,
                SerializedSize = bytes.LongLength, Sha256 = Hash(bytes), Health = packet.Health.Clone(),
            });
        }
        bundle.SensorManifest = manifest;
        bundle.IntegrityMetadata = new IntegrityMetadata { Algorithm = "SHA-256", ManifestSha256 = Hash(manifest.ToByteArray()) };
    }

    private static SensorPacket Packet(SensorStreamKind kind, string id) => new()
    {
        EpisodeId = "synthetic-episode", PacketId = id, StreamId = id, StreamKind = kind,
        ProtocolVersion = new ProtocolVersion { Major = 1 }, Health = Complete(),
        CaptureInterval = new CaptureInterval
        {
            StartedAtMonotonicTicks = 100, EndedAtMonotonicTicks = 110,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMilliseconds(110).ToString("O"),
        },
    };

    private static RectPx Bounds() => new() { Width = 1, Height = 1 };
    private static Health Complete() => new() { State = HealthState.Complete };
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
