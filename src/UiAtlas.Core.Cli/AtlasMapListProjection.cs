using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

/// <summary>Display-only projections. Never pass these to review, publication or localization.
/// Keep identity sets and surface ancestry so grouping across recordings remains exact.</summary>
internal static class AtlasMapListProjection
{
    internal static UiMapDraft Draft(UiMapDraft value) => new()
    {
        MapDraftRef = value.MapDraftRef, MappingSessionRef = value.MappingSessionRef,
        ApplicationRef = value.ApplicationRef, Etag = value.Etag, Version = value.Version, UpdatedAt = value.UpdatedAt,
        Entities = { value.Entities.Select(Entity) }, Sources = { value.Sources.Select(Source) },
        ReviewItems = { value.ReviewItems.Where(item => item.Blocking && item.ReviewState == ReviewState.ReviewUnreviewed)
            .Select(item => new ReviewItem { Blocking = true, ReviewState = item.ReviewState }) },
        Limitations = { value.Limitations },
    };

    internal static UiMapRevision Revision(UiMapRevision value) => new()
    {
        MapRevisionRef = value.MapRevisionRef, ApplicationRef = value.ApplicationRef,
        Target = value.Target?.Clone(), PublishedAt = value.PublishedAt,
        Entities = { value.Entities.Select(Entity) }, Sources = { value.Sources.Select(Source) },
        Limitations = { value.Limitations },
        // Deliberately omit Immutable and CanonicalPayloadHash: this is not an authoritative revision.
    };

    private static MapEntity Entity(MapEntity value) => new()
    {
        MapEntityRef = value.MapEntityRef, EntityKind = value.EntityKind, ParentEntityRef = value.ParentEntityRef,
        DisplayName = value.EntityKind is MapEntityKind.MapSurface or MapEntityKind.MapDataGrid ? value.DisplayName : "",
        Applicable = value.Applicable, ReviewState = value.ReviewState,
    };
    private static SourceObservationSet Source(SourceObservationSet value) => new() { SourceObservationSetRef = value.SourceObservationSetRef };
}
