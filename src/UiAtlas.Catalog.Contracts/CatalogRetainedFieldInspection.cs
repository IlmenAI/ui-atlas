namespace UiAtlas.Catalog;

// Local-only historical presentation. Never use this content as a report label,
// current UI fact, inferred business schema or execution authority.
public sealed record CatalogRetainedFieldInspection(string MapId, string RevisionRef, string CanonicalPayloadHash,
    string FieldId, string Kind, string ParentId, string HistoricalDisplayName, string HistoricalRole,
    string ReviewState, CatalogRetainedFieldEvidence[] Evidence, string[] Limitations, bool Truncated);
public sealed record CatalogRetainedFieldEvidence(string Id, string SourceObservation, string SourceArtifact,
    string SourceObject, string ObservedAt, string Family);
