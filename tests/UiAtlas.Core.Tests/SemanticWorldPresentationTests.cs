using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;

namespace UiAtlas.Core.Tests;

public sealed class SemanticWorldPresentationTests
{
    private static EvidenceRef E(string bundle = "one", long frame = 1, RectI? bounds = null) =>
        new(bundle, frame, "raw/observations/frame-000001.json", bounds ?? new(10, 20, 30, 40));
    private static GraphNode Raw(string id = "raw", string owner = "surface", string bundle = "one", long frame = 1,
        bool visible = true, RectI? bounds = null) => new(id, GraphNodeKind.Control, owner, id, "Observed name",
        [new("layer", "raw-world"), new("rawSurfaceId", owner), new("sceneReconstruction", "surface-scene/1"),
            .. visible ? new GraphProperty[] { new("sceneVisibleRegion", "10,20,15,40") } : []], [E(bundle, frame, bounds)]);
    private static UiMapControlView Control(params GraphNode[] sources)
    {
        var n = new GraphNode("meaning", GraphNodeKind.Control, "semantic", "meaning", "Reviewed name",
            [new("layer", "semantic-world"), new("semanticProjection", "map-interpretation/1"), new("semanticEntityKind", "MapControl")], [E(bounds: new(100, 200, 300, 400))]);
        return new(n.Id, UiUnderstandingLevel.SemanticWorld, n.Label, "Button", "semantic", "", n.Evidence[0].Bounds!, n.Evidence, n,
            sources, new HashSet<string> { "surface" });
    }

    [Fact]
    public void CurrentSourceGeometryAndClippingPreserveAuthoredMeaningAndStoredEvidence()
    {
        var control = Control(Raw());
        var projected = UiMapPresentation.ForObservation(control, "one", 1);
        Assert.Equal("Reviewed name", projected.DisplayName);
        Assert.Equal(new RectI(10, 20, 30, 40), projected.Bounds);
        Assert.Equal(new[] { new RectI(10, 20, 15, 40) }, RawWorldPresentation.VisibleRegions(projected));
        Assert.Equal(new RectI(100, 200, 300, 400), control.Evidence[0].Bounds);
        Assert.Equal(projected.Bounds, UiMapPresentation.ResolveControlBounds(projected, 1, "one"));
    }

    [Theory]
    [InlineData("one", 2)]
    [InlineData("another-recording", 1)]
    public void MissingExactObservationNeverBorrowsEarlierOrOtherRecordingControls(string bundle, long frame)
    {
        var result = UiMapPresentation.ForObservation(Control(Raw()), bundle, frame);
        Assert.False(ControlGeometry.IsRenderable(result.Bounds));
        Assert.Empty(RawWorldPresentation.VisibleRegions(result));
    }

    [Fact]
    public void OffscreenInactiveTabControlsRemainInspectableButHaveNoVisibleRegions()
    {
        var result = UiMapPresentation.ForObservation(Control(Raw(visible: false)), "one", 1);
        Assert.True(result.Bounds.IsValid);
        Assert.Empty(RawWorldPresentation.VisibleRegions(result));
        Assert.Single(result.ObservationSources!);
    }

    [Fact]
    public void OwnershipConflictIsDiagnosticInsteadOfPaintingOnAnUnrelatedSurface()
    {
        var result = UiMapPresentation.ForObservation(Control(Raw(owner: "other-dialog")), "one", 1);
        Assert.Empty(RawWorldPresentation.VisibleRegions(result));
        Assert.Contains(result.Source.Properties, p => p.Name == "semanticObservationDiagnostic" && p.Value.Contains("owner"));
    }

    [Fact]
    public void ConflictingInterpretationsDoNotChooseTheFirstRectangle()
    {
        var result = UiMapPresentation.ForObservation(Control(Raw(), Raw("second", bounds: new(100, 200, 30, 40))), "one", 1);
        Assert.False(ControlGeometry.IsRenderable(result.Bounds));
        Assert.Empty(RawWorldPresentation.VisibleRegions(result));
    }

    [Fact]
    public void ReviewedGridSchemaDoesNotFabricateColumnGeometryFromItsHost()
    {
        var control = Control(Raw());
        control = control with { Source = control.Source with { Properties = control.Source.Properties.Where(p => p.Name != "semanticEntityKind")
            .Append(new("semanticEntityKind", "MapGridColumn")).ToArray() } };
        Assert.Empty(RawWorldPresentation.VisibleRegions(UiMapPresentation.ForObservation(control, "one", 1)));
        Assert.Single(control.Evidence);
    }

    [Fact]
    public void SemanticVariantUsesAllSourceSurfacesAndExactFrameWindowIdentity()
    {
        var app = new GraphNode("app", GraphNodeKind.Application, "", "app", "Test", [], []);
        GraphNode Surface(string id, string layer, params EvidenceRef[] evidence) => new(id, GraphNodeKind.Surface, "app", id, "Dialog", [new("layer", layer)], evidence);
        GraphNode State(string id, string owner, string layer, string hwnd, params EvidenceRef[] evidence) => new(id, GraphNodeKind.State, owner, id, id,
            [new("layer", layer), new("windowHandleEvidence", hwnd), new("semanticVariantRef", id)], evidence);
        var meaning = Surface("semantic", "semantic-world", E(), E(frame: 2)) with { Properties = [new("layer", "semantic-world"),
            new("semanticProjection", "map-interpretation/1"), new("sourceRawSurfaceId", "first"), new("sourceRawSurfaceId", "second")] };
        var graph = new UiKnowledgeGraph(new("5", "test", "graph", DateTimeOffset.UnixEpoch, "one", "", "test"),
            [app, Surface("first", "raw-world", E()), Surface("second", "raw-world", E(frame: 2)),
                State("first-state", "first", "raw-world", "100", E()), State("second-state", "second", "raw-world", "200", E(frame: 2)),
                meaning, State("variant-a", "semantic", "semantic-world", "", E()), State("variant-b", "semantic", "semantic-world", "", E(frame: 2))], []);
        var model = new UiMappingReadModel(graph);
        var surface = model.LayerFor(UiUnderstandingLevel.SemanticWorld).Surfaces.Single();
        var variants = model.VariantsFor([surface]);
        Assert.Equal(new long?[] { 100, 200 }, variants.Select(v => v.NativeWindowHwnd));
        Assert.Equal(new[] { "variant-a", "variant-b" }, variants.Select(v => v.SemanticVariantId));
        Assert.All(variants, v => Assert.True(v.IsVisibleByDefault));
    }
}
