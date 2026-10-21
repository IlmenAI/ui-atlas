using UIMaps.Protocol;
using UIMaps.Protocol.V3;
using UIMaps.Map.Core;
using Xunit;

namespace UiAtlas.Core.Tests;

public sealed class AtlasIdentityEquivalenceTests
{
    [Fact]
    public void AliasBatchIsAtomicAndHasOneUndoBoundary()
    {
        using var temp = new TempDirectory();
        var store = new UiMapStore(temp.Path); var service = new UiMapService(temp.Path);
        var entities = Entities();
        entities.Add(Entity("v3", "surface", "Variant 3", MapEntityKind.MapSurfaceVariant));
        entities.Add(Entity("c", "v3", "A1", MapEntityKind.MapControl));
        var draft = new UiMapDraft { MapDraftRef = "draft", MappingSessionRef = "session", Etag = "initial", Entities = { entities } };
        store.SaveSessionAndDraft(new MappingSessionDescriptor { MappingSessionRef = "session", MapDraftRef = "draft" }, draft);
        var command = new ReviewCommand { RequestId = "review", MapDraftRef = "draft", ExpectedEtag = "initial",
            CommandKind = ReviewCommandKind.ReviewSetAlias, ReviewedBy = "test", Aliases = {
                new ReviewedMapAlias { FromEntityRef = "b", ToEntityRef = "a", Reason = "Same control" },
                new ReviewedMapAlias { FromEntityRef = "c", ToEntityRef = "missing", Reason = "Invalid" } } };
        Assert.Throws<UiMapException>(() => service.ApplyReviewCommand(command));
        Assert.Empty(service.GetDraft("draft").Relationships);
        Assert.Empty(store.LoadReviewStacks("draft").Undo);
        command.Aliases[1].ToEntityRef = "a";
        var result = service.ApplyReviewCommand(command);
        Assert.Equal(2, result.Draft.Relationships.Count);
        Assert.Single(store.LoadReviewStacks("draft").Undo);
        var undone = service.ApplyReviewCommand(new ReviewCommand { RequestId = "undo", MapDraftRef = "draft",
            ExpectedEtag = result.Draft.Etag, CommandKind = ReviewCommandKind.ReviewUndo, ReviewedBy = "test" });
        Assert.Empty(undone.Draft.Relationships);
    }
    [Fact]
    public void CorrespondingVariantsStayDistinctUntilExplicitlyReviewed()
    {
        var entities = Entities(); var suggestion = new AtlasIdentityEquivalence(entities, []);
        Assert.Single(suggestion.Candidates()); Assert.Equal("b", suggestion.Canonical("b"));
        var accepted = new AtlasIdentityEquivalence(entities, [Alias("b", "a")]);
        Assert.Equal("a", accepted.Canonical("b")); Assert.Empty(accepted.Candidates());
        Assert.Equal(5, entities.Count); // original entities and their evidence are not removed
    }
    [Fact]
    public void CyclesAndConflictingTargetsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => new AtlasIdentityEquivalence(Entities(), [Alias("a", "b"), Alias("b", "a")]));
        Assert.Throws<InvalidDataException>(() => new AtlasIdentityEquivalence(Entities(), [Alias("b", "a"), Alias("b", "a")]));
    }
    [Theory]
    [InlineData("role")]
    [InlineData("path")]
    [InlineData("same-variant")]
    [InlineData("rejected")]
    public void SimilarLabelsCannotOverrideScopeOrReview(string change)
    {
        var entities = Entities(); var b = entities.Single(entity => entity.MapEntityRef == "b");
        if (change == "role") b.Role = "Edit";
        if (change == "path") b.DisplayName = "A2";
        if (change == "same-variant") b.ParentEntityRef = "v1";
        if (change == "rejected") b.ReviewState = ReviewState.ReviewRejected;
        Assert.Throws<InvalidDataException>(() => new AtlasIdentityEquivalence(entities, [Alias("b", "a")]));
    }
    private static List<MapEntity> Entities() =>
    [
        Entity("surface", "", "Excel", MapEntityKind.MapSurface),
        Entity("v1", "surface", "Default", MapEntityKind.MapSurfaceVariant),
        Entity("v2", "surface", "Variant 2", MapEntityKind.MapSurfaceVariant),
        Entity("a", "v1", "A1", MapEntityKind.MapControl),
        Entity("b", "v2", "A1", MapEntityKind.MapControl)
    ];
    private static MapEntity Entity(string id, string parent, string name, MapEntityKind kind) => new()
    { MapEntityRef = id, ParentEntityRef = parent, DisplayName = name, EntityKind = kind, Role = "Cell", Applicable = true, ReviewState = ReviewState.ReviewAccepted };
    private static MapRelationship Alias(string from, string to) => new() { MapRelationshipRef = from + to,
        FromEntityRef = from, ToEntityRef = to, RelationshipKind = MapRelationshipKind.MapAliasOf, ReviewState = ReviewState.ReviewAccepted };
}
