using System.Text;
using System.Text.Json;
using UiAtlas.Catalog;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

internal static class AtlasRetainedFieldInspection
{
    internal static CatalogRetainedFieldInspection Read(string map, UiMapRevision revision, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!revision.Immutable || !revision.NotAuthorization || revision.CanonicalPayloadHash != UIMaps.Protocol.UiMapRevisionIntegrity.Hash(revision)
            || revision.MapRevisionRef != "uimaps:map-revision:sha256:" + revision.CanonicalPayloadHash)
            throw new InvalidDataException("The retained revision failed its canonical integrity check.");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 2048) throw new InvalidDataException("Choose one exact retained field reference.");
        var selected = revision.Entities.Where(e => e.MapEntityRef == id).Take(2).ToArray();
        if (selected.Length != 1 || selected[0].EntityKind is not (MapEntityKind.MapControl or MapEntityKind.MapGridColumn))
            throw new InvalidDataException("The exact declared field is missing or ambiguous in this immutable revision.");
        var field = selected[0]; var truncated = false;
        string Text(string value)
        {
            token.ThrowIfCancellationRequested();
            if (value.Length <= 2048) return value;
            truncated = true; return value[..2048] + " [historical text truncated]";
        }
        var evidence = field.EvidenceLinks.Take(8).Select(e => new CatalogRetainedFieldEvidence(Text(e.EvidenceLinkRef),
            Text(e.SourceObservationSetRef), Text(e.SourceArtifactRef), Text(e.SourceObjectRef), Text(e.ObservedAt), Text(e.EvidenceFamily))).ToArray();
        if (field.EvidenceLinks.Count > 8 || field.Limitations.Count > 8) truncated = true;
        var limitations = field.Limitations.Take(8).Select(Text).ToList();
        limitations.Add("Historical retained map content only. Display text may contain captured business values; it is shown locally and is not copied to the shared catalog. No live UI read or business meaning is inferred.");
        if (evidence.Length == 0) limitations.Add("No retained evidence links are available for this field.");
        var result = new CatalogRetainedFieldInspection(map, revision.MapRevisionRef, revision.CanonicalPayloadHash, id, field.EntityKind.ToString(),
            Text(field.ParentEntityRef), Text(field.DisplayName), Text(field.Role), field.ReviewState.ToString(), evidence, limitations.ToArray(), truncated);
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result)) > 60 * 1024)
            throw new InvalidDataException("This historical field projection exceeds sixty KiB. No partial evidence is substituted.");
        return result;
    }
}
