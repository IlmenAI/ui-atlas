using Google.Protobuf;
using UIMaps.Map.Core;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V2_1;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Tests;

public sealed class GridSchemaHistoryTests
{
    [Fact]
    public void SchemaHistoryUiOffersDefaultSelectionAndAutomaticMode()
    {
        MapperOverlayWindowTests.Sta(() =>
        {
            var (draft, grid) = Baseline("", ""); Observe(draft, grid, "corrected", ["Reference", "Name"]);
            foreach (var automatic in new[] { false, true })
            {
                string? selected = null;
                var window = new UiAtlas.Core.Desktop.GridSchemaHistoryWindow(draft, grid, value => { selected = value; return Task.CompletedTask; });
                window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var root = (System.Windows.Controls.DockPanel)window.Content;
                    var layout = root.Children.OfType<System.Windows.Controls.Grid>().Single();
                    var list = layout.Children.OfType<System.Windows.Controls.ListBox>().Single();
                    Assert.Equal(2, list.Items.Count);
                    Assert.Contains("Reference", layout.Children.OfType<System.Windows.Controls.TextBox>().Single().Text);
                    list.SelectedIndex = 0;
                    var buttons = root.Children.OfType<System.Windows.Controls.StackPanel>().Single().Children
                        .OfType<System.Windows.Controls.WrapPanel>().Single().Children.OfType<System.Windows.Controls.Button>().ToArray();
                    buttons[automatic ? 0 : 1].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }));
                Assert.True(window.ShowDialog());
                Assert.Equal(automatic ? "automatic" : grid.HostedDataGrid.SchemaVersions[0].SchemaVersionRef, selected);
            }
        });
    }

    [Fact]
    public void BulkAcceptanceCannotDiscardAnUnresolvedLegacySchemaConflict()
    {
        using var temp = new TempDirectory(); var (draft, grid) = Baseline("", "");
        draft.ReviewItems.Add(new ReviewItem { SubjectRef = grid.MapEntityRef, Category = "data-grid-schema-conflict",
            Blocking = true, ReviewState = ReviewState.ReviewUnreviewed });
        new UiMapStore(temp.Path).SaveSessionAndDraft(new MappingSessionDescriptor { MappingSessionRef = "session", MapDraftRef = "draft" }, draft);
        Assert.Throws<UiMapException>(() => new UiMapService(temp.Path).ApplyReviewCommand(new ReviewCommand
        { RequestId = "accept", MapDraftRef = "draft", ExpectedEtag = draft.Etag, ReviewedBy = "test", CommandKind = ReviewCommandKind.ReviewAcceptAllBlocking }));
        Assert.Equal(ReviewState.ReviewUnreviewed, new UiMapService(temp.Path).GetDraft("draft").ReviewItems[0].ReviewState);
    }
    [Fact]
    public void GridWithoutAnyColumnsAdoptsFirstReviewedSchema()
    {
        var (draft, grid) = Baseline();
        Observe(draft, grid, "corrected", ["Reference", "Name"]);
        Assert.Equal(new[] { "Reference", "Name" }, grid.HostedDataGrid.ExpectedHeaders);
        Assert.Equal(2, grid.HostedDataGrid.SchemaVersions.Count);
    }
    [Fact]
    public void CorrectedHeadersCreateVersionAndPreserveGridAndColumnIdentities()
    {
        var (draft, grid) = Baseline("", "", "");
        var before = draft.ToByteArray();
        Observe(draft, grid, "corrected", ["Reference", "Code", "Code"]);
        Assert.Equal(2, grid.HostedDataGrid.SchemaVersions.Count);
        Assert.Equal(new[] { "Reference", "Code", "Code" }, grid.HostedDataGrid.ExpectedHeaders);
        Assert.Equal(new[] { "column-0", "column-1", "column-2" }, ActiveColumns(draft));
        Assert.Equal(new[] { "", "", "" }, grid.HostedDataGrid.SchemaVersions[0].Snapshot.ExpectedHeaders);
        Assert.Equal(new[] { "", "", "" }, UiMapDraft.Parser.ParseFrom(before).Entities[1].HostedDataGrid.ExpectedHeaders);
        var loaded = UiMapDraft.Parser.ParseFrom(draft.ToByteArray());
        Assert.Equal(draft, loaded);
        Assert.All(grid.HostedDataGrid.SchemaVersions, v => Assert.Empty(v.Snapshot.SchemaVersions));
    }

    [Fact]
    public void DuplicateCaptureAddsEvidenceWithoutExtraVersion()
    {
        var (draft, grid) = Baseline("Reference", "Name");
        Observe(draft, grid, "again", ["Reference", "Name"]);
        Observe(draft, grid, "again", ["Reference", "Name"]);
        var version = Assert.Single(grid.HostedDataGrid.SchemaVersions);
        Assert.Contains("atlas:grid:again", version.SourceGridRefs);
        Assert.Equal(2, version.SourceObservationSetRefs.Count);
    }

    [Theory]
    [InlineData("poor")]
    [InlineData("conflicting")]
    [InlineData("geometry")]
    [InlineData("partial")]
    public void LaterCaptureCannotSilentlyReplaceBetterOrIncompatibleSchema(string kind)
    {
        var (draft, grid) = Baseline("Reference", "");
        var labels = kind switch { "poor" => new[] { "", "" }, "conflicting" => new[] { "Other", "Name" }, _ => new[] { "Reference", "Name" } };
        var incoming = Incoming("later", labels);
        if (kind == "geometry") { incoming.Columns[0].ExpectedWidthRatio = .2; incoming.Columns[1].ExpectedWidthRatio = .8; }
        if (kind == "partial") incoming.SchemaPartial = true;
        GridSchemaHistory.Observe(draft, grid, incoming, "revision-later", Source("later"), new());
        Assert.Equal(2, grid.HostedDataGrid.SchemaVersions.Count);
        Assert.Equal(new[] { "Reference", "" }, grid.HostedDataGrid.ExpectedHeaders);
    }

    [Fact]
    public void PinSurvivesNewEvidenceAndAutomaticSelectsRecoveredNamesAgain()
    {
        var (draft, grid) = Baseline("", "");
        var original = grid.HostedDataGrid.DefaultSchemaVersionRef;
        GridSchemaHistory.Select(draft, grid, original);
        Observe(draft, grid, "corrected", ["Reference", "Name"]);
        Assert.True(grid.HostedDataGrid.SchemaDefaultPinned);
        Assert.Equal(original, grid.HostedDataGrid.DefaultSchemaVersionRef);
        GridSchemaHistory.Select(draft, grid, "automatic");
        Assert.False(grid.HostedDataGrid.SchemaDefaultPinned);
        Assert.Equal(new[] { "Reference", "Name" }, grid.HostedDataGrid.ExpectedHeaders);
        GridSchemaHistory.Select(draft, grid, original);
        Assert.Equal(new[] { "", "" }, grid.HostedDataGrid.ExpectedHeaders);
        Assert.Equal(new[] { "", "" }, draft.Entities.Where(e => e.EntityKind == MapEntityKind.MapGridColumn && e.Applicable).Select(e => e.DisplayName));
    }

    [Fact]
    public void SelectingDifferentTopologyRetainsOldEntitiesAndSwitchesActiveColumns()
    {
        var (draft, grid) = Baseline("Reference", "Name");
        Observe(draft, grid, "wide", ["Reference", "Name", "Status"]);
        GridSchemaHistory.Select(draft, grid, grid.HostedDataGrid.SchemaVersions[1].SchemaVersionRef);
        Assert.Equal(3, ActiveColumns(draft).Length);
        Assert.Equal(5, draft.Entities.Count(e => e.EntityKind == MapEntityKind.MapGridColumn));
        GridSchemaHistory.Select(draft, grid, "automatic");
        Assert.Equal(new[] { "column-0", "column-1" }, ActiveColumns(draft));
    }

    [Fact]
    public void SelectionIsPersistedUndoableAndEtagChecked()
    {
        using var temp = new TempDirectory();
        var (draft, grid) = Baseline("", ""); Observe(draft, grid, "corrected", ["Reference", "Name"]);
        var store = new UiMapStore(temp.Path);
        store.SaveSessionAndDraft(new MappingSessionDescriptor { MappingSessionRef = "session", MapDraftRef = "draft" }, draft);
        var service = new UiMapService(temp.Path);
        var command = new ReviewCommand { RequestId = "pin", MapDraftRef = "draft", ExpectedEtag = draft.Etag,
            CommandKind = ReviewCommandKind.ReviewSelectGridSchema, SubjectRef = "grid", Value = grid.HostedDataGrid.SchemaVersions[0].SchemaVersionRef, ReviewedBy = "test" };
        var pinned = service.ApplyReviewCommand(command).Draft;
        Assert.Throws<UiMapException>(() => service.ApplyReviewCommand(command));
        Assert.True(new UiMapService(temp.Path).GetDraft("draft").Entities[1].HostedDataGrid.SchemaDefaultPinned);
        var undone = service.ApplyReviewCommand(withCommand(ReviewCommandKind.ReviewUndo, pinned.Etag)).Draft;
        Assert.False(undone.Entities[1].HostedDataGrid.SchemaDefaultPinned);
        Assert.Equal(new[] { "Reference", "Name" }, undone.Entities[1].HostedDataGrid.ExpectedHeaders);
        var redone = service.ApplyReviewCommand(withCommand(ReviewCommandKind.ReviewRedo, undone.Etag)).Draft;
        Assert.True(redone.Entities[1].HostedDataGrid.SchemaDefaultPinned);
        ReviewCommand withCommand(ReviewCommandKind kind, string etag) => new() { RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = "draft", ExpectedEtag = etag, CommandKind = kind, ReviewedBy = "test" };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedObservationReconciliationHandlesMissingAndUnnamedGrid(bool existing)
    {
        using var temp = new TempDirectory();
        var (draft, grid) = Baseline("", "");
        if (!existing) { draft.Entities.Remove(grid); foreach (var c in draft.Entities.Where(e => e.EntityKind == MapEntityKind.MapGridColumn).ToArray()) draft.Entities.Remove(c); }
        draft.Entities[0].EvidenceLinks.Add(new MapEvidenceLink { SourceObservationSetRef = "source:corrected", SourceObjectRef = "host-object" });
        draft.Sources.Add(Source("corrected"));
        var store = new UiMapStore(temp.Path); store.SaveSessionAndDraft(new MappingSessionDescriptor { MappingSessionRef = "session", MapDraftRef = "draft" }, draft);
        var service = new UiMapService(temp.Path);
        var incoming = Incoming("corrected", ["Reference", "Name"]); incoming.SourceEvidenceRefs.Add("host-object");
        service.ReconcileReviewedGridSchemas("draft", "source:corrected", new DataGridMapPackage { MapRevisionRef = "revision", DataGrids = { incoming } },
            [new WorldObject { WorldObjectId = "host-object", ObjectKind = WorldObjectKind.AccessibilityElement }]);
        var result = Assert.Single(service.GetDraft("draft").Entities, e => e.EntityKind == MapEntityKind.MapDataGrid);
        Assert.Equal(new[] { "Reference", "Name" }, result.HostedDataGrid.ExpectedHeaders);
        Assert.Equal(existing ? 2 : 1, result.HostedDataGrid.SchemaVersions.Count);
        Assert.Single(service.GetDraft("draft").Sources);
    }

    private static (UiMapDraft Draft, MapEntity Grid) Baseline(params string[] headers)
    {
        var draft = new UiMapDraft { MapDraftRef = "draft", MappingSessionRef = "session", Etag = "initial", ApplicationRef = "app" };
        draft.Entities.Add(new MapEntity { MapEntityRef = "host", EntityKind = MapEntityKind.MapControl, Applicable = true });
        var grid = new MapEntity { MapEntityRef = "grid", ParentEntityRef = "host", ApplicationRef = "app", EntityKind = MapEntityKind.MapDataGrid,
            Applicable = true, HostedDataGrid = new HostedDataGridDescriptor { HostControlRef = "host", SourceGridRef = "atlas:grid:original", GridMapRevisionRef = "revision-original",
                SchemaStatus = GridSchemaStatus.GridSchemaReviewed, ExpectedHeaders = { headers } } };
        draft.Entities.Add(grid);
        for (var i = 0; i < headers.Length; i++) draft.Entities.Add(new MapEntity { MapEntityRef = "column-" + i, ParentEntityRef = "grid",
            EntityKind = MapEntityKind.MapGridColumn, DisplayName = headers[i], Applicable = true, ReviewedOrder = i });
        Observe(draft, grid, "original", headers);
        return (draft, grid);
    }
    private static SourceObservationSet Source(string id) => new() { SourceObservationSetRef = "source:" + id, CapturedAt = "2026-11-12T17:12:00Z" };
    private static void Observe(UiMapDraft draft, MapEntity grid, string id, string[] headers) =>
        GridSchemaHistory.Observe(draft, grid, Incoming(id, headers), "revision-" + id, Source(id), new());
    private static MappedDataGrid Incoming(string id, string[] headers)
    {
        var grid = new MappedDataGrid { DataGridRef = "atlas:grid:" + id, ExpectedHeaderSet = { headers }, DisplayName = "Arrival Guest List" };
        for (var i = 0; i < headers.Length; i++) grid.Columns.Add(new MappedGridColumn { GridColumnRef = id + i, Ordinal = i,
            CanonicalDisplayName = headers[i], ExpectedWidthRatio = 1d / headers.Length, HeaderMatchers = { headers[i] } });
        return grid;
    }
    private static string[] ActiveColumns(UiMapDraft draft) => draft.Entities.Where(e => e.EntityKind == MapEntityKind.MapGridColumn && e.Applicable)
        .OrderBy(e => e.ReviewedOrder).Select(e => e.MapEntityRef).ToArray();
}
