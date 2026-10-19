using UIMaps.Protocol.V1;

namespace UIMaps.Recorder.Windows;

internal static class AccessibilityPacketMerger
{
    public static AccessibilityPacket Merge(AccessibilityPacket uia, AccessibilityPacket native, int maxNodes)
    {
        ArgumentNullException.ThrowIfNull(uia);
        ArgumentNullException.ThrowIfNull(native);
        var result = uia.Clone();
        result.ProviderAttempts.AddRange(native.ProviderAttempts.Select(value => value.Clone()));
        result.TraversalFrontier.AddRange(native.TraversalFrontier.Select(value => value.Clone()));
        result.NodeLimited |= native.NodeLimited;
        result.DepthLimited |= native.DepthLimited;
        result.TimeLimited |= native.TimeLimited;
        result.Health = ProtocolHealth.Aggregate([uia.Health, native.Health]);

        var mappedIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var unmatched = new List<AccessibilityNodeRecord>();
        foreach (var nativeNode in native.Nodes)
        {
            var match = FindMatch(result.Nodes, nativeNode);
            if (match is null)
            {
                unmatched.Add(nativeNode);
                continue;
            }

            mappedIds[nativeNode.AccessibilityEvidenceId] = match.AccessibilityEvidenceId;
            Enrich(match, nativeNode);
        }

        var room = Math.Max(0, maxNodes - result.Nodes.Count);
        var included = unmatched.Take(room).Select(value => value.Clone()).ToList();
        foreach (var node in included) mappedIds[node.AccessibilityEvidenceId] = node.AccessibilityEvidenceId;
        foreach (var node in included)
        {
            if (!string.IsNullOrWhiteSpace(node.ParentEvidenceId)
                && mappedIds.TryGetValue(node.ParentEvidenceId, out var parentId))
                node.ParentEvidenceId = parentId;
            result.Nodes.Add(node);
        }

        var omitted = unmatched.Count - included.Count;
        if (omitted > 0)
        {
            result.NodeLimited = true;
            result.TraversalFrontier.Add(new TraversalFrontier
            {
                FrontierId = Guid.NewGuid().ToString("N"),
                CaptureWindowId = native.ProviderAttempts.FirstOrDefault()?.CaptureWindowId ?? string.Empty,
                ProviderId = "native-hwnd",
                ReasonKnown = true,
                StopReason = "native_merge_node_limit",
                EstimatedChildCount = omitted,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                TerminalState = "remaining",
            });
        }

        result.NodesIncluded = result.Nodes.Count;
        result.NodesObserved = Math.Max(result.NodesIncluded, uia.NodesObserved + included.Count);
        if (result.NodeLimited && result.Health.State == HealthState.Complete)
            result.Health = ProtocolHealth.Single(HealthState.NodeLimited, "RECORDER_ACCESSIBILITY_NODE_LIMIT", "Accessibility capture reached its cross-provider node limit.", "accessibility");
        return result;
    }

    private static AccessibilityNodeRecord? FindMatch(
        IEnumerable<AccessibilityNodeRecord> candidates,
        AccessibilityNodeRecord nativeNode)
    {
        if (!string.IsNullOrWhiteSpace(nativeNode.NativeWindowHandleEvidence))
        {
            var byHandle = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.WindowEvidenceId, nativeNode.WindowEvidenceId, StringComparison.Ordinal)
                && string.Equals(candidate.NativeWindowHandleEvidence, nativeNode.NativeWindowHandleEvidence, StringComparison.Ordinal));
            if (byHandle is not null) return byHandle;
        }

        return candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.WindowEvidenceId, nativeNode.WindowEvidenceId, StringComparison.Ordinal)
            && SameBounds(candidate.BoundsPx, nativeNode.BoundsPx)
            && EqualsNormalized(candidate.ClassName, nativeNode.ClassName)
            && CompatibleName(candidate.Name, nativeNode.Name));
    }

    private static void Enrich(AccessibilityNodeRecord target, AccessibilityNodeRecord fallback)
    {
        target.NativeWindowHandleEvidence = First(target.NativeWindowHandleEvidence, fallback.NativeWindowHandleEvidence);
        target.NativeControlIdEvidence = First(target.NativeControlIdEvidence, fallback.NativeControlIdEvidence);
        target.AutomationId = First(target.AutomationId, fallback.AutomationId);
        target.ControlType = First(target.ControlType, fallback.ControlType);
        target.LocalizedControlType = First(target.LocalizedControlType, fallback.LocalizedControlType);
        target.Name = First(target.Name, fallback.Name);
        target.ClassName = First(target.ClassName, fallback.ClassName);
        target.ValueEvidence = First(target.ValueEvidence, fallback.ValueEvidence);
        foreach (var pattern in fallback.SupportedPatterns)
            if (!target.SupportedPatterns.Contains(pattern)) target.SupportedPatterns.Add(pattern);
    }

    private static string First(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static bool CompatibleName(string left, string right) =>
        string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) || EqualsNormalized(left, right);

    private static bool SameBounds(RectPx? left, RectPx? right) => left is not null && right is not null
        && left.X == right.X && left.Y == right.Y && left.Width == right.Width && left.Height == right.Height;

    private static bool EqualsNormalized(string? left, string? right) =>
        string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
}
