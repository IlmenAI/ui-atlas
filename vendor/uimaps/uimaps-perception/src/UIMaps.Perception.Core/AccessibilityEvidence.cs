using UIMaps.Protocol.V1;

namespace UIMaps.Perception.Core;

/// <summary>Completes the preferred tree from same-episode worker evidence. Raw packets stay immutable.</summary>
internal static class AccessibilityEvidence
{
    public static IReadOnlyList<AccessibilityNodeRecord> Nodes(AccessibilityPacket packet)
    {
        var result = packet.Nodes.ToList();
        var ids = result.Select(n => n.AccessibilityEvidenceId).ToHashSet(StringComparer.Ordinal);
        var canonical = result.GroupBy(Key).ToDictionary(g => g.Key, g => g.First().AccessibilityEvidenceId);
        foreach (var response in packet.RawProviderResponses)
        {
            var source = response.Packet.Nodes.GroupBy(n => n.AccessibilityEvidenceId)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            var added = new List<AccessibilityNodeRecord>();
            string Include(AccessibilityNodeRecord node)
            {
                if (aliases.TryGetValue(node.AccessibilityEvidenceId, out var prior)) return prior;
                if (ids.Contains(node.AccessibilityEvidenceId)) return aliases[node.AccessibilityEvidenceId] = node.AccessibilityEvidenceId;
                if (canonical.TryGetValue(Key(node), out var existing)) return aliases[node.AccessibilityEvidenceId] = existing;
                var clone = node.Clone();
                aliases[node.AccessibilityEvidenceId] = clone.AccessibilityEvidenceId;
                ids.Add(clone.AccessibilityEvidenceId);
                canonical[Key(clone)] = clone.AccessibilityEvidenceId;
                added.Add(clone);
                return clone.AccessibilityEvidenceId;
            }
            // Non-visible records remain in Raw Data Streams. Include ancestors of visible
            // additions so offscreen ancestry and provider hierarchy still qualify visibility.
            foreach (var node in source.Values.Where(n => !n.IsOffscreen && n.BoundsPx is { Width: > 0, Height: > 0 }))
            {
                if (ids.Contains(node.AccessibilityEvidenceId) || canonical.ContainsKey(Key(node))) continue;
                Include(node);
                var ancestor = node;
                var seen = new HashSet<string> { node.AccessibilityEvidenceId };
                while (source.TryGetValue(ancestor.ParentEvidenceId, out ancestor!) && seen.Add(ancestor.AccessibilityEvidenceId)) Include(ancestor);
            }
            foreach (var node in added)
            {
                var parent = node.ParentEvidenceId;
                var seen = new HashSet<string> { node.AccessibilityEvidenceId };
                while (source.TryGetValue(parent, out var ancestor) && seen.Add(parent))
                {
                    var mapped = Include(ancestor);
                    if (mapped != node.AccessibilityEvidenceId) { parent = mapped; break; }
                    parent = ancestor.ParentEvidenceId;
                }
                node.ParentEvidenceId = parent == node.AccessibilityEvidenceId ? "" : parent;
            }
            result.AddRange(added);
        }
        return result;
    }

    private static string Key(AccessibilityNodeRecord node) => string.Join("\u001f", node.WindowEvidenceId,
        node.ControlType.Replace("ControlType.", "", StringComparison.Ordinal), node.Name, node.IsOffscreen,
        node.BoundsPx?.X, node.BoundsPx?.Y, node.BoundsPx?.Width, node.BoundsPx?.Height);
}
