using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Build;
using UiAtlas.Core.Desktop;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Storage;
using static UiAtlas.Core.Tests.MapperOverlayWindowTests;

namespace UiAtlas.Core.Tests;

[Collection("Mapper desktop")]
public sealed class ParameterizationTests
{
    [Fact]
    public void DiscoverySeparatesDocumentationVariationAndGridContents()
    {
        var model = new UiMappingReadModel(Graph());
        var proposals = ParameterizationAnalysis.Build(model);
        Assert.Contains(proposals, p => p.TargetId == "sheet" && p.ParameterName == "sheet_name" &&
            p.Evidence.Any(e => e.Kind == "Product documentation"));
        Assert.Contains(proposals, p => p.TargetId == "caption" && p.ParameterName == "workbook_name");
        Assert.Contains(proposals, p => p.TargetId == "font" && p.PropertyName == "Value");
        Assert.Contains(proposals, p => p.TargetId == "grid" && p.IsGrid && p.Schema is null);
        Assert.Contains(proposals, p => p.TargetId == "changing" && p.Evidence.Any(e => e.Kind == "Observed variation"));
        Assert.DoesNotContain(proposals, p => p.TargetId is "cell" or "command" or "unchanged" or
            "document" or "surface" or "title" or "sheet-pane" or "sheet-options" or "sheet-view" or "font-edit");
        Assert.Equal(ParameterReviewState.Suggested, ParameterizationAnalysis.State(proposals[0], null));
    }

    [Fact]
    public void HierarchyPreservesParentsAndSearchAncestorsWithoutCyclesOrForeignParents()
    {
        var controls = new UiMappingReadModel(Graph()).LayerFor(UiUnderstandingLevel.SemanticWorld).Controls;
        var parents = UiControlHierarchy.Parents(controls, "A1");
        Assert.Equal("grid", parents["cell"]);
        Assert.Equal("document", parents["grid"]);
        Assert.Equal("", parents["document"]);
        Assert.Equal(3, parents.Count);
        var cell = controls.Single(c => c.Id == "cell");
        var malformed = UiControlHierarchy.Parents([cell with { Id = "a", ParentControlId = "b" },
            cell with { Id = "b", ParentControlId = "a" }, cell with { Id = "orphan", ParentControlId = "other-surface" }]);
        Assert.Equal("", malformed["orphan"]);
        Assert.True(malformed["a"] == "" || malformed["b"] == "");
    }

    [Fact]
    public void ParameterGeometryRequiresVisibleEvidenceInTheExactObservation()
    {
        var control = new UiMappingReadModel(Graph()).LayerFor(UiUnderstandingLevel.SemanticWorld).Controls.Single(c => c.Id == "sheet");
        var first = new UiMapVariantView("one", "one", "source", 1, 1, control.Evidence[0] with { ScreenshotEntry = "one.png" }, [control.Id]);
        var absent = first with { Id = "absent", FrameSequence = 2 };
        Assert.Empty(ParameterReviewPresentation.Regions(control, absent));
        Assert.Empty(ParameterReviewPresentation.Regions(control, first with { ControlSourceFrames = new Dictionary<string, long> { [control.Id] = 2 } }));
        Assert.Equal(first, ParameterReviewPresentation.Variant(control, [absent, first]));
        Assert.Equal(control.Bounds, Assert.Single(ParameterReviewPresentation.Regions(control, first)));
        Assert.Empty(ParameterReviewPresentation.Regions(control with { Source = control.Source with {
            Properties = control.Source.Properties.Append(new("sceneOffscreen", "True")).ToArray() } }, first));
        Assert.Empty(ParameterReviewPresentation.Regions(control with { ObservationSources = [] }, first));
    }

