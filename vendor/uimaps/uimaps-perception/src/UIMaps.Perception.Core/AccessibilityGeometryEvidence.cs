using UIMaps.Protocol.V1;

namespace UIMaps.Perception.Core;

internal static class AccessibilityGeometryEvidence
{
    internal const string RecorderIssueCode = "RECORDER_UIA_GEOMETRY_UNAVAILABLE";
    internal const string PerceptionIssueCode = "PERCEPTION_ACCESSIBILITY_GEOMETRY_UNAVAILABLE";

    internal static bool DeclaresUnavailable(AccessibilityNodeRecord node) =>
        node.Health?.State == HealthState.Partial
        && node.Health.Issues.Any(issue => issue.Code == RecorderIssueCode
            && issue.State == HealthState.Partial && issue.Source == "accessibility");

    internal static bool HasSpatialGeometry(AccessibilityNodeRecord node)
    {
        if (node.BoundsPx is not null) return true;
        if (DeclaresUnavailable(node)) return false;
        throw new PerceptionException("PERCEPTION_INVALID_PACKET", "Accessibility geometry is missing without an explicit source gap.");
    }

    internal static Health? NormalizationGap(SensorEpisodeBundle bundle)
    {
        var count = bundle.SensorPackets.Sum(packet => packet.Accessibility?.Nodes.Count(node =>
            node.BoundsPx is null && DeclaresUnavailable(node)) ?? 0);
        if (count == 0) return null;
        return new Health
        {
            State = HealthState.Partial,
            Issues = { new HealthIssue
            {
                State = HealthState.Partial, Code = PerceptionIssueCode, Source = "observation_normalizer",
                SafeMessage = $"{count} source accessibility node(s) explicitly lacked geometry and were not promoted to spatial observations or controls. Their original identities, metadata and timing remain in the original source packets.",
            } },
        };
    }
}
