using System.Security.Cryptography;
using System.Text;
using UIMaps.Protocol.V3;

namespace UIMaps.Protocol;

/// <summary>Reviewed, revision-scoped semantic aliases. Runtime observations remain distinct.</summary>
public sealed class AtlasIdentityEquivalence
{
    private readonly Dictionary<string, MapEntity> _entities;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    public AtlasIdentityEquivalence(IEnumerable<MapEntity> entities, IEnumerable<MapRelationship> relationships)
    {
        _entities = entities.ToDictionary(entity => entity.MapEntityRef, StringComparer.Ordinal);
        foreach (var edge in relationships.Where(edge => edge.RelationshipKind == MapRelationshipKind.MapAliasOf
            && edge.ReviewState is ReviewState.ReviewAccepted or ReviewState.ReviewCorrected))
        {
            if (!_entities.TryGetValue(edge.FromEntityRef, out var from) || !_entities.TryGetValue(edge.ToEntityRef, out var to)
                || !Compatible(from, to) || !_aliases.TryAdd(edge.FromEntityRef, edge.ToEntityRef))
                throw new InvalidDataException("Reviewed Atlas alias has incompatible scope, identity or conflicting targets.");
        }
        foreach (var reference in _aliases.Keys) _ = Canonical(reference);
    }

    public string Canonical(string reference)
    {
        if (!_entities.ContainsKey(reference)) throw new InvalidDataException("Atlas identity is outside the retained revision.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (_aliases.TryGetValue(reference, out var target))
        {
            if (!seen.Add(reference) || seen.Count > 64) throw new InvalidDataException("Atlas alias graph is cyclic or exceeds its chain bound.");
            reference = target;
        }
        return reference;
    }

    public bool Compatible(MapEntity left, MapEntity right) => left.MapEntityRef != right.MapEntityRef
        && left.EntityKind == MapEntityKind.MapControl && right.EntityKind == MapEntityKind.MapControl
        && Reviewed(left) && Reviewed(right) && Signature(left) is { Length: > 0 } signature && signature == Signature(right)
        && Variant(left) is { Length: > 0 } variant && variant != Variant(right);

    public IReadOnlyList<string[]> Candidates() => _entities.Values.Where(entity => entity.EntityKind == MapEntityKind.MapControl && Reviewed(entity))
        .GroupBy(Signature, StringComparer.Ordinal).Where(group => group.Key.Length > 0 && group.Count() is > 1 and <= 64
            && group.Select(Variant).All(value => value.Length > 0) && group.Select(Variant).Distinct(StringComparer.Ordinal).Count() == group.Count())
        .Select(group => group.Select(entity => entity.MapEntityRef).Order(StringComparer.Ordinal).ToArray())
        .Where(group => group.Select(Canonical).Distinct(StringComparer.Ordinal).Count() > 1).ToArray();

    public string Fingerprint(IEnumerable<string> members) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", members.Order(StringComparer.Ordinal).Select(reference => reference + "|" + Signature(_entities[reference])))))).ToLowerInvariant();

    private string Variant(MapEntity entity)
    {
        var seen = new HashSet<string>();
        while (seen.Add(entity.MapEntityRef))
        {
            if (entity.EntityKind == MapEntityKind.MapSurfaceVariant) return entity.MapEntityRef;
            if (!_entities.TryGetValue(entity.ParentEntityRef, out entity!)) return "";
        }
        return "";
    }
    private string Signature(MapEntity entity)
    {
        var parts = new List<string>(); var seen = new HashSet<string>();
        while (seen.Add(entity.MapEntityRef) && seen.Count <= 64)
        {
            if (!Reviewed(entity)) return "";
            if (entity.EntityKind == MapEntityKind.MapSurface) return entity.MapEntityRef + "|" + string.Join("/", parts);
            if (entity.EntityKind != MapEntityKind.MapSurfaceVariant)
                parts.Add(((int)entity.EntityKind).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Encode(entity.Role.ToLowerInvariant()) + Encode(entity.DisplayName));
            if (!_entities.TryGetValue(entity.ParentEntityRef, out entity!)) return "";
        }
        return "";
    }
    private static string Encode(string value) => value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;
    private static bool Reviewed(MapEntity entity) => entity.Applicable
        && entity.ReviewState is ReviewState.ReviewAccepted or ReviewState.ReviewCorrected;
}