    [Fact]
    public void DialogDuplicateNeedsSameProviderProcessAndObservationBeforeWithholdingParentGeometry()
    {
        var control = new UiMappingReadModel(Graph()).LayerFor(UiUnderstandingLevel.SemanticWorld).Controls.Single(c => c.Id == "sheet");
        GraphNode Source(string scope) => control.Source with { Properties = [
            new("runtimeIdentityEvidence", "same-runtime"), new("providerId", "uia"), new("windowScopeRef", scope)] };
        var parent = control with { ObservationSources = [Source("pid:1:started:now|hwnd:10|root:10|class:Main")] };
        var child = control with { Id = "dialog-control", OwnerSurfaceId = "dialog",
            ObservationSources = [Source("pid:1:started:now|hwnd:20|root:10|class:Dialog")] };
        var variant = new UiMapVariantView("one", "one", "source", 1, 1, control.Evidence[0], [control.Id]);
        Assert.True(ParameterReviewPresentation.HasMoreSpecificOwner(parent, variant, [parent, child]));
        Assert.False(ParameterReviewPresentation.HasMoreSpecificOwner(child, variant, [parent, child]));
        Assert.False(ParameterReviewPresentation.HasMoreSpecificOwner(parent, variant with { BundleId = "unrelated" }, [parent, child]));
        Assert.False(ParameterReviewPresentation.HasMoreSpecificOwner(parent, variant, [parent,
            child with { ObservationSources = [Source("pid:2:started:now|hwnd:20|root:10|class:Dialog")] }]));
        Assert.False(ParameterReviewPresentation.HasMoreSpecificOwner(parent, variant, [parent,
            child with { ObservationSources = [Source("pid:1:started:now|hwnd:30|root:30|class:Other")] }]));
    }

    [Fact]
    public void ReviewedGridPreservesPhysicalColumnsAndIndependentCoverage()
    {
        var proposals = ParameterizationAnalysis.Build(new(Graph()), [GridSource()]);
        var grid = Assert.Single(proposals, p => p.IsGrid);
        Assert.Equal(new[] { "one", "two", "three" }, grid.Schema!.Columns.Select(c => c.ColumnKey));
        Assert.Equal(new[] { "Name", "Name", "" }, grid.Schema.Columns.Select(c => c.Label));
        Assert.False(grid.Schema.IsComplete);
        Assert.Contains("Columns: partial", grid.Coverage);
        Assert.Contains("complete extent", grid.Coverage);
        Assert.False(grid.Schema.UniformBlankCellsQualified);
        Assert.Null(grid.Schema.RecordIdColumnKey);
        Assert.DoesNotContain(proposals, p => p.TargetId == "cell");
    }

