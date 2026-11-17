using UIMaps.Protocol.V1;

namespace UIMaps.Protocol;

/// <summary>Observed surface content, independent of provider tree and runtime handles.
/// Expectations constrain identity; they never assert that an action succeeded.</summary>
public sealed record SurfaceContextEvidence(string ClassName, string Title, string[] Text)
{
    public static WorldObject? Owner(WorldObject node, IReadOnlyDictionary<string, WorldObject> objects)
    {
        if (node.ObjectKind == WorldObjectKind.WindowObject) return node;
        var owner = LogicalControlIdentity.Property(node, "sceneOwnerSurfaceRef");
        if (objects.TryGetValue(owner, out var surface) && surface.ObjectKind == WorldObjectKind.WindowObject) return surface;
        var seen = new HashSet<string>();
        while (seen.Add(node.WorldObjectId))
        {
            if (node.ObjectKind == WorldObjectKind.WindowObject) return node;
            if (!objects.TryGetValue(node.ParentWorldObjectId, out node!)) break;
        }
        return null;
    }

    public static SurfaceContextEvidence Describe(WorldObject surface, IReadOnlyDictionary<string, WorldObject> objects)
    {
        var text = objects.Values.Where(node => Owner(node, objects)?.WorldObjectId == surface.WorldObjectId
                && node.ObjectKind == WorldObjectKind.AccessibilityElement
                && LogicalControlIdentity.Property(node, "controlType") is "Text" or "Static"
                && !LogicalControlIdentity.Property(node, "isOffscreen").Equals("true", StringComparison.OrdinalIgnoreCase)
                && node.Name.Length is > 0 and <= 1000)
            .Select(node => LogicalControlIdentity.Normalize(node.Name)).Distinct().Order(StringComparer.Ordinal).Take(32).ToArray();
        return new(LogicalControlIdentity.Property(surface, "className"), surface.Name, text);
    }

    public bool Matches(WorldObject surface, IReadOnlyDictionary<string, WorldObject> objects)
    {
        var actual = Describe(surface, objects);
        return ClassName.Length > 0 && actual.ClassName == ClassName && actual.Title == Title
            && Text.Length > 0 && Text.All(actual.Text.Contains);
    }
}
