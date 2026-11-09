using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;

namespace UIMaps.Recorder.ProviderProbe;

// The same bounded walk is used with real UIA elements and deterministic test
// nodes. Provider navigation remains confined to this disposable worker process.
internal static class UiaCaptureTraversal
{
    internal static AccessibilityPacket Capture<T>(T root, string windowId, int maxNodes, int maxDepth,
        Func<T, string, string, AccessibilityNodeRecord> readNode,
        Func<T, T?> firstChild, Func<T, T?> nextSibling, Func<bool> hasTime) where T : class
    {
        var packet = new AccessibilityPacket { Health = ProtocolHealth.Complete() };
        var queue = new Queue<(T Element, string ParentId, int Depth)>();
        var retained = new Dictionary<string, AccessibilityNodeRecord>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var health = new List<Health>();
        queue.Enqueue((root, string.Empty, 0));
        var first = true;

        while (queue.Count > 0)
        {
            // The root's metadata has already arrived in the bulk cache. Retain
            // that cache even if root acquisition used the remaining time.
            if (!first && !hasTime()) { Deadline(queue.Peek().ParentId); break; }
            first = false;
            var (element, parentId, depth) = queue.Dequeue();
            if (packet.Nodes.Count >= maxNodes) { NodeLimit(parentId); break; }

            AccessibilityNodeRecord node;
            try { node = readNode(element, Guid.NewGuid().ToString("N"), parentId); }
            catch
            {
                Failure(parentId, "node_metadata_unavailable", "Accessibility node metadata could not be read.");
                continue;
            }
            packet.NodesObserved++;
            var signature = node.RuntimeIdEvidence.Count == 0 ? string.Empty : string.Join('.', node.RuntimeIdEvidence);
            if (signature.Length > 0 && !visited.Add(signature))
            {
                Failure(parentId, "cycle_detected", "Accessibility traversal encountered a repeated runtime identity.");
                continue;
            }
            node.ChildCountObserved = node.ChildCountIncluded = 0;
            packet.Nodes.Add(node); retained.Add(node.AccessibilityEvidenceId, node);
            if (retained.TryGetValue(parentId, out var parent)) parent.ChildCountIncluded++;
            if (node.Health is not null) health.Add(node.Health);
            if (!hasTime()) { Deadline(node.AccessibilityEvidenceId); break; }

            // Queue reservations and retained nodes share one ceiling. Never
            // enumerate a wide sibling list and discard it only after the fact.
            var allowance = maxNodes - packet.Nodes.Count - queue.Count;
            if (allowance <= 0) { NodeLimit(node.AccessibilityEvidenceId); continue; }
            T? child;
            try { child = firstChild(element); }
            catch { Failure(node.AccessibilityEvidenceId, "child_enumeration_failed", "The first raw child could not be enumerated."); continue; }

            while (child is not null)
            {
                node.ChildCountObserved++;
                if (depth >= maxDepth)
                {
                    packet.DepthLimited = true;
                    packet.TraversalFrontier.Add(Frontier(windowId, node.AccessibilityEvidenceId, "depth_limit", node.ChildCountObserved));
                    health.Add(ProtocolHealth.Single(HealthState.Partial, "RECORDER_UIA_DEPTH_LIMIT", "Accessibility capture reached its depth limit; remaining children were not enumerated.", "accessibility"));
                    break;
                }
                queue.Enqueue((child, node.AccessibilityEvidenceId, depth + 1));
                allowance--;
                if (!hasTime()) { Deadline(node.AccessibilityEvidenceId); break; }
                if (allowance <= 0) { NodeLimit(node.AccessibilityEvidenceId); break; }
                try { child = nextSibling(child); }
                catch { Failure(node.AccessibilityEvidenceId, "sibling_enumeration_failed", "Raw sibling enumeration stopped with a provider failure."); break; }
            }
            if (!hasTime()) { if (!packet.TimeLimited) Deadline(node.AccessibilityEvidenceId); break; }
        }

        // ChildCountIncluded counts retained children, not speculative queue
        // entries that a later deadline or cycle check may never include.
        packet.NodesIncluded = packet.Nodes.Count;
        packet.Health = ProtocolHealth.Aggregate(health.Append(packet.Health));
        return packet;

        void Deadline(string parentId)
        {
            if (!packet.TimeLimited)
            {
                packet.TimeLimited = true;
                packet.TraversalFrontier.Add(Frontier(windowId, parentId, "cooperative_deadline"));
                health.Add(ProtocolHealth.Single(HealthState.Partial, "RECORDER_UIA_COOPERATIVE_DEADLINE", "Accessibility capture returned its observed prefix before the worker deadline; remaining coverage is unknown.", "accessibility", true));
            }
            foreach (var pending in queue.Select(x => x.ParentId).Distinct(StringComparer.Ordinal))
                if (!packet.TraversalFrontier.Any(x => x.ParentEvidenceId == pending && x.StopReason == "cooperative_deadline"))
                    packet.TraversalFrontier.Add(Frontier(windowId, pending, "cooperative_deadline"));
        }
        void NodeLimit(string parentId)
        {
            packet.NodeLimited = true;
            packet.TraversalFrontier.Add(Frontier(windowId, parentId, "per_window_node_limit"));
            health.Add(ProtocolHealth.Single(HealthState.NodeLimited, "RECORDER_ACCESSIBILITY_NODE_LIMIT", "Accessibility capture reached its node or queued-candidate limit; remaining children were not enumerated.", "accessibility"));
        }
        void Failure(string parentId, string reason, string message)
        {
            packet.TraversalFrontier.Add(Frontier(windowId, parentId, reason));
            health.Add(ProtocolHealth.Single(HealthState.Partial, "RECORDER_UIA_TRAVERSAL_PARTIAL", message, "accessibility"));
        }
    }

    internal static TraversalFrontier Frontier(string windowId, string parentId, string reason, int observedChildren = 0) => new()
    {
        FrontierId = Guid.NewGuid().ToString("N"), CaptureWindowId = windowId, ProviderId = "uia", ParentEvidenceId = parentId,
        ReasonKnown = true, StopReason = reason, EstimatedChildCount = observedChildren,
        CreatedAt = DateTimeOffset.UtcNow.ToString("O"), TerminalState = "remaining",
    };
}
