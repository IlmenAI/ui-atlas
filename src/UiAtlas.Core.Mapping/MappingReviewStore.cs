using System.Text.Json;
using UIMaps.Map.Core;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Mapping;

public interface IMappingReviewStore
{
    UiMapService Service { get; }
    IReadOnlyList<UiMapDraft> Drafts { get; }
    IReadOnlyList<MapValidationReport> Validate();
    ApplyReviewCommandResponse Review(ReviewCommand command);
    IReadOnlyList<UiMapRevision> Publish(string reviewer);
    ImportMapRevisionResponse ImportRevision(Google.Protobuf.ByteString bytes);
}

/// <summary>Review authority for native episode imports. The import manifest is
/// a receipt; the durable database is always used for current state.</summary>
public sealed class EpisodeMappingReviewStore : IMappingReviewStore
{
    private readonly string _root;
    private readonly string _draftRef;
    public UiMapService Service { get; }
    public IReadOnlyList<UiMapDraft> Drafts => [Service.GetDraft(_draftRef)];
    public EpisodeMappingReviewStore(string directory)
    {
        _root = Path.GetFullPath(directory);
        var receipt = JsonSerializer.Deserialize<UiMapsEpisodeImportResult>(File.ReadAllText(Path.Combine(_root, "manifest.json")))
            ?? throw new InvalidDataException("Episode import receipt is missing.");
        if (receipt.Format != "ui-atlas.uimaps-import/1" || !receipt.NotAuthorization) throw new InvalidDataException("Unsupported episode import receipt.");
        _draftRef = receipt.MapDraftRef;
        if (!File.Exists(Path.Combine(_root, "map", "map.db"))) throw new InvalidDataException("Map authority database is missing.");
        Service = new UiMapService(Path.Combine(_root, "map"));
    }
    private FileStream Lease() => new(Path.Combine(_root, "review.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public IReadOnlyList<MapValidationReport> Validate()
    {
        using var lease = Lease();
        return Drafts.Select(draft => Service.ValidateDraft(draft.MapDraftRef, draft.Etag)).ToArray();
    }
    public ImportMapRevisionResponse ImportRevision(Google.Protobuf.ByteString bytes)
    {
        using var lease = Lease(); return Service.Import(bytes, true);
    }
    public ApplyReviewCommandResponse Review(ReviewCommand command)
    {
        if (command.MapDraftRef != _draftRef) throw new ArgumentException("The review command names a different draft.");
        using var lease = Lease();
        MappingReviewGuard.ValidateParent(command, Service.GetDraft(_draftRef));
        return Service.ApplyReviewCommand(command);
    }
    public IReadOnlyList<UiMapRevision> Publish(string reviewer)
    {
        using var lease = Lease();
        var draft = Service.GetDraft(_draftRef);
        var validation = Service.ValidateDraft(draft.MapDraftRef, draft.Etag);
        if (!validation.PublicationEligible) throw new InvalidDataException("Explicit review and valid evidence are required before publication.");
        return [Service.Publish(new PublishMapDraftRequest { RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = draft.MapDraftRef,
            ExpectedEtag = draft.Etag, ValidationRunRef = validation.ValidationRunRef, PublishedBy = reviewer }).Revision];
    }
}

public static class MappingReviewGuard
{
    public static void ValidateParent(ReviewCommand command, UiMapDraft draft)
    {
        if (command.CommandKind != ReviewCommandKind.ReviewSetParent) return;
        var byRef = draft.Entities.ToDictionary(entity => entity.MapEntityRef, StringComparer.Ordinal);
        if (!byRef.TryGetValue(command.SubjectRef, out var child) || !byRef.TryGetValue(command.SecondaryRef, out var parent))
            throw new ArgumentException("Select an existing child and parent in this draft.");
        var validKind = child.EntityKind switch
        {
            MapEntityKind.MapApplicationState => parent.EntityKind == MapEntityKind.MapApplication,
            MapEntityKind.MapSurface => parent.EntityKind is MapEntityKind.MapApplicationState or MapEntityKind.MapApplication,
            MapEntityKind.MapSurfaceVariant => parent.EntityKind == MapEntityKind.MapSurface,
            MapEntityKind.MapControl => parent.EntityKind is MapEntityKind.MapControl or MapEntityKind.MapSurfaceVariant or MapEntityKind.MapSurface,
            MapEntityKind.MapDataGrid => parent.EntityKind == MapEntityKind.MapControl,
            MapEntityKind.MapGridColumn => parent.EntityKind == MapEntityKind.MapDataGrid,
            _ => false,
        };
        if (!validKind) throw new ArgumentException("That parent kind cannot contain the selected entity.");
        var seen = new HashSet<string>(StringComparer.Ordinal) { child.MapEntityRef };
        while (true)
        {
            if (!seen.Add(parent.MapEntityRef)) throw new ArgumentException("The parent change would create a containment cycle.");
            if (string.IsNullOrEmpty(parent.ParentEntityRef)) break;
            if (!byRef.TryGetValue(parent.ParentEntityRef, out parent!)) throw new ArgumentException("The parent hierarchy is incomplete.");
        }
    }
}
