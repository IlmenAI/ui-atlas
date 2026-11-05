using System.Text.Json;
using UiAtlas.Core.Cli;
using UIMaps.Protocol;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Tests;

public sealed class AtlasSemanticCatalogTests
{
    private static UiMapRevision Revision(int count = 2)
    {
        var value = new UiMapRevision { Immutable = true, NotAuthorization = true };
        for (var index = 0; index < count; index++) value.Entities.Add(new MapEntity
        { MapEntityRef = "field-" + index.ToString("D3"), EntityKind = MapEntityKind.MapControl,
            DisplayName = "PRIVATE GUEST VALUE", Role = "PRIVATE CONTROL TEXT", ReviewState = ReviewState.ReviewAccepted });
        Seal(value); return value;
    }
    private static void Seal(UiMapRevision value)
    { value.CanonicalPayloadHash = UiMapRevisionIntegrity.Hash(value); value.MapRevisionRef = "uimaps:map-revision:sha256:" + value.CanonicalPayloadHash; }
    [Fact]
    public void Summary_preserves_exact_refs_without_captured_labels_or_full_revision()
    {
        var revision = Revision(); var page = AtlasSemanticCatalog.Summarize("map", revision, true, false, null, 30, default);
        var json = JsonSerializer.Serialize(page);
        Assert.Equal(revision.CanonicalPayloadHash, page.CanonicalPayloadHash); Assert.Equal(revision.MapRevisionRef, page.RevisionRef);
        Assert.All(page.Fields, f => Assert.Equal("UI control", f.Label));
        Assert.DoesNotContain("PRIVATE", json);
        using var summary = JsonDocument.Parse(json);
        Assert.False(summary.RootElement.TryGetProperty("Payload", out _));
        Assert.False(summary.RootElement.TryGetProperty("ExactRevision", out _));
        Assert.False(summary.RootElement.TryGetProperty("RawWorldSources", out _));
        Assert.False(summary.RootElement.TryGetProperty("Matchers", out _));
        Assert.True(summary.RootElement.TryGetProperty("CanonicalPayloadHash", out _));
    }
    [Fact]
    public void Pages_are_exact_and_outside_cursor_is_rejected()
    {
        var revision = Revision(31); var first = AtlasSemanticCatalog.Summarize("map", revision, false, true, null, 30, default);
        Assert.Equal(30, first.Fields.Length); Assert.NotNull(first.NextField);
        var second = AtlasSemanticCatalog.Summarize("map", revision, false, true, first.NextField, 30, default);
        Assert.Single(second.Fields); Assert.Null(second.NextField); Assert.True(second.Archived);
        Assert.Throws<InvalidDataException>(() => AtlasSemanticCatalog.Summarize("map", revision, true, false, "foreign", 30, default));
    }
    [Fact]
    public void Tamper_duplicate_refs_cancellation_and_size_limits_fail_before_summary()
    {
        var revision = Revision(); revision.Entities[0].DisplayName = "changed";
        Assert.Throws<InvalidDataException>(() => AtlasSemanticCatalog.Summarize("map", revision, true, false, null, 30, default));
        Seal(revision); revision.Entities[1].MapEntityRef = revision.Entities[0].MapEntityRef; Seal(revision);
        Assert.Throws<InvalidDataException>(() => AtlasSemanticCatalog.Summarize("map", revision, true, false, null, 30, default));
        Assert.Throws<OperationCanceledException>(() => AtlasSemanticCatalog.Summarize("map", Revision(), true, false, null, 30, new(true)));
        Assert.Throws<InvalidDataException>(() => AtlasSemanticCatalog.Summarize("map", Revision(), true, false, null, 31, default));
    }
    [Fact]
    public void Only_selected_endpoint_ui_links_are_returned_and_never_business_joins()
    {
        var revision = Revision(3);
        revision.Relationships.Add(new MapRelationship { MapRelationshipRef = "ui-link", FromEntityRef = "field-000", ToEntityRef = "field-001", RelationshipKind = MapRelationshipKind.MapContains });
        revision.Relationships.Add(new MapRelationship { MapRelationshipRef = "outside-page", FromEntityRef = "field-000", ToEntityRef = "field-002", RelationshipKind = MapRelationshipKind.MapContains }); Seal(revision);
        var page = AtlasSemanticCatalog.Summarize("map", revision, true, false, null, 2, default);
        Assert.Equal("ui-link", Assert.Single(page.Relationships).Id); Assert.Contains("not business joins", page.Coverage);
    }
}
