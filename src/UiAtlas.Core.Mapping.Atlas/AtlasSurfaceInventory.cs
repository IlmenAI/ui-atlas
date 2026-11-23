using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Mapping.Atlas;

internal sealed record AtlasGesture(MapEntity Entity, string Verb, string ControlType, string AutomationId, ObservedMapInteraction? Observation);
internal sealed record AtlasTransition(string From, string To, ObservedMapInteraction Interaction);
internal sealed record AtlasSurface(string Id, string Name, MapEntity[] Controls, MapEntity[] Grids, AtlasGesture[] Actions,
    ObservedMapInteraction[] Interactions, string[] Limitations);

/// <summary>Presentation and extraction evidence from the canonical entities of a published map.</summary>
internal sealed class AtlasSurfaceInventory
{
    public AtlasSurface[] Surfaces { get; }
    public AtlasTransition[] Transitions { get; }
    public IReadOnlyDictionary<string, string> Owners { get; }

    // The map viewer and action inventory use the same containment rule for variants and nested controls.
    internal static string Owner(IReadOnlyDictionary<string, MapEntity> entities, string entityRef)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (entities.TryGetValue(entityRef, out var entity) && visited.Add(entityRef))
        {
            if (entity.EntityKind == MapEntityKind.MapSurface) return entityRef;
            entityRef = entity.ParentEntityRef;
        }
        return "";
    }

    public AtlasSurfaceInventory(UiMapRevision revision)
    {
        var entities = revision.Entities.Where(e => Usable(e.ReviewState)).ToDictionary(e => e.MapEntityRef);
        var owners = entities.Keys.ToDictionary(id => id, id => Owner(entities, id));
        Owners = owners;
        var controls = entities.Values.Where(e => e.EntityKind == MapEntityKind.MapControl).ToLookup(e => owners[e.MapEntityRef]);
        var grids = entities.Values.Where(e => e.EntityKind == MapEntityKind.MapDataGrid).ToLookup(e => owners[e.MapEntityRef]);
        var interactions = revision.Interactions.Where(i => Usable(i.ReviewState)).OrderBy(i => i.SequenceNumber).ToArray();
        Transitions = interactions.Where(i => i.Outcome == MapInteractionOutcome.MapInteractionStateChanged)
            .SelectMany(i => i.ResultSurfaceVariantRefs.Select(id => owners.GetValueOrDefault(id, "")).Distinct()
                .Select(to => new AtlasTransition(owners.GetValueOrDefault(i.SourceMapEntityRef, ""), to, i)))
            .Where(t => t.From.Length > 0 && t.To.Length > 0 && t.From != t.To).ToArray();
        var byControl = interactions.ToLookup(i => i.SourceMapEntityRef);
        var bySurface = interactions.ToLookup(i => owners.GetValueOrDefault(i.SourceMapEntityRef, ""));
        AtlasSurface Build(string id, string name, IEnumerable<string> limitations)
        {
            var members = controls[id].ToArray();
            return new(id, name, members, grids[id].ToArray(),
                members.Where(e => e.Applicable).SelectMany(e => Gestures(e, byControl[e.MapEntityRef])).ToArray(),
                bySurface[id].ToArray(), limitations.Distinct().ToArray());
        }
        var surfaces = entities.Values.Where(e => e.EntityKind == MapEntityKind.MapSurface)
            .OrderBy(e => e.ReviewedOrder).ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.MapEntityRef, StringComparer.Ordinal)
            .Select(e => Build(e.MapEntityRef, string.IsNullOrWhiteSpace(e.DisplayName) ? "Unnamed surface" : e.DisplayName, e.Limitations)).ToList();
        if (controls.Contains("") || grids.Contains("") || bySurface.Contains(""))
            surfaces.Add(Build("", "Surface unknown", []));
        Surfaces = surfaces.ToArray();
    }

    private static bool Usable(ReviewState state) =>
        state is not (ReviewState.ReviewRejected or ReviewState.ReviewConflicting or ReviewState.ReviewDeferred);

    private static string Matcher(MapEntity entity, string key) => entity.Matchers.Where(m => m.Key == key && !m.VolatileValue && Usable(m.ReviewState))
        .Select(m => m.ExpectedValue).Distinct(StringComparer.Ordinal).ToArray() is [var value] && value.Length <= 256 ? value : "";

    private static IEnumerable<AtlasGesture> Gestures(MapEntity entity, IEnumerable<ObservedMapInteraction> interactions)
    {
        var type = Matcher(entity, "controlType");
        if (type.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase)) type = type[12..];
        var role = (string.IsNullOrWhiteSpace(type) ? entity.Role : type).ToLowerInvariant();
        var id = Matcher(entity, "automationId");
        var verbs = new Dictionary<string, ObservedMapInteraction?>();
        foreach (var interaction in interactions)
        {
            var verb = interaction.Kind switch
            {
                MapInteractionKind.MapInteractionPointerPrimary => "invoke",
                MapInteractionKind.MapInteractionPointerDoubleClick => "double-click",
                _ => ""
            };
            if (verb.Length > 0) verbs.TryAdd(verb, interaction);
        }
        var inferred = role switch
        {
            "button" or "push button" or "menuitem" or "menu item" or "hyperlink" => "invoke",
            "edit" or "textbox" or "text box" => "type",
            "listitem" or "tabitem" => "select",
            _ => ""
        };
        if (inferred.Length > 0) verbs.TryAdd(inferred, null);
        return verbs.Select(v => new AtlasGesture(entity, v.Key, type, id, v.Value));
    }
}
