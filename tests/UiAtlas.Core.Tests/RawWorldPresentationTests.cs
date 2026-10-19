using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;

namespace UiAtlas.Core.Tests;

public sealed class RawWorldPresentationTests
{
    [Theory]
    [InlineData("RawTooltipWindow")]
    [InlineData("SemanticTooltipWindow")]
    public void TooltipRootKeepsTextPixelsInControlsView(string kind)
    {
        var source = new GraphNode("tip", GraphNodeKind.Surface, "app", "tip", "Helpful text", [], []);
        var surface = new UiMapSurfaceView("tip", UiUnderstandingLevel.RawWorld, "Helpful text", kind, "app", new(10, 20, 100, 21), 1, [], [], source);
        var control = new UiMapControlView("root", UiUnderstandingLevel.RawWorld, "Helpful text", "Pane", "tip", "", surface.Bounds, [], source);
        Assert.True(RawVariantPresentation.ShouldUseControlCrop(control, surface));
        Assert.False(RawVariantPresentation.ShouldUseControlCrop(control, surface with { SurfaceKind = "RawPopupWindow" }));
        Assert.False(RawVariantPresentation.ShouldUseControlCrop(control with { Bounds = new(0, 0, 0, 0) }, surface));
    }

    [Fact]
    public void WindowIdentityFollowsTheSelectedObservationRatherThanTheMergedSurfaceHandle()
    {
        EvidenceRef Frame(long frame) => new("recording", frame, $"raw/observations/frame-{frame:D6}.json", new(10, 20, 100, 80));
        var surface = new GraphNode("popup", GraphNodeKind.Surface, "app", "popup", "Menu",
            [new("layer", "raw-world"), new("surfaceClass", "RawPopupWindow"), new("windowHandleEvidence", "100")], [Frame(1), Frame(2)]);
        GraphNode State(long frame, string hwnd) => new("state-" + frame, GraphNodeKind.State, surface.Id, "state-" + frame, "Observed",
            [new("layer", "raw-world"), new("sceneProjection", "surface-observation"), new("windowHandleEvidence", hwnd)], [Frame(frame)]);
        var graph = new UiKnowledgeGraph(new("5", "test", "graph", DateTimeOffset.UnixEpoch, "recording", "", "test"),
            [new("app", GraphNodeKind.Application, "", "app", "Test", [], []), surface, State(1, "100"), State(2, "200")], []);
        var variants = new UiMappingReadModel(graph).LayerFor(UiUnderstandingLevel.RawWorld).Surfaces.Single().Variants;
        Assert.Equal(new long?[] { 100, 200 }, variants.Select(variant => variant.NativeWindowHwnd));
    }

    [Fact]
    public void PresentationMembershipIsSeparateFromOwnershipAndCannotChangeRawDataStreams()
    {
        var source = new GraphNode("item", GraphNodeKind.Control, "main", "item", "Normal",
            [new("scenePresentedOn", "popup")], []);
        var control = new UiMapControlView("item", UiUnderstandingLevel.RawWorld, "Normal", "ListItem", "main", "", new(10, 20, 40, 30), [], source);
        Assert.True(RawWorldPresentation.IsAvailableOnSurface(control, "main"));
        Assert.True(RawWorldPresentation.IsAvailableOnSurface(control, "popup"));
        Assert.False(RawWorldPresentation.IsAvailableOnSurface(control with { Level = UiUnderstandingLevel.RawDataStreams }, "popup"));
        Assert.False(RawWorldPresentation.IsAvailableOnSurface(control, "different-popup"));
    }

    [Fact]
    public void PublishedVisibleRegionsReplaceCoveredGeometryAndUnknownRegionsDoNotInventPixels()
    {
        var source = new GraphNode("item", GraphNodeKind.Control, "main", "item", "Covered button",
            [new("sceneReconstruction", "surface-scene/1"), new("sceneVisibleRegion", "10,20,15,30"),
                new("sceneVisibleRegion", "40,20,10,30"), new("sceneVisibleRegion", "invalid")], []);
        var control = new UiMapControlView("item", UiUnderstandingLevel.RawWorld, source.Label, "Button", "main", "", new(10, 20, 40, 30), [], source);
        Assert.Equal(new[] { new RectI(10, 20, 15, 30), new RectI(40, 20, 10, 30) }, RawWorldPresentation.VisibleRegions(control));
        Assert.Empty(RawWorldPresentation.VisibleRegions(control with { Source = source with { Properties = [new("sceneReconstruction", "surface-scene/1")] } }));
        Assert.Equal(new[] { control.Bounds }, RawWorldPresentation.VisibleRegions(control with { Level = UiUnderstandingLevel.RawDataStreams }));
    }
}
