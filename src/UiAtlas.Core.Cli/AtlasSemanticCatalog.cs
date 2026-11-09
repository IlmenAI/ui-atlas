using System.Text;
using System.Text.Json;
using UiAtlas.Catalog;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

internal static class AtlasSemanticCatalog
{
    internal static CatalogSemanticPage Summarize(string mapId, UiMapRevision revision, bool ready, bool archived,
        string? after, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 30 || after is { Length: > 2048 }) throw new InvalidDataException("Source summaries are bounded to thirty fields per page.");
        if (!revision.Immutable || !revision.NotAuthorization || revision.CanonicalPayloadHash != UIMaps.Protocol.UiMapRevisionIntegrity.Hash(revision)
            || revision.MapRevisionRef != "uimaps:map-revision:sha256:" + revision.CanonicalPayloadHash)
            throw new InvalidDataException("The exact retained revision failed canonical integrity validation.");
        var fields = revision.Entities.Where(e => e.EntityKind is MapEntityKind.MapControl or MapEntityKind.MapGridColumn)
            .OrderBy(e => e.MapEntityRef, StringComparer.Ordinal).ToArray();
        if (fields.Select(f => f.MapEntityRef).Distinct(StringComparer.Ordinal).Count() != fields.Length)
            throw new InvalidDataException("Retained field references are ambiguous.");
        if (after is not null && !fields.Any(f => f.MapEntityRef == after)) throw new InvalidDataException("The field cursor does not belong to this immutable revision.");
        var selected = fields.Where(f => after is null || string.CompareOrdinal(f.MapEntityRef, after) > 0).Take(limit).ToArray();
        var selectedIds = selected.Select(f => f.MapEntityRef).ToHashSet(StringComparer.Ordinal);
        var output = selected.Select(field =>
        {
            token.ThrowIfCancellationRequested(); Check(field.MapEntityRef); if (field.ParentEntityRef.Length > 0) Check(field.ParentEntityRef);
            return new CatalogSemanticField(field.MapEntityRef, field.EntityKind.ToString(), field.EntityKind == MapEntityKind.MapGridColumn ? "Grid column" : "UI control",
                field.ReviewState.ToString(), field.ParentEntityRef.Length == 0 ? null : field.ParentEntityRef,
                Refs(field.EvidenceLinks.Select(e => e.EvidenceLinkRef)));
        }).ToArray();
        var links = revision.Relationships.Where(r => selectedIds.Contains(r.FromEntityRef) && selectedIds.Contains(r.ToEntityRef))
            .OrderBy(r => r.MapRelationshipRef, StringComparer.Ordinal).ToArray();
        var relations = links.Take(20).Select(link =>
        {
            token.ThrowIfCancellationRequested(); Check(link.MapRelationshipRef);
            return new CatalogSemanticRelationship(link.MapRelationshipRef, link.RelationshipKind.ToString(), link.FromEntityRef, link.ToEntityRef,
                link.ReviewState.ToString(), Refs(link.EvidenceLinkRefs));
        }).ToArray();
        var next = selected.Length > 0 && fields.Any(f => string.CompareOrdinal(f.MapEntityRef, selected[^1].MapEntityRef) > 0) ? selected[^1].MapEntityRef : null;
        var result = new CatalogSemanticPage(mapId, revision.MapRevisionRef, revision.CanonicalPayloadHash, ready, archived, output, relations,
            next, fields.Length, links.Length, links.Length > 20,
            "Retained UI control/column declarations only; selected-page UI relationships are not business joins. No live values, inferred types, PMS row counts or execution authorization are supplied. Evidence references are capped at eight per item.");
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result)) > 60 * 1024)
            throw new InvalidDataException("Selected summary exceeds sixty KiB. Request a smaller field page; nothing was silently omitted.");
        return result;
    }
    private static string[] Refs(IEnumerable<string> refs)
    {
        var result = refs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(8).ToArray();
        foreach (var value in result) Check(value); return result;
    }
    private static void Check(string value)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)) throw new InvalidDataException("Retained UI reference is outside its supported bound."); }
}