    [Theory]
    [InlineData(ParameterReviewState.Accepted)]
    [InlineData(ParameterReviewState.Denied)]
    [InlineData(ParameterReviewState.NeedsEvidence)]
    public void DecisionsPersistAndStaleWritersCannotOverwriteThem(ParameterReviewState state)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "map.db");
        var p = ParameterizationAnalysis.Build(new(Graph())).First(p => !p.IsGrid);
        var d = Decision(p, state) with { ParameterName = "reviewed_name",
            Documentation = [new("Product reference", "https://example.com/docs", "Names can change.", "Version 1")] };
        var saved = MapParameterizationStore.Save(path, Document(p, d), 0);
        var read = MapParameterizationStore.Load(path, "map");
        Assert.Equal(1, read.Revision);
        Assert.Equal("reviewed_name", Assert.Single(read.Decisions).ParameterName);
        Assert.Equal(state, ParameterizationAnalysis.State(p, read.Decisions[0]));
        Assert.Equal("Product reference", read.Decisions[0].Documentation[0].Title);
        Assert.Throws<IOException>(() => MapParameterizationStore.Save(path, saved, 0));
        Assert.Equal(ParameterReviewState.NeedsEvidence, ParameterizationAnalysis.State(p with { Fingerprint = "changed" }, d));
        Assert.Throws<InvalidDataException>(() => MapParameterizationStore.Load(path, "other-map"));
    }

    [Theory]
    [InlineData("file:///c:/test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://name:secret@example.com")]
    public void DocumentationCannotBecomeAnExecutableOrCredentialLink(string url)
    {
        Assert.False(MapParameterizationStore.IsDocumentationUrl(url));
        var p = ParameterizationAnalysis.Build(new(Graph())).First(p => !p.IsGrid);
        var document = Document(p, Decision(p) with { Documentation = [new("Reference", url, "", "")] });
        Assert.Throws<InvalidDataException>(() => MapParameterizationStore.Validate(document, "map"));
    }

    [Fact]
    public void SchemaAcceptanceRequiresPhysicalSchemaAndInvalidatesOnSchemaChange()
    {
        var source = GridSource();
        var p = ParameterizationAnalysis.Build(new(Graph()), [source]).Single(p => p.IsGrid);
        var decision = Decision(p) with { GridRule = new(GridVariationMode.AccordingToSchema) };
        MapParameterizationStore.Validate(Document(p, decision), "map");
        Assert.Throws<InvalidDataException>(() => MapParameterizationStore.Validate(Document(p with { Schema = null }, decision), "map"));
        var revised = source with { Review = source.Review with { Review = source.Review.Review with {
            Schema = source.Review.Review.Schema with { Revision = "changed" } } } };
        var next = ParameterizationAnalysis.Build(new(Graph()), [revised]).Single(p => p.IsGrid);
        Assert.Equal(p.Id, next.Id);
        Assert.Equal(ParameterReviewState.NeedsEvidence, ParameterizationAnalysis.State(next, decision));
        var corrupt = p with { Schema = p.Schema! with { Columns = [p.Schema.Columns[0], p.Schema.Columns[0]] } };
        Assert.Throws<InvalidDataException>(() => MapParameterizationStore.Validate(Document(corrupt, decision), "map"));
    }

    [Fact]
    public void ManualCandidatesRefreshEvidenceWithoutLosingTheirIdentity()
    {
        var graph = Graph(); var node = graph.Nodes.Single(n => n.Id == "unchanged");
        var p = ParameterizationAnalysis.Create(node, "surface", new(0, 0, 50, 20), "Name", "manual_name",
            "Text", "This control", false, [new("Mapper proposal", "Review this name.", "Selected element")], true);
        var original = ParameterizationAnalysis.Build(new(graph), manualProposals: [p]).Single(v => v.Id == p.Id);
        Assert.Equal(p.Fingerprint, original.Fingerprint);
        var changed = graph with { Nodes = graph.Nodes.Select(n => n.Id == node.Id ? n with { Label = "changed" } : n).ToArray() };
        var revised = ParameterizationAnalysis.Build(new(changed), manualProposals: [p]).Single(v => v.Id == p.Id);
        Assert.Equal(ParameterReviewState.NeedsEvidence, ParameterizationAnalysis.State(revised, Decision(original)));
    }

    [Fact]
    public void SchemaSubregionsRetainParentAndRebindToCurrentSourceEvidence()
    {
        var graph = Graph();
        var inner = graph.Nodes.Single(n => n.Id == "grid") with { Id = "inner", ParentId = "grid", StableKey = "inner",
            Label = "Inner table", Evidence = [new("source", 1, "frame.json", new(60, 180, 300, 150))] };
        graph = graph with { Nodes = graph.Nodes.Append(inner).ToArray() };
        var source = GridSource() with { TargetId = "inner" };
        var detected = ParameterizationAnalysis.Build(new(graph), [source]);
        var parent = detected.Single(p => p.TargetId == "grid");
        var table = detected.Single(p => p.Id == "grid:reviewed");
        var subregion = table with { Id = "subregion:example", ParentProposalId = parent.Id, IsManual = true,
            Fingerprint = ParameterizationAnalysis.Fingerprint(parent.Fingerprint, table.Fingerprint),
            Evidence = table.Evidence.Append(new ParameterEvidence("Mapper association", "Explicit subregion", table.Id)).ToArray() };
        var decision = Decision(subregion) with { GridRule = new(GridVariationMode.AccordingToSchema) };
        MapParameterizationStore.Validate(new(MapParameterizationDocument.CurrentFormat, "map", 0,
            [parent, table, subregion], [decision]), "map");
        var movedImage = source with { ImagePath = "new-retained-image.png" };
        var reopened = ParameterizationAnalysis.Build(new(graph), [movedImage], [subregion]).Single(p => p.Id == subregion.Id);
        Assert.Equal("new-retained-image.png", reopened.ImagePath);
        Assert.Equal(subregion.Fingerprint, reopened.Fingerprint);
        var changedSchema = source with { Review = source.Review with { Review = source.Review.Review with {
            Schema = source.Review.Review.Schema with { Revision = "r2" } } } };
        var changed = ParameterizationAnalysis.Build(new(graph), [changedSchema], [subregion]).Single(p => p.Id == subregion.Id);
        Assert.Equal(ParameterReviewState.NeedsEvidence, ParameterizationAnalysis.State(changed, decision));
        Assert.Throws<InvalidDataException>(() => MapParameterizationStore.Validate(new(
            MapParameterizationDocument.CurrentFormat, "map", 0,
            [parent, table, subregion with { Bounds = new(0, 0, 900, 900) }], [decision]), "map"));
    }

    [Fact]
    public async Task WorkspaceRebuildCopyAndPackageRoundTripRetainDecisionsAndRawEvidence()
    {
        using var temp = new TempDirectory();
        var bundle = CompleteScreenshots(temp.Path);
        var originalHash = SHA256.HashData(File.ReadAllBytes(bundle));
        var map = Path.Combine(temp.Path, "map.db");
        var proposal = ParameterizationAnalysis.Build(new(Graph())).First(p => !p.IsGrid);
        var legacyGraph = new RecordingGraphBuilder().Build(bundle);
        SqliteGraphStore.Save(legacyGraph, map);
        var document = Document(proposal, Decision(proposal)) with { LogicalMapId = legacyGraph.Metadata.EffectiveLogicalMapId };
        MapParameterizationStore.Save(map, document, 0);
        var graph = await AtlasMapWorkspace.BuildAsync([bundle], map);
        var logical = graph.Metadata.EffectiveLogicalMapId;
        Assert.Equal(1, MapParameterizationStore.Load(map, logical).Revision);
        var oldGenerationPath = MapParameterizationStore.PathFor(map);
        await AtlasMapWorkspace.BuildAsync([bundle], map);
        Assert.Throws<IOException>(() => MapParameterizationStore.Save(map, document, 1, oldGenerationPath));
        Assert.Equal(ParameterReviewState.Accepted, Assert.Single(MapParameterizationStore.Load(map, logical).Decisions).State);
        var copy = Path.Combine(temp.Path, "copy.db");
        MapBackendCompanion.Copy(map, copy, "copy-id");
        Assert.Equal("copy-id", MapParameterizationStore.Load(copy, "copy-id").LogicalMapId);
        var archive = Path.Combine(temp.Path, "review.atlasmap");
        AtlasMapPackage.Export(map, archive, true);
        var imported = AtlasMapPackage.Import(archive, Path.Combine(temp.Path, "import"), true);
        Assert.Equal(JsonSerializer.Serialize(document.Decisions[0]),
            JsonSerializer.Serialize(MapParameterizationStore.Load(imported, logical).Decisions[0]));
        Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(bundle)));
    }

    [Fact]
    public void LegacyCatalogCopyArchiveAndDeleteCarryReviewData()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(temp.Path);
        var path = catalog.MapPath("map");
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        SqliteGraphStore.Save(graph with { Metadata = graph.Metadata with { LogicalMapId = "map" } }, path);
        var p = ParameterizationAnalysis.Build(new(Graph())).First(p => !p.IsGrid);
        MapParameterizationStore.Save(path, Document(p, Decision(p)), 0);
        var copy = catalog.DuplicateMap("map");
        Assert.Single(MapParameterizationStore.Load(copy.MapPath, copy.Id).Decisions);
        var archive = catalog.ArchiveMap(copy.Id);
        Assert.Single(MapParameterizationStore.Load(archive, copy.Id).Decisions);
        Assert.True(catalog.DeleteUiAtlasmanently("map"));
        Assert.False(File.Exists(MapParameterizationStore.LegacyPath(path)));
    }

    [Fact]
    public void ActualEditorSupportsBothModesSelectionAndAllThreeDecisions()
    {
        Sta(() =>
        {
            using var temp = new TempDirectory();
            var path = Path.Combine(temp.Path, "map.db");
            var graph = Graph();
            var source = GridSource();
            var imagePath = WriteGridImage(temp.Path);
            source = source with { ImagePath = imagePath, Review = source.Review with { Review = source.Review.Review with
                { ImagePath = imagePath, ImageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath))) } } };
            var proposals = ParameterizationAnalysis.Build(new(graph), [source]);
            var viewer = new ExplorerWindow(null) { Width = 1600, Height = 980, Left = 100, Top = 70 };
            try
            {
                Set("_model", new UiMappingReadModel(graph)); Set("_graphPath", path);
                Set("_parameterDocument", MapParameterizationDocument.Empty("map")); Set("_parameterProposals", proposals);
                viewer.Show(); Pump(50);
                Invoke("EnterParameterReview", "Data grids"); Pump(30);
                var grid = Assert.Single(proposals, p => p.IsGrid);
                Assert.Equal(grid.Id, Get<ParameterProposal>("_selectedParameter").Id);
                var mode = Descendants<ComboBox>(Get<StackPanel>("_properties")).Single();
                Assert.Equal(0, mode.SelectedIndex);
                mode.SelectedIndex = 1; Pump(20);
                Assert.Equal(2, Get<Canvas>("_appMapOverlay").Children.OfType<Rectangle>().Count());
                Assert.IsType<BitmapImage>(Get<Image>("_appMapImage").Source);
                Click("Accept"); Await(() => !Get<bool>("_savingParameters"));
                Assert.Equal(GridVariationMode.AccordingToSchema, MapParameterizationStore.Load(path, "map").Decisions[0].GridRule!.Mode);
                Invoke("EnterParameterReview", "Parameters"); Pump(20);
                foreach (var state in new[] { ("Accept", ParameterReviewState.Accepted), ("Deny", ParameterReviewState.Denied), ("Need evidence", ParameterReviewState.NeedsEvidence) })
                {
                    var selected = Get<ParameterProposal>("_selectedParameter");
                    Descendants<TextBox>(Get<StackPanel>("_properties")).First().Text = "edited_name";
                    Click(state.Item1); Await(() => !Get<bool>("_savingParameters"));
                    var saved = MapParameterizationStore.Load(path, "map").Decisions.Single(d => d.ProposalId == selected.Id);
                    Assert.Equal(state.Item2, saved.State); Assert.Equal("edited_name", saved.ParameterName);
                }
                var tree = Get<TreeView>("_hierarchy");
                var hierarchy = Get<Dictionary<string, TreeViewItem>>("_hierarchyItems");
                var second = hierarchy.Values.Last();
                second.IsSelected = true; Pump(20);
                Assert.Equal(((ParameterProposal)second.Tag).Id, Get<ParameterProposal>("_selectedParameter").Id);
                var sheet = proposals.Single(p => p.TargetId == "sheet");
                Invoke("SelectParameterProposal", sheet); Pump(20);
                var sheetItem = hierarchy[sheet.Id];
                Assert.True(sheetItem.IsSelected);
                var nameGroup = Assert.IsType<TreeViewItem>(sheetItem.Parent);
                Assert.Equal(2, nameGroup.Items.Count);
                Assert.True(nameGroup.IsExpanded);
                // The renderer must replace the previous selection, never draw all proposals.
                var preview = new BitmapImage();
                using (var stream = new MemoryStream(File.ReadAllBytes(imagePath)))
                {
                    preview.BeginInit(); preview.CacheOption = BitmapCacheOption.OnLoad;
                    preview.StreamSource = stream; preview.EndInit(); preview.Freeze();
                }
                Get<Image>("_appMapImage").Source = preview;
                Set("_renderedViewportBounds", new RectI(0, 0, 800, 600));
                Get<Canvas>("_appMapOverlay").Children.Clear();
                Invoke("RenderParameterOverlays");
                var box = Assert.Single(Get<Canvas>("_appMapOverlay").Children.OfType<Rectangle>());
                Assert.Equal(sheet.Id, box.Tag);
                Assert.Equal(100, box.Width); Assert.Equal(30, box.Height);
                Assert.Equal(30, Canvas.GetLeft(box)); Assert.Equal(540, Canvas.GetTop(box));
                var qa = Environment.GetEnvironmentVariable("UIATLAS_PARAMETER_QA_DIR");
                if (!string.IsNullOrWhiteSpace(qa))
                {
                    Directory.CreateDirectory(qa);
                    Render((FrameworkElement)viewer.Content, Path.Combine(qa, "parameter-review.png"));
                    Invoke("EnterParameterReview", "Data grids"); Pump(30);
                    Render((FrameworkElement)viewer.Content, Path.Combine(qa, "grid-review.png"));
                    viewer.Width = 1200; Pump(30);
                    Render((FrameworkElement)viewer.Content, Path.Combine(qa, "grid-review-narrow.png"));
                }
                Click("Back to map"); Pump(20);
                Assert.Null(Get<string?>("_parameterReviewMode"));
                Assert.NotEmpty(Get<TreeView>("_hierarchy").Items);
                Invoke("BuildHierarchy", "", "cell");
                hierarchy = Get<Dictionary<string, TreeViewItem>>("_hierarchyItems");
                Assert.Same(hierarchy["grid"], hierarchy["cell"].Parent);
                Assert.Same(hierarchy["document"], hierarchy["grid"].Parent);
                Invoke("SelectControlById", "cell"); Pump(20);
                Assert.True(hierarchy["cell"].IsSelected);
                Assert.True(hierarchy["grid"].IsExpanded);
                Assert.True(hierarchy["document"].IsExpanded);
            }
            finally { viewer.Close(); }
            void Set(string f, object v) => typeof(ExplorerWindow).GetField(f, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, v);
            T Get<T>(string f) => (T)typeof(ExplorerWindow).GetField(f, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
            void Invoke(string method, params object[] args) => typeof(ExplorerWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewer, args);
            void Click(string title) => Descendants<Button>(viewer).Single(b => Equals(b.Content, title)).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        if (Environment.GetEnvironmentVariable("UIATLAS_PARAMETER_RETAINED_MAP") is { Length: > 0 } retained)
            VerifyRetainedParameterSelection(retained);
    }

    private static void VerifyRetainedParameterSelection(string path)
    {
        Sta(() =>
        {
            var viewer = new ExplorerWindow(null) { Width = 1700, Height = 1050 };
            try
            {
                viewer.Show();
                var load = (Task)Call("LoadGraphAsync", path)!;
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (!load.IsCompleted && DateTime.UtcNow < deadline) Pump(50);
                Assert.True(load.IsCompleted); load.GetAwaiter().GetResult();
                var proposals = Get<IReadOnlyList<ParameterProposal>>("_parameterProposals");
                var model = Get<UiMappingReadModel>("_model");
                var dialog = model.LayerFor(UiUnderstandingLevel.SemanticWorld).Surfaces.Single(s => s.DisplayName == "Insert Function");
                Assert.Contains(proposals, p => p.ParameterName == "sheet_name");
                Assert.DoesNotContain(proposals, p => p.DisplayName is "Sheet Options" or "Sheet View");
                Call("EnterParameterReview", "Parameters"); Pump(100);
                var cases = new[] {
                    ("worksheet-tab", proposals.First(p => p.ParameterName == "sheet_name" && p.DisplayName == "Inventory list")),
                    ("workbook-caption", proposals.First(p => p.ParameterName == "workbook_name")),
                    ("font-size", proposals.First(p => p.PropertyName == "Value" && p.DisplayName == "Font Size")),
                    ("dialog-value", proposals.First(p => p.PropertyName == "Value" && p.DisplayName == "Or select a category:" && p.SurfaceId == dialog.Id))
                };
                var qa = Environment.GetEnvironmentVariable("UIATLAS_PARAMETER_QA_DIR");
                var report = new List<object>();
                foreach (var (name, proposal) in cases)
                {
                    // Exercise the actual tree selection handler, including collapsed groups.
                    var item = Get<Dictionary<string, TreeViewItem>>("_hierarchyItems")[proposal.Id];
                    item.IsSelected = true; Pump(150);
                    Assert.Equal(proposal.Id, Get<ParameterProposal>("_selectedParameter").Id);
                    Assert.NotNull(Get<Image>("_appMapImage").Source);
                    var rectangles = Get<Canvas>("_appMapOverlay").Children.OfType<Rectangle>().ToArray();
                    Assert.NotEmpty(rectangles);
                    Assert.All(rectangles, r => Assert.Equal(proposal.Id, r.Tag));
                    var control = Get<UiMapControlView>("_selectedControl");
                    var variant = Get<UiMapVariantView>("_selectedVariant");
                    var viewport = Get<RectI>("_renderedViewportBounds");
                    var expected = ParameterReviewPresentation.Regions(control, variant, model.LayerFor(UiUnderstandingLevel.SemanticWorld).Controls,
                            model, Get<UiEvidenceReader>("_evidence").ReadObservation(variant.BundleId, variant.FrameSequence))
                        .Select(r => UiMapPresentation.ProjectToSurface(r, viewport)).OfType<RectI>().ToArray();
                    Assert.Equal(expected.Length, rectangles.Length);
                    for (var i = 0; i < expected.Length; i++)
                    {
                        Assert.Equal(expected[i].Width, rectangles[i].Width);
                        Assert.Equal(expected[i].Height, rectangles[i].Height);
                        Assert.Equal(expected[i].X, Canvas.GetLeft(rectangles[i]));
                        Assert.Equal(expected[i].Y, Canvas.GetTop(rectangles[i]));
                    }
                    report.Add(new { name, proposal.TargetId, proposal.DisplayName, control.CanonicalKind,
                        variant.BundleId, variant.FrameSequence, Bounds = expected });
                    if (!string.IsNullOrWhiteSpace(qa))
                    {
                        Directory.CreateDirectory(qa);
                        Render((FrameworkElement)viewer.Content, Path.Combine(qa, "retained-" + name + ".png"));
                    }
                }
                var duplicate = proposals.First(p => p.PropertyName == "Value" && p.DisplayName == "Or select a category:" && p.SurfaceId != dialog.Id);
                Call("SelectParameterProposal", duplicate); Pump(50);
                Assert.Empty(Get<Canvas>("_appMapOverlay").Children.OfType<Rectangle>());
                Assert.Contains("child window", Get<TextBlock>("_appMapSummary").Text);
                Call("ExitParameterReview"); Call("RefreshAll");
                var cell = model.LayerFor(UiUnderstandingLevel.SemanticWorld).Controls.First(c => c.DisplayName == "A1");
                Call("SelectControlById", cell.Id); Pump(100);
                var hierarchy = Get<Dictionary<string, TreeViewItem>>("_hierarchyItems");
                Assert.Same(hierarchy[cell.ParentControlId], hierarchy[cell.Id].Parent);
                if (!string.IsNullOrWhiteSpace(qa))
                {
                    Render((FrameworkElement)viewer.Content, Path.Combine(qa, "retained-control-hierarchy.png"));
                    File.WriteAllText(Path.Combine(qa, "retained-selection.json"),
                        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            finally { viewer.Close(); }
            T Get<T>(string name) => (T)typeof(ExplorerWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
            object? Call(string method, params object[] args) => typeof(ExplorerWindow).GetMethod(method,
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewer, args);
        });
    }

    private static ParameterDecision Decision(ParameterProposal p, ParameterReviewState state = ParameterReviewState.Accepted) =>
        new(p.Id, p.Fingerprint, state, p.ParameterName, p.ValueType, p.Scope, p.IsGrid ? new() : null, [], "test-reviewer", DateTimeOffset.UnixEpoch);
    private static MapParameterizationDocument Document(ParameterProposal p, ParameterDecision d) =>
        new(MapParameterizationDocument.CurrentFormat, "map", 0, [p], [d]);

    private static string CompleteScreenshots(string directory)
    {
        using var source = RecordingBundle.Open(SyntheticBundleFactory.Create(directory, includeScreenshot: true));
        using var writer = new RecordingBundleWriter(Path.Combine(directory, "complete-staging"));
        foreach (var entry in source.Entries.Where(e => e is not ("manifest.json" or "hashes.sha256")))
        {
            if (entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal))
            {
                var frame = source.ReadJson<FrameObservation>(entry);
                writer.WriteJson(entry, frame with { FrameEntry = $"raw/frames/frame-{frame.Sequence:D6}.png" });
                if (frame.Sequence != 1) writer.WriteBytes($"raw/frames/frame-{frame.Sequence:D6}.png",
                    source.ReadBytes("raw/frames/frame-000001.png"));
            }
            else writer.WriteBytes(entry, source.ReadBytes(entry));
        }
        writer.WriteJson("manifest.json", source.ReadJson<RecordingManifest>("manifest.json") with { Files = writer.DescribeEntries() });
        var output = Path.Combine(directory, "complete.mlrec"); writer.Complete(output); return output;
    }

    private static string WriteGridImage(string directory)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 300, 180));
            dc.DrawRectangle(Brushes.AliceBlue, null, new Rect(0, 0, 300, 30));
            for (var x = 0; x <= 300; x += 100)
                dc.DrawLine(new Pen(Brushes.LightGray, 1), new Point(x, 0), new Point(x, 180));
            for (var y = 30; y <= 180; y += 30)
                dc.DrawLine(new Pen(Brushes.LightGray, 1), new Point(0, y), new Point(300, y));
            foreach (var cell in new[] { ("Name", 5, 5), ("Name", 105, 5), ("Example A", 5, 40), ("Example B", 5, 70),
                ("Sample", 105, 40), ("Sample", 105, 70) })
                dc.DrawText(new FormattedText(cell.Item1, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.Black, 1), new Point(cell.Item2, cell.Item3));
        }
        var bitmap = new RenderTargetBitmap(300, 180, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(directory, "fixture-grid.png");
        using var stream = File.Create(path); encoder.Save(stream); return path;
    }

    internal static UiKnowledgeGraph Graph()
    {
        var evidence = new EvidenceRef("source", 1, "frame.json", new(0, 0, 800, 600));
        RectI Bounds(string id) => id.StartsWith("sheet", StringComparison.Ordinal) ? new(30, 540, 100, 30) :
            new(30, id == "grid" ? 140 : 40, 500, id == "grid" ? 300 : 30);
        GraphNode Node(string id, string parent, string label, string role, params string[] values) =>
            new(id, GraphNodeKind.Control, parent, id, label,
                [new("layer", "semantic-world"), new("controlType", role), new("semanticSurfaceId", "surface")],
                [evidence with { Bounds = Bounds(id) }],
                values.Select((v, i) => new GraphControlObservation("source", i + 1,
                    new(id, parent, "", v, role, "", new(30, 40, 500, 30), true, false, "", 1), 1, "recorded", true)).ToArray());
        return new(new("5", "test", "graph", DateTimeOffset.UnixEpoch, "source", "", "test", LogicalMapId: "map"),
            [new("app", GraphNodeKind.Application, "", "app", "EXCEL", [], []),
             new("surface", GraphNodeKind.Surface, "app", "surface", "Book2 - Excel", [new("layer", "semantic-world")], [evidence]),
             Node("sheet", "document", "Sheet1", "Sheet Tab"),
             Node("sheet2", "document", "Sheet1", "Sheet Tab"),
             Node("sheet-pane", "document", "Sheet Sheet1", "Pane"),
             Node("sheet-options", "surface", "Sheet Options", "Button"),
             Node("sheet-view", "surface", "Sheet View", "Group"),
             Node("title", "surface", "Book2 - Excel", "Title Bar"),
             Node("caption", "title", "\u202aBook2\u202c  -  Excel", "Button"),
             Node("font", "surface", "Font Size", "ComboBox"),
             Node("font-edit", "font", "Font Size", "Edit Box"),
             Node("document", "surface", "Book2", "Custom"),
             Node("grid", "document", "Worksheet grid", "DataGrid"),
             Node("cell", "grid", "A1", "Item", "Alice", "Bob"),
             Node("changing", "surface", "Arrival date", "Text", "Monday", "Tuesday"),
             Node("command", "surface", "Save", "Button", "Save", "Save as"),
             Node("unchanged", "surface", "Stable", "Text")], []);
    }

    private static ParameterGridSource GridSource()
    {
        var schema = new GridSchema("r1", false, null, [new("one", 0, "Name", 0, 100),
            new("two", 1, "Name", 100, 200), new("three", 2, "", 200, 300)], 24, 30);
        var context = new DataGridReviewContext(new("Excel", "XLMAIN", "Book2", [], new("Grid")),
            "sheet", new("Grid", "grid", "DataGrid", "Worksheet grid"), new(30, 140, 500, 300));
        var review = new GridSchemaReview("acq", "reviewed", schema, new(0, 0, 300, 30),
            "", "hash", "", GridCaptureStatus.Partial,
            new(false, true, false, true, true, false, true, [], []),
            new(GridRestorationStatus.Succeeded, "", true, true), [], [], DateTimeOffset.UnixEpoch);
        return new("reviewed", "grid", "surface", "Reviewed worksheet", new("reviewed", "Reviewed worksheet",
            context, review, DateTimeOffset.UnixEpoch), "", true);
    }
}
