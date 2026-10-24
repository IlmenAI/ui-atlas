using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Reader;

public sealed record UiInspectionFrameGroup(string Id, string Label, IReadOnlyList<UiMapVariantView> Frames)
{
    public override string ToString() => Label;
}

/// <summary>Read-only editor organization. Never changes recorded names or target identities.</summary>
public static class UiInspectionPresentation
{
    public static string ControlLabel(UiMapControlView control)
    {
        var kind = string.IsNullOrWhiteSpace(control.CanonicalKind) ? "Control" : control.CanonicalKind.Trim();
        if (!string.IsNullOrWhiteSpace(control.DisplayName)) return $"{control.DisplayName.Trim()} · {kind}";
        var hint = new[] { "automationId", "className", "nativeClassName" }
            .Select(name => Property(control.Source, name)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return $"{kind} · unnamed · {hint ?? control.Id[^Math.Min(8, control.Id.Length)..]}";
    }

    public static bool Matches(UiMapControlView control, string query) =>
        ControlLabel(control).Contains(query, StringComparison.OrdinalIgnoreCase) ||
        control.CanonicalKind.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        control.Id.Contains(query, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<UiMapVariantView> IncludeEquivalentFrames(IReadOnlyList<UiMapVariantView> eligible,
        IEnumerable<UiMapVariantView> recorded)
    {
        var ids = eligible.Select(frame => frame.Id).ToHashSet(StringComparer.Ordinal);
        return eligible.Concat(recorded.Where(frame => ids.Contains(frame.DedupedIntoVariantId) &&
                frame.Reason is "duplicate_content" or "duplicate_visual_content"))
            .DistinctBy(frame => frame.Id).OrderBy(frame => frame.BundleId, StringComparer.Ordinal)
            .ThenBy(frame => frame.FrameSequence).ThenBy(frame => frame.Id, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<UiInspectionFrameGroup> FrameGroups(IReadOnlyList<UiMapVariantView> frames) =>
        frames.GroupBy(frame => frame.SemanticVariantId.Length > 0
                ? "semantic:" + frame.SemanticVariantId
                : frame.DedupedIntoVariantId.Length > 0 ? "observation:" + frame.DedupedIntoVariantId
                : frames.Any(other => other.DedupedIntoVariantId == frame.Id) ? "observation:" + frame.Id
                : "unclassified:" + frame.SurfaceId, StringComparer.Ordinal)
            .Select(group => new UiInspectionFrameGroup(group.Key,
                group.First().SemanticVariantId.Length > 0
                    ? (string.IsNullOrWhiteSpace(group.First().SemanticVariantName) ? "Recorded variant" : group.First().SemanticVariantName)
                    : group.Key.StartsWith("observation:", StringComparison.Ordinal) ? "Equivalent observations" : "Unclassified observations",
                group.OrderBy(frame => frame.BundleId, StringComparer.Ordinal).ThenBy(frame => frame.FrameSequence)
                    .ThenBy(frame => frame.Id, StringComparer.Ordinal).ToArray()))
            .ToArray();

    public static IReadOnlyList<ParameterProposal> GridsForSurface(UiMappingReadModel model,
        UiMapSurfaceView? surface, IReadOnlyList<ParameterProposal> proposals)
    {
        if (surface is null || surface.Level == UiUnderstandingLevel.RawDataStreams) return [];
        var semantic = model.LayerFor(UiUnderstandingLevel.SemanticWorld);
        var raw = model.LayerFor(UiUnderstandingLevel.RawWorld);
        return proposals.Where(p => p.IsGrid && p.HostBound && p.ParentProposalId is null).Where(p =>
        {
            var host = semantic.Controls.FirstOrDefault(control => control.Id == p.TargetId);
            if (host is null || host.OwnerSurfaceId != p.SurfaceId) return false;
            if (surface.Level == UiUnderstandingLevel.SemanticWorld) return host.OwnerSurfaceId == surface.Id;
            var sources = host.Source.Properties.Where(property => property.Name == "sourceRawControlId")
                .Select(property => property.Value).ToHashSet(StringComparer.Ordinal);
            return raw.Controls.Any(control => sources.Contains(control.Id) &&
                RawWorldPresentation.IsAvailableOnSurface(control, surface.Id));
        }).ToArray();
    }

    private static string Property(GraphNode node, string name) =>
        node.Properties.FirstOrDefault(p => p.Name == name)?.Value ?? "";
}
