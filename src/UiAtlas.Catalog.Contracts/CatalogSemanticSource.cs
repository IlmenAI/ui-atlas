namespace UiAtlas.Catalog;

// Retained UI structure only. Labels use a closed vocabulary, never runtime text/cells.
public sealed record CatalogSemanticField(string Id, string Kind, string Label, string ReviewState,
    string? ParentId, string[] EvidenceRefs);
public sealed record CatalogSemanticRelationship(string Id, string Kind, string FromId, string ToId,
    string ReviewState, string[] EvidenceRefs);
public sealed record CatalogSemanticPage(string MapId, string RevisionRef, string CanonicalPayloadHash,
    bool Ready, bool Archived, CatalogSemanticField[] Fields, CatalogSemanticRelationship[] Relationships,
    string? NextField, int TotalUiFields, int PageRelationshipCount, bool RelationshipsTruncated, string Coverage);
