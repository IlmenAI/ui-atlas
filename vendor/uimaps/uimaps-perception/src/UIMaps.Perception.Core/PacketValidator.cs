using System.Security.Cryptography;
using Google.Protobuf;
using UIMaps.Protocol.V1;

namespace UIMaps.Perception.Core;

public sealed class PacketValidator(PerceptionPolicy policy)
{
    public void Validate(SensorEpisodeBundle bundle)
        => Validate(bundle, requireScreenshot: true);

    public void ValidateRecorded(SensorEpisodeBundle bundle)
        => Validate(bundle, requireScreenshot: false);

    public void ValidateInteraction(SensorEpisodeBundle bundle)
        => Validate(bundle, requireScreenshot: false, interaction: true);

    private void Validate(SensorEpisodeBundle bundle, bool requireScreenshot, bool interaction = false)
    {
        if (bundle is null) throw Invalid("Bundle is required.");
        if (bundle.CalculateSize() > policy.MaximumBundleBytes) throw Invalid("Bundle exceeds the configured size limit.");
        if (interaction ? bundle.FormatVersion != "interaction/1.0" : bundle.FormatVersion is not ("1.0" or "1.1"))
            throw new PerceptionException("PROTOCOL_UNSUPPORTED_VERSION", "Bundle format version is not supported.");
        if (string.IsNullOrWhiteSpace(bundle.EpisodeId)) throw Invalid("Episode identity is required.");
        if (bundle.SensorPackets.Count is 0 || bundle.SensorPackets.Count > policy.MaximumPacketCount) throw Invalid("Packet count is outside supported limits.");
        if (bundle.ClockMetadata is null || bundle.ClockMetadata.MonotonicFrequencyHz <= 0) throw Invalid("Recorder clock metadata is invalid.");
        if (bundle.DisplayDescriptor?.BoundsPx is null || bundle.DisplayDescriptor.WidthPx <= 0 || bundle.DisplayDescriptor.HeightPx <= 0) throw Invalid("Display descriptor is invalid.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var packet in bundle.SensorPackets)
        {
            if (packet.ProtocolVersion?.Major != 1) throw new PerceptionException("PROTOCOL_UNSUPPORTED_VERSION", "Packet protocol version is not supported.");
            if (packet.EpisodeId != bundle.EpisodeId) throw Invalid("Packet episode identity does not match its bundle.");
            if (string.IsNullOrWhiteSpace(packet.PacketId) || !ids.Add(packet.PacketId)) throw Invalid("Packet identities must be non-empty and unique.");
            if (packet.CaptureInterval is null || packet.CaptureInterval.EndedAtMonotonicTicks < packet.CaptureInterval.StartedAtMonotonicTicks) throw Invalid("Packet capture interval is invalid.");
            var validPayload = packet.StreamKind switch
            {
                SensorStreamKind.Screenshot => packet.PayloadCase == SensorPacket.PayloadOneofCase.Screenshot,
                SensorStreamKind.WindowTopology => packet.PayloadCase == SensorPacket.PayloadOneofCase.WindowTopology,
                SensorStreamKind.Accessibility => packet.PayloadCase == SensorPacket.PayloadOneofCase.Accessibility,
                _ => false,
            };
            if (!validPayload) throw Invalid("Packet kind and payload do not match.");
            if (packet.Accessibility?.Nodes.Any(node => node.BoundsPx is null
                && !AccessibilityGeometryEvidence.DeclaresUnavailable(node)) == true)
                throw Invalid("Accessibility geometry is missing without an explicit source gap.");
        }

        var screenshots = bundle.SensorPackets.Where(x => x.StreamKind == SensorStreamKind.Screenshot).ToList();
        if (requireScreenshot && (screenshots.Count != 1 || screenshots[0].Screenshot is null || screenshots[0].Screenshot.ImageBlob.IsEmpty))
            throw new PerceptionException("PERCEPTION_MISSING_SCREEN", "A valid diagnostic visual frame requires screenshot evidence.");
        if (screenshots.Count > 1) throw Invalid("An episode can contain at most one screenshot stream.");
        var screenshot = screenshots.FirstOrDefault();
        if (screenshot?.Screenshot.ImageBlob.IsEmpty == false
            && (screenshot.Screenshot.WidthPx != bundle.DisplayDescriptor.WidthPx || screenshot.Screenshot.HeightPx != bundle.DisplayDescriptor.HeightPx))
            throw Invalid("Screenshot geometry does not match the selected display.");

        if (!requireScreenshot)
        {
            if (bundle.Health is null || bundle.SensorPackets.Any(packet => packet.Health is null)) throw Invalid("Recorded provider health is required.");
            if (screenshot?.Screenshot.ImageBlob.IsEmpty == true && !DeclaresUnavailable(screenshot.Health))
                throw Invalid("Missing recorded pixels require an explicit provider gap.");
            foreach (var packet in bundle.SensorPackets)
            {
                if (packet.WindowTopology?.Windows.Any(window => window.BoundsPx is null || window.IntersectionWithDisplayPx is null) == true)
                    throw Invalid("Recorded geometry is missing.");
            }
        }

        ValidateManifest(bundle);
        ValidateMappingEvidence(bundle);
        if (bundle.FormatVersion == "1.1")
        {
            if (bundle.EffectiveCapturePolicy is null || bundle.EffectiveCapturePolicy.CaptureSemantics == CaptureSemantics.Unspecified)
                throw Invalid("Phase 1 bundle requires an exact effective capture policy.");
            if (bundle.CoverageReport is null || bundle.CoverageReport.EligibleWindows != bundle.CoverageReport.WindowOutcomes.Count)
                throw new PerceptionException("COVERAGE_ACCOUNTING_INCONSISTENT", "Phase 1 window coverage does not account for every eligible window.");
            if (bundle.CoverageReport.ConvergenceState == ConvergenceState.ConvergenceConverged &&
                (bundle.CoverageReport.RemainingFrontierCount != 0 || bundle.CoverageReport.WindowOutcomes.Any(x => x.TerminalStatus is not "completed")))
                throw new PerceptionException("COVERAGE_ACCOUNTING_INCONSISTENT", "Converged coverage contradicts unfinished evidence.");
        }
    }

    private static void ValidateMappingEvidence(SensorEpisodeBundle bundle)
    {
        foreach (var packet in bundle.SensorPackets.Where(packet => packet.Accessibility is not null))
        {
            var attempts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var response in packet.Accessibility.RawProviderResponses)
            {
                if (response.Attempt is null || string.IsNullOrWhiteSpace(response.Attempt.AttemptId)
                    || !attempts.Add(response.Attempt.AttemptId) || string.IsNullOrWhiteSpace(response.Attempt.ProviderId)
                    || string.IsNullOrWhiteSpace(response.WindowEvidenceId) || response.Packet is null
                    || response.Packet.RawProviderResponses.Count != 0)
                    throw Invalid("Raw provider response identity or nesting is invalid.");
            }
        }
        var windows = bundle.SensorPackets.Where(packet => packet.WindowTopology is not null)
            .SelectMany(packet => packet.WindowTopology.Windows).Select(window => window.WindowHandleEvidence).ToHashSet(StringComparer.Ordinal);
        var planes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plane in bundle.WindowImages)
            if (!windows.Contains(plane.WindowHandleEvidence) || !planes.Add(plane.WindowHandleEvidence)
                || plane.BoundsPx is not { Width: > 0, Height: > 0 }
                || plane.Status is not ("ok" or "unavailable" or "timeout")
                || (plane.Status == "ok") == plane.Png.IsEmpty)
                throw Invalid("Independent window image has invalid scope, geometry or status.");
        if (bundle.ObservationConsistency is { } consistency)
        {
            if (consistency.Status is not ("stable" or "changed" or "unverified"))
                throw Invalid("Unknown mapping consistency status.");
            if (consistency.Status == "stable" && (consistency.InputObserved || !consistency.TopologyMatched
                || !consistency.PixelsMatched || consistency.VerificationScreenshot?.ImageBlob.IsEmpty != false
                || consistency.VerificationWindows.Count == 0))
                throw Invalid("Stable mapping consistency requires retained verification evidence.");
            if (consistency.Status != "stable" && bundle.EffectiveCapturePolicy?.CaptureSemantics == CaptureSemantics.Snapshot)
                throw Invalid("Unverified mapping observations cannot be published as snapshots.");
        }
    }

    private static void ValidateManifest(SensorEpisodeBundle bundle)
    {
        if (bundle.SensorManifest is null || bundle.SensorManifest.PacketCount != bundle.SensorPackets.Count || bundle.SensorManifest.Entries.Count != bundle.SensorPackets.Count)
            throw Invalid("Sensor manifest does not match the packet set.");
        var packets = bundle.SensorPackets.ToDictionary(x => x.PacketId, StringComparer.Ordinal);
        var manifestIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in bundle.SensorManifest.Entries)
        {
            if (!manifestIds.Add(entry.PacketId) || !packets.TryGetValue(entry.PacketId, out var packet) || entry.StreamKind != packet.StreamKind)
                throw Invalid("Sensor manifest packet identities and kinds do not match.");
            var bytes = packet.ToByteArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (entry.SerializedSize != bytes.LongLength || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(hash), System.Text.Encoding.ASCII.GetBytes(entry.Sha256)))
                throw Invalid("Sensor packet integrity verification failed.");
        }
        var manifestHash = Convert.ToHexString(SHA256.HashData(bundle.SensorManifest.ToByteArray())).ToLowerInvariant();
        if (bundle.IntegrityMetadata?.Algorithm != "SHA-256" || !string.Equals(bundle.IntegrityMetadata.ManifestSha256, manifestHash, StringComparison.Ordinal))
            throw Invalid("Sensor manifest integrity verification failed.");
    }

    private static PerceptionException Invalid(string message) => new("PERCEPTION_INVALID_PACKET", message);

    private static bool DeclaresUnavailable(Health health) => health.State is not (HealthState.Complete or HealthState.Unspecified)
        && health.Issues.Any(issue => issue.State is HealthState.SensorUnavailable or HealthState.ProviderFailed or HealthState.TimedOut
            or HealthState.CancelledPartial or HealthState.PermissionDenied or HealthState.WorkerCrashed or HealthState.Unsupported or HealthState.BudgetExhausted);
}
