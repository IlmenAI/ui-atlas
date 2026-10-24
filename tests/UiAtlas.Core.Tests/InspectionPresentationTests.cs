using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Desktop;
using UiAtlas.Core.Reader;
using static UiAtlas.Core.Tests.MapperOverlayWindowTests;

namespace UiAtlas.Core.Tests;

[Collection("Mapper desktop")]
public sealed class InspectionPresentationTests
{
    [Fact]
    public void UnnamedControlsHaveReadableRolesWithoutInventingObservedNames()
    {
        var model = new UiMappingReadModel(Graph());
        var controls = model.LayerFor(UiUnderstandingLevel.SemanticWorld).Controls;
        var pane = controls.Single(c => c.Id == "pane");
        Assert.Equal("Pane · unnamed · ProFrame", UiInspectionPresentation.ControlLabel(pane));
        Assert.Equal("", pane.Source.Label);
        Assert.Equal("Arrival Guest List · DataGrid", UiInspectionPresentation.ControlLabel(controls.Single(c => c.Id == "grid")));
        var parents = UiControlHierarchy.Parents(controls, "ProFrame");
        Assert.Contains("pane", parents.Keys);
        Assert.Equal("pane", UiControlHierarchy.Parents(controls)["grid"]);
        Assert.Contains("grid", UiControlHierarchy.Parents(controls, "Arrival").Keys);
        Assert.Contains("pane", UiControlHierarchy.Parents(controls, "Arrival").Keys);
    }

    [Fact]
    public void FramesUseExistingVariantIdentityAndKeepRepeatedFrameNumbersAcrossRecordings()
    {
        var model = new UiMappingReadModel(Graph());
        var frames = model.LayerFor(UiUnderstandingLevel.SemanticWorld).Surfaces.Single(s => s.Id == "surface").Variants;
        var groups = UiInspectionPresentation.FrameGroups(frames);
        Assert.Equal(2, groups.Count);
        Assert.Equal([1L, 2L], groups.Single(g => g.Id == "semantic:v1").Frames.Select(f => f.FrameSequence));
        Assert.Equal(3, Assert.Single(groups.Single(g => g.Id == "semantic:v2").Frames).FrameSequence);
        var a = frames[0] with { SemanticVariantId = "", SemanticVariantName = "", SurfaceId = "raw", Id = "a" };
        var b = a with { BundleId = "another-recording", Id = "b" };
        var unclassified = Assert.Single(UiInspectionPresentation.FrameGroups([a, b]));
        Assert.Equal("Unclassified observations", unclassified.Label);
        Assert.Equal(2, unclassified.Frames.Count);
        Assert.NotEqual(unclassified.Frames[0].BundleId, unclassified.Frames[1].BundleId);
        Assert.Equal(2, Assert.Single(UiInspectionPresentation.FrameGroups([a, a with
            { Id = "duplicate", FrameSequence = 2, DedupedIntoVariantId = "a" }])).Frames.Count);
        var duplicate = a with { Id = "duplicate", FrameSequence = 2, DedupedIntoVariantId = "a", Reason = "duplicate_content", IsVisibleByDefault = false };
        Assert.Equal(2, UiInspectionPresentation.IncludeEquivalentFrames([a], [a, duplicate,
            duplicate with { Id = "delta", Reason = "control_delta" }, duplicate with { Id = "foreign", DedupedIntoVariantId = "missing" }]).Count);
    }

    [Fact]
    public void GridAssociationUsesExactHostAndRawLineageNeverMatchingNames()
    {
        var model = new UiMappingReadModel(Graph());
        var proposal = Proposal();
        UiMapSurfaceView Surface(string id) => model.Layers.SelectMany(layer => layer.Surfaces).Single(s => s.Id == id);
        Assert.Single(UiInspectionPresentation.GridsForSurface(model, Surface("surface"), [proposal]));
        Assert.Single(UiInspectionPresentation.GridsForSurface(model, Surface("raw"), [proposal]));
        Assert.Empty(UiInspectionPresentation.GridsForSurface(model, Surface("other"), [proposal]));
        Assert.Empty(UiInspectionPresentation.GridsForSurface(model, Surface("unrelated-raw"), [proposal]));
        Assert.Empty(UiInspectionPresentation.GridsForSurface(model, Surface("surface"), [proposal with { HostBound = false }]));
        Assert.Empty(UiInspectionPresentation.GridsForSurface(model, Surface("surface"), [proposal with { TargetId = "missing" }]));
        Assert.Empty(UiInspectionPresentation.GridsForSurface(model, Surface("raw"), [proposal with { SurfaceId = "other" }]));
    }

