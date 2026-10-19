using UiAtlas.Core.Contracts;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Mapping.Atlas;

/// <summary>Interpretation metadata and persisted Map variants. Does not change Map authority.</summary>
internal static class SemanticMapProjection
{
    private static string P(GraphNode node, string key) => node.Properties.FirstOrDefault(p => p.Name == key)?.Value ?? "";

    internal static string ControlKind(MapEntity entity, IReadOnlyList<GraphNode> sources)
    {
        if (!entity.Role.StartsWith("MSAA.Role", StringComparison.Ordinal)) return entity.Role;
        var kinds = sources.Select(n => P(n, "controlType")).Where(k => k.Length > 0).Distinct().ToArray();
        return kinds.Length == 1 ? kinds[0] : entity.Role;
    }

    internal static IEnumerable<GraphProperty> Describe(MapEntity entity, IReadOnlyList<GraphNode> sources)
    {
        yield return new("semanticProjection", "map-interpretation/1");
        yield return new("semanticLineage", "semantic_interpreted_from");
        yield return new("semanticMaterialization", sources.Count == 0 ? "diagnostic-only" : "reproducible");
        yield return new("semanticAuthority", "Map draft; interpretation is not publication or action authority");
        yield return new("semanticEntityKind", entity.EntityKind.ToString());
        yield return new("mapRole", entity.Role);
        foreach (var matcher in entity.Matchers.Where(m => !m.VolatileValue && m.Key is "automationId" or "className" or "frameworkId"))
            yield return new("semanticSelector", matcher.Key + "=" + matcher.ExpectedValue, true);
        foreach (var limitation in entity.Limitations) yield return new("semanticDiagnostic", limitation);
        if (entity.EntityKind != MapEntityKind.MapSurface) yield break;
        var classes = sources.Select(s => P(s, "surfaceClass")).Where(s => s.Length > 0).Distinct().ToArray();
        var semanticClass = classes.Length == 1 ? classes[0] switch
        {
            "RawDialogWindow" => "SemanticDialogWindow",
            "RawPopupWindow" => "SemanticPopupWindow",
            "RawTooltipWindow" => "SemanticTooltipWindow",
            "RawToolWindow" => "SemanticToolWindow",
            _ => "SemanticWindow"
        } : "SemanticWindow";
        yield return new("semanticClass", semanticClass);
        // The Map surface is a durable family; its MapSurfaceVariant states below
        // preserve concrete content identity separately from observation identity.
        if (semanticClass == "SemanticPopupWindow")
        {
            yield return new("semanticPopupFamilyId", entity.MapEntityRef);
            yield return new("semanticAnchorStatus", "unresolved; no observed opener relationship was supplied by Map");
        }
        if (classes.Length > 1)
            yield return new("semanticDiagnostic", "Raw World sources disagree on surface type; review required.");
        foreach (var source in sources)
            yield return new("semanticSourceType", P(source, "surfaceClass"));
    }

    internal static void AddVariants(UiMapDraft draft, IDictionary<string, GraphNode> nodes)
    {
        var rawStates = nodes.Values.Where(n => n.Kind == GraphNodeKind.State && P(n, "layer") == "raw-world")
            .GroupBy(n => n.ParentId).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var variant in draft.Entities.Where(e => e.EntityKind == MapEntityKind.MapSurfaceVariant))
        {
            if (!nodes.TryGetValue(variant.ParentEntityRef, out var surface)) continue;
            var observations = draft.Sources.Where(s => s.SurfaceObservations.Any(o => o.ResultingMapVariantRef == variant.MapEntityRef)
                || variant.EvidenceLinks.Any(l => l.SourceObservationSetRef == s.SourceObservationSetRef))
                .Select(s => s.SourceObservationSetRef).ToHashSet();
            // Source refs are retained on the raw states, so matching cannot drift
            // to another recording that happens to use the same frame number.
            var states = surface.Properties.Where(p => p.Name == "sourceRawSurfaceId")
                .SelectMany(p => rawStates.GetValueOrDefault(p.Value) ?? [])
                .Where(n => observations.Contains(P(n, "sourceObservationSetRef"))).ToArray();
            if (states.Length == 0) continue; // unobserved placeholder variants have no visual evidence
            var properties = new List<GraphProperty>
            {
                new("layer", "semantic-world"), new("semanticVariantRef", variant.MapEntityRef),
                new("mapDraftRef", draft.MapDraftRef), new("reviewState", variant.ReviewState.ToString()),
                new("curationHidden", (variant.ReviewState == ReviewState.ReviewRejected).ToString()),
                new("semanticProjection", "map-interpretation/1"), new("semanticLineage", "semantic_interpreted_from")
            };
            properties.AddRange(states.Select(s => new GraphProperty("sourceRawStateId", s.Id)));
            properties.AddRange(variant.Matchers.Where(m => m.Family == "structure" && m.Key == "signature")
                .Select(m => new GraphProperty("semanticContentSignature", m.ExpectedValue)));
            nodes.Add(variant.MapEntityRef, new(variant.MapEntityRef, GraphNodeKind.State, surface.Id,
                variant.MapEntityRef, variant.DisplayName, properties, states.SelectMany(s => s.Evidence).Distinct().ToArray()));
        }
    }
}
