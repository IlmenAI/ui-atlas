using System.Text.Json;
using UiAtlas.Core.Cli;
using UIMaps.Protocol;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Tests;

public sealed class AtlasRetainedFieldInspectionTests
{
    [Fact]
    public void Historical_selected_field_is_exact_local_content_and_never_changes_static_report_labels()
    {
        var revision = Revision(); var token = CancellationToken.None;
        var detail = AtlasRetainedFieldInspection.Read("map", revision, "field", token);
        Assert.Equal("PRIVATE retained guest", detail.HistoricalDisplayName);
        Assert.Equal(revision.MapRevisionRef, detail.RevisionRef); Assert.Equal("field", detail.FieldId);
        var evidence = Assert.Single(detail.Evidence); Assert.Equal("original-observation", evidence.SourceObservation);
        var report = AtlasSemanticCatalog.Summarize("map", revision, false, false, null, 30, token);
        Assert.Equal("UI control", Assert.Single(report.Fields).Label);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(report));
        using var projection = JsonDocument.Parse(JsonSerializer.Serialize(detail));
        Assert.False(projection.RootElement.TryGetProperty("Matchers", out _)); Assert.False(projection.RootElement.TryGetProperty("Payload", out _));
    }
    [Fact]
    public void Missing_ambiguous_tampered_or_cancelled_field_inspection_never_substitutes_a_different_field()
    {
        var token = CancellationToken.None; var revision = Revision();
        Assert.Throws<InvalidDataException>(() => AtlasRetainedFieldInspection.Read("map", revision, "missing", token));
        revision.Entities.Add(revision.Entities[0].Clone()); Seal(revision);
        Assert.Throws<InvalidDataException>(() => AtlasRetainedFieldInspection.Read("map", revision, "field", token));
        revision = Revision(); revision.Entities[0].DisplayName = "tampered";
        Assert.Throws<InvalidDataException>(() => AtlasRetainedFieldInspection.Read("map", revision, "field", token));
        Assert.Throws<OperationCanceledException>(() => AtlasRetainedFieldInspection.Read("map", Revision(), "field", new(true)));
    }
    private static UiMapRevision Revision()
    {
        var revision = new UiMapRevision { Immutable = true, NotAuthorization = true };
        var field = new MapEntity { MapEntityRef = "field", EntityKind = MapEntityKind.MapControl, DisplayName = "PRIVATE retained guest", Role = "edit" };
        field.EvidenceLinks.Add(new MapEvidenceLink { EvidenceLinkRef = "evidence", SourceObservationSetRef = "original-observation", ObservedAt = "2026-01-01T00:00:00Z" });
        revision.Entities.Add(field); Seal(revision); return revision;
    }
    private static void Seal(UiMapRevision revision)
    { revision.CanonicalPayloadHash = UiMapRevisionIntegrity.Hash(revision); revision.MapRevisionRef = "uimaps:map-revision:sha256:" + revision.CanonicalPayloadHash; }
}