    [Fact]
    public void EditorShowsInlineGridInBothWorldsAndClearsItForOtherSurfaces()
    {
        Sta(() =>
        {
            using var temp = new TempDirectory();
            var path = Path.Combine(temp.Path, "grid.png");
            var bitmap = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgra32, null,
                Enumerable.Repeat((byte)255, 24).ToArray(), 12);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);
            var proposal = Proposal() with { ImagePath = path, ImageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
            var viewer = new ExplorerWindow(null) { Width = 1500, Height = 950 };
            try
            {
                Set("_model", new UiMappingReadModel(Graph())); Set("_parameterProposals", new[] { proposal });
                viewer.Show(); Call("RefreshAll"); Call("SelectSurfaceById", "surface"); Pump(30);
                Assert.Equal(0, Get<TabControl>("_inspectionTabs").SelectedIndex);
                Assert.NotNull(Get<Image>("_inspectionGridImage").Source);
                Assert.Equal(3, Get<DataGrid>("_inspectionGridColumns").Items.Count);
                Assert.Equal("Arrival Guest List", Get<ComboBox>("_inspectionGridSelector").SelectedItem.ToString());
                Assert.Equal("", ((GridColumn)Get<DataGrid>("_inspectionGridColumns").Items[2]).Label);
                Assert.Contains("separate from the selected frame", Get<TextBlock>("_inspectionGridSummary").Text);
                Assert.Contains("partial or unknown schema", Get<TextBlock>("_inspectionGridSummary").Text);
                var second = proposal with { Id = "grid:second", DisplayName = "Second retained grid",
                    Schema = proposal.Schema! with { Columns = [new("second-column", 0, "Second", 0, 100)] } };
                Set("_parameterProposals", new[] { proposal, second }); Call("RefreshInspectionPreview");
                var grids = Get<ComboBox>("_inspectionGridSelector");
                Assert.Equal(2, grids.Items.Count); grids.SelectedIndex = 1;
                Assert.Equal("Second retained grid", grids.SelectedItem.ToString());
                Assert.Single(Get<DataGrid>("_inspectionGridColumns").Items.Cast<GridColumn>());
                Set("_parameterProposals", new[] { proposal }); Call("RefreshInspectionPreview");
                Assert.Equal(3, Get<DataGrid>("_inspectionGridColumns").Items.Count);
                Assert.True(Get<ScrollViewer>("_variantStripScroller").Height <= 50);
                var variants = Get<ComboBox>("_inspectionVariantSelector");
                Assert.Equal(2, variants.Items.Count);
                variants.SelectedItem = variants.Items.OfType<UiInspectionFrameGroup>().Single(group => group.Id == "semantic:v1");
                Assert.Equal("Guest list", variants.SelectedItem.ToString());
                Assert.Contains("semantic:v1", variants.ToolTip.ToString());
                var frames = Get<StackPanel>("_variantCardsPanel").Children.OfType<Button>().ToArray();
                Assert.Equal(new[] { "1", "2" }, frames.Select(b => b.Content!.ToString()));
                frames[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(2, Get<UiMapVariantView>("_selectedVariant").FrameSequence);
                variants.SelectedItem = variants.Items.OfType<UiInspectionFrameGroup>().Single(group => group.Id == "semantic:v2");
                Assert.Equal(3, Get<UiMapVariantView>("_selectedVariant").FrameSequence);
                Assert.Contains("semantic:v2", variants.ToolTip.ToString());
                Call("BuildHierarchy", "", "grid");
                var hierarchy = Get<Dictionary<string, TreeViewItem>>("_hierarchyItems");
                Assert.Same(hierarchy["pane"], hierarchy["grid"].Parent);
                Assert.Contains("Pane · unnamed", AutomationProperties.GetName(hierarchy["pane"]));
                Assert.Contains("Variant", AutomationProperties.GetName((TreeViewItem)hierarchy["pane"].Parent));
                Get<TabControl>("_inspectionTabs").SelectedIndex = 1;
                Call("RenderAppMap"); Assert.Equal(1, Get<TabControl>("_inspectionTabs").SelectedIndex);
                Call("SelectSurfaceById", "raw");
                Assert.Equal(0, Get<TabControl>("_inspectionTabs").SelectedIndex);
                Assert.NotNull(Get<Image>("_inspectionGridImage").Source);
                Call("SelectSurfaceById", "other");
                Assert.Null(Get<Image>("_inspectionGridImage").Source);
                Assert.Empty(Get<DataGrid>("_inspectionGridColumns").Items);
                Assert.Contains("No retained grid", Get<TextBlock>("_inspectionGridSummary").Text);
                Set("_parameterProposals", new[] { proposal with { ImageSha256 = new string('0', 64) } });
                Call("SelectSurfaceById", "surface");
                Assert.Null(Get<Image>("_inspectionGridImage").Source);
                Assert.Contains("unavailable", Get<TextBlock>("_inspectionGridSummary").Text);
                Assert.Equal(3, Get<DataGrid>("_inspectionGridColumns").Items.Count);
                Set("_parameterDocument", MapParameterizationDocument.Empty("map"));
                Call("EnterParameterReview", "Data grids");
                Assert.Equal(1, Get<TabControl>("_inspectionTabs").SelectedIndex);
                Call("ExitParameterReview"); Call("RefreshAll"); Call("SelectSurfaceById", "surface");
                Assert.Equal(0, Get<TabControl>("_inspectionTabs").SelectedIndex);
                var graph = Graph();
                Set("_model", new UiMappingReadModel(graph with { Nodes = graph.Nodes.Append(new GraphNode(
                    "hidden-frame", GraphNodeKind.State, "raw", "hidden-frame", "Hidden observation",
                    [new("layer", "raw-world"), new("sceneVisible", "False")], graph.Nodes.Single(n => n.Id == "v2").Evidence)).ToArray() }));
                Call("RefreshAll"); Call("SelectSurfaceById", "surface");
                Call("BuildHierarchy", "", "details");
                var hiddenVariant = (TreeViewItem)Get<Dictionary<string, TreeViewItem>>("_hierarchyItems")["details"].Parent;
                hiddenVariant.IsSelected = true; Pump(20);
                Assert.Null(Get<UiMapVariantView?>("_selectedVariant"));
                Assert.Null(Get<Image>("_appMapImage").Source);
                Assert.Contains("Details: no eligible recorded frames", Get<string>("_inspectionUnavailableVariant"));
                Assert.Contains("no eligible frames", variants.SelectedItem.ToString());
                Call("RenderAppMap");
                Assert.Contains("no eligible recorded frames", ((TextBlock)Get<ContentControl>("_traceBannerHost").Content).Text);
                variants.SelectedItem = variants.Items.OfType<UiInspectionFrameGroup>().Single(g => g.Id == "semantic:v1");
                Assert.NotNull(Get<UiMapVariantView>("_selectedVariant"));
                Assert.Null(Get<string?>("_inspectionUnavailableVariant"));
                variants.SelectedItem = variants.Items.OfType<UiInspectionFrameGroup>().Single(g => g.Id == "semantic:v2");
                Assert.Null(Get<UiMapVariantView?>("_selectedVariant"));
                Assert.Contains("no eligible recorded frames", Get<string>("_inspectionUnavailableVariant"));
            }
            finally { viewer.Close(); }
            void Set(string name, object value) => typeof(ExplorerWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, value);
            T Get<T>(string name) => (T)typeof(ExplorerWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
            void Call(string name, params object[] args) => typeof(ExplorerWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewer, args);
        });
    }

    private static ParameterProposal Proposal() => new("grid:arrival", "grid", "surface", "Arrival Guest List", "table_data",
        "Collection", "Mapped grid", "Contents", true, new(0, 0, 300, 100), [], "fingerprint",
        new("schema", false, null, [new("a", 0, "Name", 0, 100), new("b", 1, "Name", 100, 200), new("c", 2, "", 200, 300)], 24, 30));

    private static UiKnowledgeGraph Graph()
    {
        var evidence = new EvidenceRef("recording", 1, "frame.json", new(0, 0, 800, 600));
        EvidenceRef[] Frames(params long[] values) => values.Select(value => evidence with { FrameSequence = value }).ToArray();
        GraphNode Surface(string id, string layer) => new(id, GraphNodeKind.Surface, "app", id, "Same window title",
            [new("layer", layer), new("semanticProjection", "recorded"), new("sourceRawSurfaceId", "raw")], Frames(1, 2, 3));
        GraphNode Control(string id, string parent, string name, string kind, string surface, string layer, string variant) =>
            new(id, GraphNodeKind.Control, parent, id, name, [new("layer", layer), new("controlType", kind),
                new(layer == "raw-world" ? "rawSurfaceId" : "semanticSurfaceId", surface), new("className", "ProFrame"),
                new("semanticVariantRef", variant), new("sourceRawControlId", "raw-grid")], Frames(1, 2));
        return new(new("5", "test", "inspection", DateTimeOffset.UnixEpoch, "recording", "", "test", LogicalMapId: "map"),
            [new("app", GraphNodeKind.Application, "", "app", "Test", [], []),
             Surface("surface", "semantic-world"), Surface("other", "semantic-world"),
             Surface("raw", "raw-world"), Surface("unrelated-raw", "raw-world"),
             new("v1", GraphNodeKind.State, "surface", "v1", "Guest list", [new("layer", "semantic-world")], Frames(1, 2)),
             new("v2", GraphNodeKind.State, "surface", "v2", "Details", [new("layer", "semantic-world")], Frames(3)),
             Control("pane", "v1", "", "Pane", "surface", "semantic-world", "v1"),
             Control("grid", "pane", "Arrival Guest List", "DataGrid", "surface", "semantic-world", "v1"),
             Control("details", "v2", "Details", "Pane", "surface", "semantic-world", "v2") with { Evidence = Frames(3) },
             Control("raw-grid", "raw", "Arrival Guest List", "DataGrid", "raw", "raw-world", "")], []);
    }
}
