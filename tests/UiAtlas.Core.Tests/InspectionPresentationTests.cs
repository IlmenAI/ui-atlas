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
    public void VariantTreesKeepExactGridMembershipAndUnclassifiedControls()
    {
        var model = new UiMappingReadModel(Graph());
        var surface = model.LayerFor(UiUnderstandingLevel.SemanticWorld).Surfaces.Single(s => s.Id == "surface");
        var first = Proposal();
        var second = first with { Id = "grid:second", TargetId = "grid2" }; // Same label is not identity.
        var details = first with { Id = "grid:details", TargetId = "details" };
        var groups = UiInspectionPresentation.Variants(model, surface, [first, second, details, first with { Id = "unbound", HostBound = false }]);
        Assert.Equal(["grid:arrival", "grid:second"], groups.Single(g => g.Group.Id == "semantic:v1").Grids.Select(g => g.Id));
        Assert.Equal("grid:details", Assert.Single(groups.Single(g => g.Group.Id == "semantic:v2").Grids).Id);
        Assert.Equal(["pane", "grid", "grid2"], groups.Single(g => g.Group.Id == "semantic:v1").Controls.Select(c => c.Id));
        Assert.True(UiInspectionPresentation.Matches(first, "c"));
        var raw = model.LayerFor(UiUnderstandingLevel.RawWorld).Surfaces.Single(s => s.Id == "raw");
        Assert.Contains(UiInspectionPresentation.Variants(model, raw, [first]), g => g.Controls.Any(c => c.Id == "raw-grid") && g.Grids.Count == 1);
        var orphan = Graph().Nodes.Single(n => n.Id == "grid") with { Id = "orphan", ParentId = "surface", Evidence = [],
            Properties = Graph().Nodes.Single(n => n.Id == "grid").Properties.Where(p => p.Name != "semanticVariantRef").ToArray() };
        var withOrphan = new UiMappingReadModel(Graph() with { Nodes = Graph().Nodes.Append(orphan).ToArray() });
        var unknown = UiInspectionPresentation.Variants(withOrphan, surface, [first with { Id = "grid:orphan", TargetId = "orphan" }]);
        Assert.Equal("grid:orphan", Assert.Single(unknown.Single(v => v.Group.Id == "unclassified:surface").Grids).Id);
        Assert.DoesNotContain(unknown.Where(v => v.Group.Id.StartsWith("semantic:")), v => v.Grids.Count > 0);
        var emptyState = Graph().Nodes.Single(n => n.Id == "v2") with { Id = "empty-variant", Evidence = [] };
        var emptyModel = new UiMappingReadModel(Graph() with { Nodes = Graph().Nodes.Append(emptyState).ToArray() });
        var empty = UiInspectionPresentation.Variants(emptyModel, surface, []).Single(v => v.Group.Id == "semantic:empty-variant");
        Assert.Empty(empty.Group.Frames); Assert.Empty(empty.Controls); Assert.Empty(empty.Grids);
    }

    [Fact]
    public void EditorKeepsPipelineVisibleAcrossVariantsWorldsAndGridReview()
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
                var pipeline = Get<Canvas>("_topologyCanvas");
                Assert.True(viewer.IsAncestorOf(pipeline));
                Assert.DoesNotContain(VisualDescendants(viewer).OfType<TabControl>(), tabs =>
                    tabs.Items.OfType<TabItem>().Any(item => item.Header is "Data grids" or "Pipeline"));
                Assert.Contains(VisualDescendants(viewer).OfType<Button>(), button => Equals(button.Content, "Data grids"));
                var second = proposal with { Id = "grid:second", TargetId = "grid2", DisplayName = "Second retained grid",
                    Schema = proposal.Schema! with { Columns = [new("second-column", 0, "Second", 0, 100)] } };
                Set("_parameterProposals", new[] { proposal, second }); Call("RefreshInspectionTitle");
                Set("_parameterProposals", new[] { proposal }); Call("RefreshInspectionTitle");
                Assert.True(Get<ScrollViewer>("_variantStripScroller").Height <= 50);
                var variants = Get<ListBox>("_inspectionVariantSelector");
                Assert.True(variants.IsVisible);
                Assert.Equal(2, variants.Items.Count);
                viewer.UpdateLayout();
                var firstVariant = Assert.IsType<ListBoxItem>(variants.ItemContainerGenerator.ContainerFromIndex(0));
                var secondVariant = Assert.IsType<ListBoxItem>(variants.ItemContainerGenerator.ContainerFromIndex(1));
                var firstPosition = firstVariant.TransformToAncestor(variants).Transform(new Point());
                var secondPosition = secondVariant.TransformToAncestor(variants).Transform(new Point());
                Assert.Equal(firstPosition.Y, secondPosition.Y, 1); Assert.True(secondPosition.X > firstPosition.X);
                variants.SelectedItem = variants.Items.OfType<UiInspectionFrameGroup>().Single(group => group.Id == "semantic:v1");
                Assert.Equal("Guest list", variants.SelectedItem.ToString());
                Assert.Contains("semantic:v1", variants.ToolTip.ToString());
                var frames = Get<StackPanel>("_variantCardsPanel").Children.OfType<Button>().ToArray();
                Assert.Equal(new[] { "1", "2" }, frames.Select(b => b.Content!.ToString()));
                frames[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(2, Get<UiMapVariantView>("_selectedVariant").FrameSequence);
                Set("_parameterProposals", new[] { proposal, second });
                Call("BuildHierarchy", "", "grid");
                var tree = Get<Dictionary<string, TreeViewItem>>("_hierarchyItems");
                var controlBranch = (TreeViewItem)tree["pane"].Parent;
                Assert.Equal("Controls", AutomationProperties.GetName(controlBranch));
                var variantNode = (TreeViewItem)controlBranch.Parent;
                Assert.Equal(new[] { "Controls", "Data grids" }, variantNode.Items.OfType<TreeViewItem>().Select(AutomationProperties.GetName));
                var gridBranch = (TreeViewItem)variantNode.Items[1];
                Assert.Equal(2, gridBranch.Items.Count);
                var secondNode = (TreeViewItem)gridBranch.Items[1];
                secondNode.IsSelected = true; Pump(20);
                Assert.Same(secondNode, Get<TreeView>("_hierarchy").SelectedItem);
                Assert.Equal(2, Get<UiMapVariantView>("_selectedVariant").FrameSequence);
                var firstGridNode = (TreeViewItem)gridBranch.Items[0];
                var blankColumn = (TreeViewItem)firstGridNode.Items[2];
                Assert.Equal("3. (unnamed column)", AutomationProperties.GetName(blankColumn));
                blankColumn.IsSelected = true; Pump(20);
                Assert.Equal(2, Get<UiMapVariantView>("_selectedVariant").FrameSequence);
                Assert.Equal("1. Name", AutomationProperties.GetName((TreeViewItem)firstGridNode.Items[0]));
                Assert.Equal("2. Name", AutomationProperties.GetName((TreeViewItem)firstGridNode.Items[1]));
                // Metadata arriving after expansion refreshes the existing branch and leaves controls intact.
                Set("_parameterProposals", new[] { proposal }); Call("RefreshInspectionHierarchyGrids"); Call("RefreshInspectionTitle");
                Assert.Single(gridBranch.Items.Cast<TreeViewItem>());
                Assert.Same(controlBranch, tree["pane"].Parent);
                Call("BuildHierarchy", "Second", "");
                Assert.DoesNotContain("surface", Get<Dictionary<string, TreeViewItem>>("_hierarchyItems").Keys);
                Set("_parameterProposals", new[] { proposal, second }); Call("BuildHierarchy", "second-column", "");
                var searchSurface = Get<Dictionary<string, TreeViewItem>>("_hierarchyItems")["surface"];
                var searchVariant = Assert.Single(searchSurface.Items.OfType<TreeViewItem>());
                Assert.Equal(2, searchVariant.Items.Count);
                Assert.Equal("Second retained grid", AutomationProperties.GetName(Assert.Single(((TreeViewItem)searchVariant.Items[1]).Items.OfType<TreeViewItem>())));
                Set("_parameterProposals", new[] { proposal });
                variants.SelectedItem = variants.Items.OfType<UiInspectionFrameGroup>().Single(group => group.Id == "semantic:v2");
                Assert.Equal(3, Get<UiMapVariantView>("_selectedVariant").FrameSequence);
                Assert.Contains("semantic:v2", variants.ToolTip.ToString());
                Call("BuildHierarchy", "", "grid");
                var hierarchy = Get<Dictionary<string, TreeViewItem>>("_hierarchyItems");
                Assert.Same(hierarchy["pane"], hierarchy["grid"].Parent);
                Assert.Contains("Pane · unnamed", AutomationProperties.GetName(hierarchy["pane"]));
                Assert.Equal("Controls", AutomationProperties.GetName((TreeViewItem)hierarchy["pane"].Parent));
                Assert.Contains("Variant", AutomationProperties.GetName((TreeViewItem)((TreeViewItem)hierarchy["pane"].Parent).Parent));
                Call("SelectSurfaceById", "raw");
                Assert.True(variants.IsVisible); Assert.NotEmpty(variants.Items);
                Call("BuildHierarchy", "", "raw-grid");
                var rawControlBranch = (TreeViewItem)Get<Dictionary<string, TreeViewItem>>("_hierarchyItems")["raw-grid"].Parent;
                Assert.Equal("Controls", AutomationProperties.GetName(rawControlBranch));
                Assert.Equal(new[] { "Controls", "Data grids" }, ((TreeViewItem)rawControlBranch.Parent).Items.OfType<TreeViewItem>().Select(AutomationProperties.GetName));
                Call("SelectSurfaceById", "other");
                Set("_parameterProposals", new[] { proposal with { ImageSha256 = new string('0', 64) } });
                Call("SelectSurfaceById", "surface");
                Set("_parameterDocument", MapParameterizationDocument.Empty("map"));
                Call("EnterParameterReview", "Data grids");
                Assert.True(viewer.IsAncestorOf(pipeline));
                Assert.Equal("Data grids", Get<string>("_parameterReviewMode"));
                Call("ExitParameterReview"); Call("RefreshAll"); Call("SelectSurfaceById", "surface");
                Assert.True(viewer.IsAncestorOf(pipeline));
                Set("_sideBySideLayout", false); Call("ApplyCenterLayout"); Pump(30);
                Assert.True(viewer.IsAncestorOf(pipeline));
                if (Environment.GetEnvironmentVariable("UIATLAS_PIPELINE_QA_DIR") is { Length: > 0 } qa)
                {
                    Directory.CreateDirectory(qa); viewer.UpdateLayout();
                    var image = new RenderTargetBitmap((int)viewer.ActualWidth, (int)viewer.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(viewer);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(qa, "pipeline-only.png")); png.Save(file);
                }
                var graph = Graph();
                Set("_model", new UiMappingReadModel(graph with { Nodes = graph.Nodes.Append(new GraphNode(
                    "hidden-frame", GraphNodeKind.State, "raw", "hidden-frame", "Hidden observation",
                    [new("layer", "raw-world"), new("sceneVisible", "False")], graph.Nodes.Single(n => n.Id == "v2").Evidence)).ToArray() }));
                Call("RefreshAll"); Call("SelectSurfaceById", "surface");
                Call("BuildHierarchy", "", "details");
                var hiddenVariant = (TreeViewItem)((TreeViewItem)Get<Dictionary<string, TreeViewItem>>("_hierarchyItems")["details"].Parent).Parent;
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
                var streamSurface = graph.Nodes.Single(n => n.Id == "raw") with { Id = "streams", Properties = [new("layer", "raw-data-streams")] };
                Set("_model", new UiMappingReadModel(graph with { Nodes = graph.Nodes.Append(streamSurface).ToArray() }));
                Get<CheckBox>("_showExcludedRaw").IsChecked = true; Get<CheckBox>("_showSupplementaryRaw").IsChecked = true;
                Call("RefreshAll"); Call("SelectSurfaceById", "streams"); Pump(20);
                Assert.True(variants.IsVisible); Assert.NotEmpty(variants.Items);
            }
            finally { viewer.Close(); }
            void Set(string name, object value) => typeof(ExplorerWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, value);
            T Get<T>(string name) => (T)typeof(ExplorerWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
            void Call(string name, params object[] args) => typeof(ExplorerWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewer, args);
        });
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
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
             Control("grid2", "pane", "Other grid", "DataGrid", "surface", "semantic-world", "v1"),
             Control("details", "v2", "Details", "Pane", "surface", "semantic-world", "v2") with { Evidence = Frames(3) },
             Control("raw-grid", "raw", "Arrival Guest List", "DataGrid", "raw", "raw-world", "")], []);
    }
}
