using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Core.Storage;
using static UiAtlas.Core.Tests.MapperOverlayWindowTests;

namespace UiAtlas.Core.Tests;

[Collection("Mapper desktop")]
public sealed class GridSchemaReviewWindowTests
{
    [Fact]
    public void ColumnFocusUsesRetainedImageCoordinatesDespiteAGlobalMosaicOrigin()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Succeeded, true);
            var original = fixture.Review(fixture.Source.HeaderBounds, null);
            var shifted = original with { Schema = original.Schema with { Columns = original.Schema.Columns
                .Select(c => c with { StartX = c.StartX + 1000, EndX = c.EndX + 1000 }).ToArray() } };
            var review = new GridSchemaReviewWindow(fixture.Source, shifted);
            try
            {
                review.Show(); Pump(30); var grid = Descendants<DataGrid>(review).Single(); grid.SelectedIndex = 2; Pump(20);
                Assert.Equal(400, review.SelectedColumnBounds!.Value.X); Assert.Equal(200, review.SelectedColumnBounds.Value.Width);
                review.SelectColumnAt(new(450, 50)); Pump(20); Assert.Equal(-1, grid.SelectedIndex);
                review.SelectColumnAt(new(250, 50)); Pump(20); Assert.Equal(1, grid.SelectedIndex);
                Assert.Equal(200, review.SelectedColumnBounds!.Value.X); Assert.Same(shifted, review.Draft);
            }
            finally { review.Close(); }
        });
    }
    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(37)]
    public void ColumnSelectionHighlightsItsPhysicalIntervalAndRestoresThePreviousView(int index)
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.SafelySkipped, true, 4800, 600, 38);
            var original = fixture.Review(fixture.Source.HeaderBounds, null);
            var review = new GridSchemaReviewWindow(fixture.Source, original) { Height = 900 };
            if (index == 37 && Environment.GetEnvironmentVariable("UIATLAS_SCHEMA_QA_SECONDS") is not null) review.ShowInTaskbar = true;
            try
            {
                review.Show(); Pump(40);
                var zoom = Descendants<ComboBox>(review).Single(); zoom.SelectedIndex = 1; Pump(20);
                var preview = Descendants<ScrollViewer>(review).Single(v => v.Content is Canvas);
                preview.ScrollToHorizontalOffset(640); preview.ScrollToVerticalOffset(35); Pump(20);
                var oldZoom = review.PreviewZoom; var oldX = preview.HorizontalOffset; var oldY = preview.VerticalOffset;
                var grid = Descendants<DataGrid>(review).Single(); grid.SelectedIndex = index; Pump(30);
                var selected = Assert.IsType<Rect>(review.SelectedColumnBounds);
                var column = original.Schema.Columns[index];
                Assert.Equal(column.StartX, selected.X); Assert.Equal(column.EndX - column.StartX, selected.Width);
                Assert.True(review.PreviewZoom > .25);
                var center = (selected.X + selected.Width / 2) * review.PreviewZoom - preview.HorizontalOffset;
                Assert.InRange(center, 0, preview.ViewportWidth);
                Assert.Same(original, review.Draft); Assert.Null(review.Draft!.ReviewedUtc);
                if (index == 37) Capture(review, "schema-final-column-focus");
                if (index == 37 && int.TryParse(Environment.GetEnvironmentVariable("UIATLAS_SCHEMA_QA_SECONDS"), out var seconds))
                {
                    // Keep the real WPF preview available for an attended input check.
                    Pump(Math.Clamp(seconds, 0, 120) * 1000);
                    Assert.Same(original, review.Draft); Assert.Equal(original.HeaderBounds, review.HeaderSelection);
                }
                Click(Descendants<Button>(review).Single(b => Equals(b.Content, "Clear column focus"))); Pump(20);
                Assert.Null(review.SelectedColumnBounds); Assert.Equal(-1, grid.SelectedIndex);
                Assert.Equal(oldZoom, review.PreviewZoom, 6); Assert.Equal(oldX, preview.HorizontalOffset, 4); Assert.Equal(oldY, preview.VerticalOffset, 4);
            }
            finally { review.Close(); }
        });
    }

    [Fact]
    public void PreviewGesturesAndReverseColumnSelectionPreserveNamesGeometryAndReviewState()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Failed, true, 4800, 600, 38);
            var draft = fixture.Review(fixture.Source.HeaderBounds, null);
            var review = new GridSchemaReviewWindow(fixture.Source, draft) { Height = 900 };
            try
            {
                review.Show(); Pump(30);
                var grid = Descendants<DataGrid>(review).Single();
                grid.SelectedIndex = 0; grid.CurrentCell = new DataGridCellInfo(grid.Items[0], grid.Columns[1]); grid.Focus();
                Assert.True(grid.BeginEdit()); Pump(20); Descendants<TextBox>(grid).First().Text = "Edited name";
                grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true); Pump(20);
                review.ClearColumnFocus(); Pump(20);
                var originalHeader = review.HeaderSelection;
                var preview = Descendants<ScrollViewer>(review).Single(v => v.Content is Canvas);
                Assert.True(preview.IsManipulationEnabled);
                var scale = review.PreviewZoom;
                review.ZoomPreview(2, new(120, 80)); Pump(20); Assert.Equal(scale * 2, review.PreviewZoom, 5);
                // Navigation cancels any in-progress edit drag before touch promotion can continue it.
                review.BeginPreviewInteraction(new(0, 0));
                review.NavigatePreviewGesture(1.25, new Vector(-100, -10), new Point(120, 80));
                review.ContinuePreviewInteraction(new(200, 50)); review.EndPreviewInteraction(); Pump(20);
                Assert.Equal(originalHeader, review.HeaderSelection); Assert.Same(draft, review.Draft);
                Assert.True(grid.IsVisible); Assert.Equal("Edited name", ((GridSchemaReviewWindow.ColumnRow)grid.Items[0]).Label);
                Descendants<CheckBox>(review).Single(c => Equals(c.Content, "Zoom to selected column")).IsChecked = false;
                var manual = review.PreviewZoom;
                var last = draft.Schema.Columns.Last(); review.SelectColumnAt(new(last.StartX + 2, 45)); Pump(20);
                Assert.Equal(37, grid.SelectedIndex); Assert.Equal(manual, review.PreviewZoom);
                review.SelectColumnAt(new(last.StartX + 2, 45)); Pump(20); Assert.Null(review.SelectedColumnBounds);
                var save = review.SaveAsync(); Await(() => save.IsCompleted); save.GetAwaiter().GetResult();
                Assert.Equal("Edited name", review.SavedReview!.Schema.Columns[0].Label);
                Assert.Equal(draft.HeaderBounds, review.SavedReview.HeaderBounds);
                Assert.Equal(draft.Schema.Columns.Select(c => (c.StartX, c.EndX)), review.SavedReview.Schema.Columns.Select(c => (c.StartX, c.EndX)));
                Capture(review, "schema-navigation-toolbar");
            }
            finally { review.Close(); }
        });
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void CompanionCloseIconIsGeometricallyCenteredAtCommonScales(double scale)
    {
        Sta(() =>
        {
            var header = UiAtlas.Presentation.AtlasFluent.Header("Table Explorer", () => { });
            header.LayoutTransform = new ScaleTransform(scale, scale);
            var window = new Window { Content = header, Width = 500, Height = 180 };
            try
            {
                window.Show(); Pump(20);
                var close = Descendants<Button>(window).Single(); close.ApplyTemplate();
                var path = Assert.IsType<System.Windows.Shapes.Path>(close.Template.FindName("CloseIcon", close));
                var ring = Assert.IsType<Border>(close.Template.FindName("Ring", close));
                var center = path.TransformToAncestor(ring).Transform(new(path.ActualWidth / 2, path.ActualHeight / 2));
                Assert.Equal(ring.ActualWidth / 2, center.X, 1); Assert.Equal(ring.ActualHeight / 2, center.Y, 1);
            }
            finally { window.Close(); }
        });
    }
    [Theory]
    [InlineData(GridCaptureStatus.Complete)]
    [InlineData(GridCaptureStatus.Partial)]
    public void AutomaticAnalysisPreparesDraftAndNameWithDetailsBeforeExplicitAcceptance(GridCaptureStatus status)
    {
        Sta(() =>
        {
            using var fixture = new Fixture(status, GridRestorationStatus.Succeeded, true);
            var control = MapperHighlightTests.Control("Orders", "DataGrid", new(0, 0, 600, 120), "WPF", ["Grid"]);
            var selection = Assert.Single(MapperHighlightModel.Build(control.Bounds, [control]));
            GridSchemaReview? accepted = null;
            var inspector = new MapperGridInspector(selection) { AutoAnalyze = true,
                Explore = (_, _, _) => Task.FromResult(fixture.Source),
                SchemaExtractor = (source, header, dividers, token) => Task.FromResult(fixture.Review(header, dividers) with
                    { Reasons = status == GridCaptureStatus.Complete ? [] : ["schema-width-unverified"] }),
                SaveDataGrid = (name, schema) => { Assert.Equal("Orders", name); accepted = schema; } };
            try
            {
                inspector.Show(); var work = inspector.StartExplorationAsync(); Await(() => work.IsCompleted); work.GetAwaiter().GetResult(); Pump(30);
                Assert.NotNull(inspector.SavedSchema); Assert.Null(inspector.SavedSchema.ReviewedUtc); Assert.Null(accepted);
                Assert.Null(inspector.SchemaReviewWindow); Assert.Equal("Orders", Assert.Single(Descendants<TextBox>(inspector)).Text);
                Assert.Equal(status, inspector.SavedSchema.CaptureStatus);
                if (status == GridCaptureStatus.Complete)
                {
                    Assert.False(inspector.SavedSchema.Schema.IsComplete); // Acceptance has not happened yet.
                    Assert.DoesNotContain(Descendants<TextBlock>(inspector), t => t.Text.StartsWith("Partial capture"));
                }
                if (status == GridCaptureStatus.Partial)
                {
                    Assert.Contains(Descendants<TextBlock>(inspector), t => t.Text.Contains("Additional columns may exist"));
                    Assert.Contains(Descendants<TextBlock>(inspector), t => t.Text.StartsWith("Partial schema"));
                    Assert.DoesNotContain(Descendants<TextBlock>(inspector), t => t.Text.StartsWith("Schema ready"));
                    Assert.Contains(Descendants<Button>(inspector), b => Equals(b.Content, "Explore again"));
                }
                Click(Descendants<Button>(inspector).Single(b => Equals(b.Content, "Explore details"))); Pump(20);
                Assert.NotNull(inspector.SchemaReviewWindow!.Draft); Assert.Null(inspector.SchemaReviewWindow.Draft.ReviewedUtc);
                inspector.SchemaReviewWindow.Close(); Pump(20); Assert.Null(accepted);
                Capture(inspector, "automatic-grid-summary");
                Click(Descendants<Button>(inspector).Single(b => Equals(b.Content, "Save data grid")));
                Assert.NotNull(accepted); Assert.NotNull(accepted.ReviewedUtc); Assert.Equal(status, accepted.CaptureStatus);
            }
            finally { inspector.SchemaReviewWindow?.Close(); inspector.Close(); }
        });
    }

    [Fact]
    public void ClosingDuringAutomaticReadingCancelsBeforeResumingAndNeverAcceptsALateDraft()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Complete, GridRestorationStatus.Succeeded, true);
            var control = MapperHighlightTests.Control("Orders", "DataGrid", new(0, 0, 600, 120), "WPF", ["Grid"]);
            var pending = new TaskCompletionSource<GridSchemaReview>(); CancellationToken readingToken = default; var finished = 0;
            var inspector = new MapperGridInspector(Assert.Single(MapperHighlightModel.Build(control.Bounds, [control])))
            { AutoAnalyze = true, Explore = (_, _, _) => Task.FromResult(fixture.Source),
                SchemaExtractor = (_, _, _, token) => { readingToken = token; return pending.Task; } };
            inspector.Finished += () => finished++;
            inspector.Show(); var work = inspector.StartExplorationAsync(); Assert.True(inspector.IsExploring);
            inspector.Close(); Assert.True(readingToken.IsCancellationRequested); Assert.Equal(0, finished);
            pending.SetResult(fixture.Review(fixture.Source.HeaderBounds, null));
            Await(() => work.IsCompleted); work.GetAwaiter().GetResult(); Assert.Equal(1, finished); Assert.Null(inspector.SavedSchema);
        });
    }

    [Fact]
    public void AutomaticProviderFailureKeepsTheCaptureAvailableForManualDetails()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Failed, true);
            var control = MapperHighlightTests.Control("Orders", "DataGrid", new(0, 0, 600, 120), "WPF", ["Grid"]);
            var inspector = new MapperGridInspector(Assert.Single(MapperHighlightModel.Build(control.Bounds, [control])))
            { AutoAnalyze = true, Explore = (_, _, _) => Task.FromResult(fixture.Source),
                SchemaExtractor = (_, _, _, _) => Task.FromException<GridSchemaReview>(new AzureHeaderException("Provider unavailable")) };
            try
            {
                inspector.Show(); var work = inspector.StartExplorationAsync(); Await(() => work.IsCompleted); work.GetAwaiter().GetResult(); Pump(20);
                Assert.Null(inspector.SavedSchema); Assert.Same(fixture.Source, inspector.Result);
                Assert.Contains("Automatic header analysis could not finish", inspector.Error);
                Assert.Contains(Descendants<Button>(inspector), b => Equals(b.Content, "Explore details") && b.IsEnabled);
                Assert.Contains(Descendants<TextBlock>(inspector), t => t.Text == "Partial capture");
            }
            finally { inspector.Close(); }
        });
    }
    [Theory]
    [InlineData(GridCaptureStatus.Complete, GridRestorationStatus.Succeeded, true)]
    [InlineData(GridCaptureStatus.Partial, GridRestorationStatus.Failed, true)]
    [InlineData(GridCaptureStatus.Partial, GridRestorationStatus.SafelySkipped, true)]
    [InlineData(GridCaptureStatus.Failed, GridRestorationStatus.Failed, false)]
    public void ExtractButtonOpensHeaderReviewAndSavesEditedSchema(GridCaptureStatus status, GridRestorationStatus restoration, bool assembled)
    {
        Sta(() =>
        {
            using var fixture = new Fixture(status, restoration, assembled);
            var control = MapperHighlightTests.Control("Orders", "DataGrid", new(0, 0, 600, 120), "WPF", ["Grid"]);
            var selection = Assert.Single(MapperHighlightModel.Build(control.Bounds, [control]));
            var workspace = RecorderWorkspace.CreateStandaloneWorkspace(System.IO.Path.Combine(fixture.Directory, "orders.mlrec"), "OrdersApp");
            var context = new DataGridReviewContext(new("OrdersApp", "OrdersWindow", "Orders", [], new("Grid")),
                "orders-tab", new("Grid", "orders", "DataGrid", "Orders"), control.Bounds);
            var resumed = 0;
            var inspector = new MapperGridInspector(selection) { Left = 100, Top = 80, Explore = (_, _, _) => Task.FromResult(fixture.Source),
                SchemaExtractor = (source, header, dividers, token) => new GridSchemaExtractor().ExtractAsync(source, header, dividers, token),
                SaveDataGrid = (name, schema) => workspace.SaveDataGrid(name, context, schema) };
            inspector.Finished += () => resumed++;
            try
            {
                inspector.Show(); Pump(20);
                Capture(inspector, "explorer-start");
                var exploring = inspector.StartExplorationAsync(); Await(() => exploring.IsCompleted); exploring.GetAwaiter().GetResult();
                inspector.UpdateLayout(); Pump(20);
                if (status == GridCaptureStatus.Partial) Capture(inspector, "capture-review");
                var extract = Descendants<Button>(inspector).Single(b => Equals(b.Content, "Analyze headers"));
                Assert.True(extract.IsEnabled); Click(extract); Pump(50);
                var review = Assert.IsType<GridSchemaReviewWindow>(inspector.SchemaReviewWindow);
                Assert.True(review.IsVisible); Assert.True(review.IsActive);
                Assert.False(Descendants<DataGrid>(review).Single().IsVisible);
                Capture(review, "schema-select");
                Click(Descendants<Button>(review).Single(b => Equals(b.Content, "Read column names")));
                Await(() => !review.IsBusy);
                Assert.NotNull(review.Draft);
                Assert.Equal(new[] { "Order", "Name", "Total" }, review.Draft.Schema.Columns.Select(c => c.Label));
                var grid = Descendants<DataGrid>(review).Single();
                Assert.True(grid.IsVisible);
                var rows = grid.Items.Cast<GridSchemaReviewWindow.ColumnRow>().ToArray();
                // Exercise the actual editing control, including keyboard focus in the owned review window.
                grid.SelectedIndex = 0; grid.CurrentCell = new DataGridCellInfo(rows[0], grid.Columns[1]);
                grid.Focus(); Assert.True(grid.BeginEdit()); Pump(20);
                var editor = Descendants<TextBox>(grid).First(); editor.Focus(); editor.Text = "Order ID";
                Pump(20);
                var qa = Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR");
                if (!string.IsNullOrWhiteSpace(qa) && status == GridCaptureStatus.Partial && restoration == GridRestorationStatus.Failed)
                    Render((FrameworkElement)review.Content, System.IO.Path.Combine(qa, "schema-review.png"));
                Click(Descendants<Button>(review).Single(b => Equals(b.Content, "Save schema")));
                Await(() => !review.IsBusy);
                Assert.NotNull(review.SavedReview); Assert.Equal("Order ID", review.SavedReview.Schema.Columns[0].Label);
                Assert.Equal(status, review.SavedReview.CaptureStatus); Assert.Equal(restoration, review.SavedReview.Restoration.Status);
                Assert.NotNull(review.SavedPath); Assert.True(File.Exists(review.SavedPath));
                var saved = JsonSerializer.Deserialize<GridSchemaReview>(File.ReadAllText(review.SavedPath), JsonDefaults.Options)!;
                Assert.Equal("Order ID", saved.Schema.Columns[0].Label);
                Assert.False(review.IsVisible); Assert.Null(inspector.SchemaReviewWindow);
                Assert.Equal(0, resumed); Assert.True(inspector.IsVisible);
                Assert.NotNull(inspector.SavedSchema);
                if (status == GridCaptureStatus.Partial)
                {
                    Capture(inspector, "grid-naming");
                    Assert.Contains(Descendants<TextBlock>(inspector), text => text.Text.Contains("Additional columns may exist"));
                }
                Assert.Equal(status, inspector.Result!.Capture.Status);
                Click(Descendants<Button>(inspector).Single(b => Equals(b.Content, "Edit schema"))); Pump(20);
                Assert.Equal("Order ID", inspector.SchemaReviewWindow!.Draft!.Schema.Columns[0].Label);
                inspector.SchemaReviewWindow.Close(); Pump(20);
                var nameBox = Assert.Single(Descendants<TextBox>(inspector));
                Assert.Equal("Orders", nameBox.Text);
                var saveGrid = Descendants<Button>(inspector).Single(b => Equals(b.Content, "Save data grid"));
                nameBox.Text = "  "; Assert.False(saveGrid.IsEnabled);
                nameBox.Text = "Open orders"; Assert.True(saveGrid.IsEnabled); Click(saveGrid);
                Assert.False(inspector.IsVisible); Assert.Equal(1, resumed);
                var manifest = LogicalMapSessionStore.Load(workspace.SessionManifestPath);
                var namedGrid = Assert.Single(manifest.DataGrids!);
                Assert.Equal("Open orders", namedGrid.DisplayName); Assert.Equal("orders-tab", namedGrid.Context.SurfaceLayerKey);
                Assert.Equal("orders", namedGrid.Context.Control.AutomationId);
                Assert.Equal("Order ID", namedGrid.Review.Schema.Columns[0].Label);
                var restored = RecorderWorkspace.CreateExistingWorkspace(workspace.MapPath, workspace.DefaultExportPath, workspace.SessionManifestPath, manifest);
                restored.AddCompletedSession("next", System.IO.Path.Combine(fixture.Directory, "next.mlrec"));
                Assert.Equal("Open orders", Assert.Single(LogicalMapSessionStore.Load(workspace.SessionManifestPath).DataGrids!).DisplayName);
            }
            finally { inspector.SchemaReviewWindow?.Close(); inspector.Close(); }
        });
    }

    [Fact]
    public void ChangingHeaderSelectionInvalidatesEarlierReadAndClosingCancelsWork()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Failed, true);
            using var started = new ManualResetEventSlim();
            CancellationToken operationToken = default;
            var review = new GridSchemaReviewWindow(fixture.Source, extractor: async (_, _, _, token) =>
            {
                operationToken = token; started.Set(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException();
            });
            review.Show();
            var changed = new RectI(0, 40, 600, 36); review.SelectHeader(changed); Assert.Equal(changed, review.HeaderSelection);
            Assert.False(Descendants<Button>(review).Single(b => Equals(b.Content, "Save schema")).IsEnabled);
            var task = review.ExtractAsync(); Assert.True(started.IsSet);
            review.Close(); Await(() => task.IsCompleted); task.GetAwaiter().GetResult();
            Assert.True(operationToken.IsCancellationRequested); Assert.Null(review.SavedReview);
        });
    }

    [Fact]
    public void SelectedDividersCanBeDraggedNudgedRemovedAndAddedBeforeSaving()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Failed, true);
            var review = new GridSchemaReviewWindow(fixture.Source, extractor: (_, header, dividers, _) =>
                Task.FromResult(fixture.Review(header, dividers)));
            Button Find(string name) => Descendants<Button>(review).Single(b => Equals(b.Content, name));
            void Read() { Click(Find("Read column names")); Await(() => !review.IsBusy); Assert.NotNull(review.Draft); }
            try
            {
                review.Show(); Pump(30); Read();
                Descendants<CheckBox>(review).Single(c => Equals(c.Content, "Edit column dividers")).IsChecked = true;
                Assert.False(Find("Remove divider").IsEnabled);
                // Select from the table body, well below the header, then drag without crossing a neighbour.
                review.BeginPreviewInteraction(new(200, 90));
                review.ContinuePreviewInteraction(new(-30, 90));
                Assert.False(Find("Move left").IsEnabled);
                review.ContinuePreviewInteraction(new(900, 90));
                Assert.False(Find("Move right").IsEnabled);
                review.ContinuePreviewInteraction(new(240, 90)); review.EndPreviewInteraction();
                Assert.Null(review.Draft); Assert.False(Find("Save schema").IsEnabled);
                Click(Find("Move right")); Click(Find("Move left")); Read();
                Assert.Equal(new[] { 0, 240, 400 }, review.Draft!.HeaderReadings.Select(r => r.Bounds.X));
                review.BeginPreviewInteraction(new(240, 90)); review.EndPreviewInteraction();
                Click(Find("Remove divider"));
                Assert.False(Find("Remove divider").IsEnabled);
                Click(Find("Add divider")); review.BeginPreviewInteraction(new(399, 90));
                Assert.Contains("Cancel adding", Descendants<Button>(review).Select(b => b.Content));
                review.BeginPreviewInteraction(new(300, 90)); Read();
                Assert.Equal(new[] { 0, 300, 400 }, review.Draft!.HeaderReadings.Select(r => r.Bounds.X));
                Assert.Equal(fixture.Source.HeaderBounds, review.HeaderSelection);
                var save = review.SaveAsync(); Await(() => save.IsCompleted); save.GetAwaiter().GetResult();
                var saved = JsonSerializer.Deserialize<GridSchemaReview>(File.ReadAllText(review.SavedPath!), JsonDefaults.Options)!;
                Assert.Equal(new double[] { 300, 400, 600 }, saved.Schema.Columns.Select(c => c.EndX));
                Assert.False(saved.Schema.IsComplete);
                // Removing every detected divider must remain an explicit one-column schema on the next read.
                foreach (var x in new[] { 300, 400 })
                { review.BeginPreviewInteraction(new(x, 90)); review.EndPreviewInteraction(); Click(Find("Remove divider")); }
                Read(); Assert.Single(review.Draft!.Schema.Columns);
            }
            finally { review.Close(); }
        });
    }

    [Fact]
    public void WideImageOverlaysRemainVisibleAndSelectableAtEveryZoom()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Failed, true, 4800, 600, 12);
            var review = new GridSchemaReviewWindow(fixture.Source, extractor: (_, header, dividers, _) =>
                Task.FromResult(fixture.Review(header, dividers)));
            try
            {
                review.Show(); Pump(30);
                var task = review.ExtractAsync(); Await(() => task.IsCompleted); task.GetAwaiter().GetResult();
                var canvas = Descendants<Canvas>(review).Single();
                var zoom = Descendants<ComboBox>(review).Single();
                var scale = Assert.IsType<ScaleTransform>(canvas.LayoutTransform);
                Assert.InRange(scale.ScaleX, .01, .3);
                foreach (var index in new[] { 0, 1, 2, 0 })
                {
                    zoom.SelectedIndex = index; review.UpdateLayout(); Pump(20);
                    var lines = canvas.Children.OfType<Line>().Where(l => !Equals(l.Stroke, Brushes.White)).ToArray();
                    Assert.Equal(11, lines.Length);
                    Assert.All(lines, line =>
                    {
                        Assert.Equal(600, line.Y2); Assert.Equal(0, line.Y1);
                        Assert.Equal(1.5, line.StrokeThickness * scale.ScaleX, 6);
                    });
                    var header = Assert.Single(canvas.Children.OfType<Rectangle>(), r => r.Visibility == Visibility.Visible && !Equals(r.Stroke, Brushes.White));
                    Assert.Equal(2, header.StrokeThickness * scale.ScaleX, 6);
                    Assert.True(header.Height > 0);
                    Assert.Equal(0, Canvas.GetTop(header));
                    Assert.Equal(review.HeaderSelection.Height, header.RenderedGeometry.Bounds.Height + header.StrokeThickness, 6);
                }
                var qa = Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR");
                if (!string.IsNullOrWhiteSpace(qa)) Render((FrameworkElement)review.Content, System.IO.Path.Combine(qa, "schema-dividers-fit.png"));
                Descendants<CheckBox>(review).Single(c => Equals(c.Content, "Edit column dividers")).IsChecked = true; review.UpdateLayout(); Pump(20);
                // Hit tolerance is measured on screen even when the captured image is much wider than the window.
                review.BeginPreviewInteraction(new(800 + 6 / scale.ScaleX, 500)); review.EndPreviewInteraction();
                review.UpdateLayout(); Pump(30);
                Assert.True(Descendants<Button>(review).Single(b => Equals(b.Content, "Remove divider")).IsEnabled);
                Assert.Equal(2, canvas.Children.OfType<Line>().Count(l => l.X1 == 800));
                Assert.Contains(canvas.Children.OfType<Line>(), l => l.X1 == 800 && Math.Abs(l.StrokeThickness * scale.ScaleX - 3) < .001);
                if (!string.IsNullOrWhiteSpace(qa)) Render((FrameworkElement)review.Content, System.IO.Path.Combine(qa, "schema-dividers-edit.png"));
            }
            finally { review.Close(); }
        });
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ColumnMenuRemovesItsOwnRowAndPersistsRemainingPhysicalColumns(int removedIndex)
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Complete, GridRestorationStatus.Succeeded, true);
            var original = GridSchemaExtractor.Confirm(fixture.Review(fixture.Source.HeaderBounds, null) with { Reasons = [] }, ["Code", "Code", ""]);
            Assert.True(original.Schema.IsComplete);
            var review = new GridSchemaReviewWindow(fixture.Source, original);
            GridSchemaReviewWindow? reopened = null;
            try
            {
                review.Show(); Pump(30);
                var grid = Descendants<DataGrid>(review).Single();
                var editedIndex = (removedIndex + 1) % 3;
                grid.SelectedIndex = editedIndex;
                grid.CurrentCell = new DataGridCellInfo(grid.Items[editedIndex], grid.Columns[1]);
                grid.Focus(); Assert.True(grid.BeginEdit()); Pump(20);
                Descendants<TextBox>(grid).First().Text = "Edited header"; Pump(20);
                var menu = OpenColumnMenu(grid, removedIndex);
                Assert.Equal(removedIndex, grid.SelectedIndex);
                var remove = Assert.IsType<MenuItem>(Assert.Single(menu.Items.Cast<object>()));
                Assert.Equal("Remove column", remove.Header); Assert.True(remove.IsEnabled);
                var qa = Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR");
                if (!string.IsNullOrWhiteSpace(qa) && removedIndex == 1)
                    Render(menu, System.IO.Path.Combine(qa, "schema-remove-menu.png"));
                remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false; Pump(20);

                var expected = original.Schema.Columns.Select((column, i) => i == editedIndex ? column with { Label = "Edited header" } : column)
                    .Where((_, i) => i != removedIndex).Select((column, i) => column with { Ordinal = i }).ToArray();
                Assert.Equal(expected, review.Draft!.Schema.Columns);
                Assert.Null(review.Draft.ReviewedUtc);
                Assert.Equal(new[] { 1, 2 }, grid.Items.Cast<GridSchemaReviewWindow.ColumnRow>().Select(row => row.Number));
                Assert.Contains(Descendants<TextBlock>(review), text => text.Text.Contains("2 columns remaining", StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(qa) && removedIndex == 1)
                    Render((FrameworkElement)review.Content, System.IO.Path.Combine(qa, "schema-column-removed.png"));

                var save = review.SaveAsync(); Await(() => save.IsCompleted); save.GetAwaiter().GetResult();
                var saved = JsonSerializer.Deserialize<GridSchemaReview>(File.ReadAllText(review.SavedPath!), JsonDefaults.Options)!;
                Assert.Equal(expected, saved.Schema.Columns);
                Assert.Equal(original.HeaderReadings.Where((_, i) => i != removedIndex), saved.HeaderReadings);
                Assert.Equal(original.ImageSha256, saved.ImageSha256); Assert.Equal(original.HeaderBounds, saved.HeaderBounds);
                Assert.Equal(original.CaptureStatus, saved.CaptureStatus); Assert.Equal(original.Restoration, saved.Restoration);
                Assert.Equal(original.Coverage, review.SavedReview!.Coverage);
                Assert.False(saved.Schema.IsComplete); Assert.Null(saved.Schema.KnownColumnCount);
                Assert.NotNull(saved.ReviewedUtc);
                review.Close();
                reopened = new GridSchemaReviewWindow(fixture.Source, saved); reopened.Show(); Pump(20);
                Assert.Equal(expected, reopened.Draft!.Schema.Columns);
                var boundaries = Descendants<Canvas>(reopened).Single().Children.OfType<Line>()
                    .Select(line => line.X1).Distinct().Order().ToArray();
                Assert.Equal(new double[] { 200, 400 }, boundaries);
            }
            finally { reopened?.Close(); review.Close(); }
        });
    }

    [Fact]
    public void ColumnRemovalKeepsOneColumnAndCannotApplyWhileExtractionIsRunning()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Failed, true);
            var pending = new TaskCompletionSource<GridSchemaReview>();
            var review = new GridSchemaReviewWindow(fixture.Source, fixture.Review(fixture.Source.HeaderBounds, null),
                extractor: (_, _, _, _) => pending.Task);
            try
            {
                review.Show(); Pump(30);
                var grid = Descendants<DataGrid>(review).Single();
                for (var count = 3; count > 1; count--)
                {
                    var menu = OpenColumnMenu(grid, 0);
                    var remove = Assert.IsType<MenuItem>(menu.Items[0]);
                    Assert.True(remove.IsEnabled); remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    menu.IsOpen = false; Pump(20);
                    Assert.Equal(count - 1, review.Draft!.Schema.Columns.Count);
                }
                var lastMenu = OpenColumnMenu(grid, 0);
                var lastRemove = Assert.IsType<MenuItem>(lastMenu.Items[0]);
                Assert.False(lastRemove.IsEnabled);
                lastRemove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); lastMenu.IsOpen = false;
                Assert.Single(review.Draft!.Schema.Columns);
                var save = review.SaveAsync(); Await(() => save.IsCompleted); save.GetAwaiter().GetResult();
                Assert.Single(review.SavedReview!.Schema.Columns);

                pending.SetResult(fixture.Review(fixture.Source.HeaderBounds, null));
                var extraction = review.ExtractAsync(); Await(() => extraction.IsCompleted); extraction.GetAwaiter().GetResult();
                pending = new TaskCompletionSource<GridSchemaReview>();
                var busyMenu = OpenColumnMenu(grid, 1);
                var busyRemove = Assert.IsType<MenuItem>(busyMenu.Items[0]);
                Assert.True(busyRemove.IsEnabled);
                extraction = review.ExtractAsync(); Assert.True(review.IsBusy);
                busyRemove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); busyMenu.IsOpen = false;
                Assert.Equal(3, review.Draft!.Schema.Columns.Count);
                pending.SetResult(fixture.Review(fixture.Source.HeaderBounds, null));
                Await(() => extraction.IsCompleted); extraction.GetAwaiter().GetResult();
            }
            finally { pending.TrySetCanceled(); review.Close(); }
        });
    }

    private static ContextMenu OpenColumnMenu(DataGrid grid, int index)
    {
        grid.ScrollIntoView(grid.Items[index]); grid.UpdateLayout(); Pump(20);
        var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(index));
        var menu = Assert.IsType<ContextMenu>(row.ContextMenu);
        menu.PlacementTarget = row; menu.IsOpen = true; Pump(20);
        return menu;
    }

    [Fact]
    public void FailedGridSaveKeepsNamingOpenAndClosingResumesOnce()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.Failed, true);
            var control = MapperHighlightTests.Control("Orders", "DataGrid", new(0, 0, 600, 120), "WPF", ["Grid"]);
            var inspector = new MapperGridInspector(Assert.Single(MapperHighlightModel.Build(control.Bounds, [control])))
            {
                Explore = (_, _, _) => Task.FromResult(fixture.Source),
                SchemaExtractor = (_, header, dividers, _) => Task.FromResult(fixture.Review(header, dividers)),
                SaveDataGrid = (_, _) => throw new IOException("fixture-save-failure")
            };
            var resumed = 0; inspector.Finished += () => resumed++;
            try
            {
                inspector.Show();
                var explore = inspector.StartExplorationAsync(); Await(() => explore.IsCompleted);
                inspector.OpenSchemaReview();
                var review = inspector.SchemaReviewWindow!;
                var extract = review.ExtractAsync(); Await(() => extract.IsCompleted);
                var save = review.SaveAsync(closeAfterSave: true); Await(() => save.IsCompleted);
                Assert.False(review.IsVisible);
                Click(Descendants<Button>(inspector).Single(b => Equals(b.Content, "Save data grid")));
                Assert.True(inspector.IsVisible); Assert.Equal(0, resumed);
                Assert.Contains(Descendants<TextBlock>(inspector), text => text.Text.Contains("could not be saved", StringComparison.Ordinal));
                Click(Descendants<Button>(inspector).Single(b => Equals(b.Content, "×")));
                Assert.Equal(1, resumed);
                Assert.Equal("Column 1 / Column 2 table", MapperGridInspector.SuggestGridName("DataGrid", inspector.SavedSchema!));
            }
            finally { inspector.Close(); }
        });
    }

    [Fact]
    public void ClosingDuringExplorationQueuesResumeOnlyAfterExplorationUnwinds()
    {
        Sta(() =>
        {
            using var panel = new RecordingControlPanel("orders", "OrdersApp");
            var pending = new TaskCompletionSource<GridImageExplorationResult>();
            var control = MapperHighlightTests.Control("Orders", "DataGrid", new(0, 0, 600, 120), "WPF", ["Grid"]);
            var inspector = new MapperGridInspector(Assert.Single(MapperHighlightModel.Build(control.Bounds, [control])))
                { Explore = (_, _, _) => pending.Task };
            inspector.Finished += panel.RequestResumeAfterGrid;
            try
            {
                inspector.Show(); var operation = inspector.StartExplorationAsync();
                Pump(20); Capture(inspector, "exploring");
                Assert.True(inspector.IsExploring); inspector.Close();
                Assert.False(panel.TryDequeueCommand(out _));
                pending.SetCanceled(); Await(() => operation.IsCompleted); operation.GetAwaiter().GetResult();
                Assert.True(panel.TryDequeueCommand(out var command)); Assert.Equal("R", command);
                Assert.False(panel.TryDequeueCommand(out _));
            }
            finally { pending.TrySetCanceled(); inspector.Close(); }
        });
    }

    [Fact]
    public void HighlightsToggleVisibilityWithoutChangingCategoryIdentityAndCollapseRestores()
    {
        Sta(() =>
        {
            var visible = Enum.GetValues<MapperHighlightKind>().ToDictionary(kind => kind, _ => true);
            var legend = new MapperLegendWindow(kind => visible[kind], (kind, value) => visible[kind] = value);
            try
            {
                legend.Show(); Pump(20); Capture(legend, "highlights");
                var toggles = Descendants<CheckBox>(legend).ToArray(); Assert.Equal(4, toggles.Length);
                toggles[3].IsChecked = false; Assert.False(visible[MapperHighlightKind.GridCandidate]);
                Assert.True(visible[MapperHighlightKind.NativeGrid]);
                legend.SetCollapsed(true); Pump(20); Capture(legend, "highlights-collapsed");
                legend.SetCollapsed(false); Pump(20);
                Assert.False(Descendants<CheckBox>(legend).Last().IsChecked);
            }
            finally { legend.Close(); }
        });
    }

    [Fact]
    public void SchemaDropdownAndScrollbarsAreThemedAndDividerToolsKeepThePreviewUsable()
    {
        Sta(() =>
        {
            using var fixture = new Fixture(GridCaptureStatus.Partial, GridRestorationStatus.SafelySkipped, true, 4800, 600, 38);
            var review = new GridSchemaReviewWindow(fixture.Source, extractor: (_, header, dividers, _) => Task.FromResult(fixture.Review(header, dividers))) { Height = 740 };
            try
            {
                review.Show(); Pump(30);
                var extraction = review.ExtractAsync(); Await(() => extraction.IsCompleted); extraction.GetAwaiter().GetResult();
                Descendants<CheckBox>(review).Single(c => Equals(c.Content, "Edit column dividers")).IsChecked = true;
                var zoom = Descendants<ComboBox>(review).Single(); zoom.SelectedIndex = 1; Pump(30);
                var imageScroll = Descendants<ScrollViewer>(review).Single(s => s.Content is Canvas);
                Assert.True(imageScroll.ActualHeight >= 160, $"Preview squeezed to {imageScroll.ActualHeight}");
                imageScroll.ScrollToHorizontalOffset(150); Pump(20); Assert.Equal(150, imageScroll.HorizontalOffset);
                var bars = Descendants<System.Windows.Controls.Primitives.ScrollBar>(review).Where(b => b.IsVisible).ToArray();
                Assert.NotEmpty(bars);
                Assert.All(bars, b =>
                {
                    b.ApplyTemplate();
                    var track = Assert.IsType<System.Windows.Controls.Primitives.Track>(b.Template.FindName("PART_Track", b));
                    track.Thumb.ApplyTemplate();
                    Assert.Equal(4, Assert.IsType<Border>(VisualTreeHelper.GetChild(track.Thumb, 0)).CornerRadius.TopLeft);
                });
                var horizontalBar = Descendants<System.Windows.Controls.Primitives.ScrollBar>(imageScroll).Single(b => b.Orientation == Orientation.Horizontal);
                var horizontalTrack = (System.Windows.Controls.Primitives.Track)horizontalBar.Template.FindName("PART_Track", horizontalBar);
                horizontalTrack.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(30, 0)
                    { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
                Pump(20); Assert.True(imageScroll.HorizontalOffset > 150, "The themed thumb must still drag the image.");
                zoom.IsDropDownOpen = true; Pump(20);
                var item = Assert.IsType<ComboBoxItem>(zoom.ItemContainerGenerator.ContainerFromIndex(0)); item.ApplyTemplate();
                Assert.Equal(6, Assert.IsType<Border>(VisualTreeHelper.GetChild(item, 0)).CornerRadius.TopLeft);
                var qa = Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR");
                if (!string.IsNullOrWhiteSpace(qa))
                {
                    var popup = Descendants<System.Windows.Controls.Primitives.Popup>(zoom).Single();
                    Render((FrameworkElement)popup.Child, System.IO.Path.Combine(qa, "schema-zoom-menu.png"));
                }
                zoom.IsDropDownOpen = false; Capture(review, "schema-wide-divider-review");
            }
            finally { review.Close(); }
        });
    }

    private static void Capture(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); window.UpdateLayout();
        Render((FrameworkElement)window.Content, System.IO.Path.Combine(directory, name + ".png"));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "schema-ui-test-" + Guid.NewGuid().ToString("N"));
        internal GridImageExplorationResult Source { get; }
        private readonly int _columnCount;
        internal Fixture(GridCaptureStatus status, GridRestorationStatus restoration, bool assembled,
            int width = 600, int height = 120, int columnCount = 3)
        {
            _columnCount = columnCount;
            System.IO.Directory.CreateDirectory(Directory);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, width, 36));
                var names = new[] { "Order", "Name", "Total" };
                for (var i = 0; i < columnCount; i++)
                {
                    dc.DrawText(new FormattedText(i < names.Length ? names[i] : $"Column {i + 1}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 20, Brushes.Black, 1), new Point(i * width / columnCount + 12, 4));
                    dc.DrawText(new FormattedText(i == 0 ? "101" : i == 1 ? "Guest" : "40.29", CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 18, Brushes.Black, 1), new Point(i * width / columnCount + 12, 45));
                }
                for (var i = 1; i < columnCount; i++) dc.DrawRectangle(Brushes.Gray, null, new Rect(i * width / columnCount, 0, 1, height));
                for (var y = 36; y < height; y += 40) dc.DrawRectangle(Brushes.Gray, null, new Rect(0, y, width, 1));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream); var png = stream.ToArray();
            var path = System.IO.Path.Combine(Directory, "table.png"); File.WriteAllBytes(path, png);
            var image = new RectI(0, 0, width, height); var header = new RectI(0, 0, width, 36); var body = new RectI(0, 36, width, height - 36);
            var now = DateTimeOffset.UtcNow;
            var tile = new GridCapturedTile("tile", path, Convert.ToHexString(SHA256.HashData(png)), now, image, header, body, 0, 0, assembled, []);
            var capture = new CapturedGrid("acquisition", "grid", new("image", false, null, [], 0, 36), new(1, 1, 1, now, 1, image, image),
                now, now, status, GridAcquisitionStage.Capture, status == GridCaptureStatus.Complete ? [] : ["tile-limit"], [tile], [], [],
                new(true, true, status == GridCaptureStatus.Complete, status == GridCaptureStatus.Complete, true, false, false, [body], []),
                new(restoration, "fixture", false, false), System.IO.Path.Combine(Directory, "manifest.json"));
            Source = new(capture, assembled ? path : null, image, header, capture.Reasons);
        }
        internal GridSchemaReview Review(RectI header, IReadOnlyList<int>? dividers)
        {
            var edges = new[] { header.X }.Concat(dividers ?? Enumerable.Range(1, _columnCount - 1)
                .Select(i => header.X + i * header.Width / _columnCount).ToArray()).Append(header.X + header.Width).ToArray();
            var columns = edges.Zip(edges.Skip(1)).Select((pair, i) => new GridColumn($"column-{i}", i, $"Column {i + 1}", pair.First, pair.Second)).ToArray();
            return new(Source.Capture.AcquisitionId, Source.Capture.GridId, new("fixture", false, null, columns, 0, header.Height),
                header, Source.ImagePath!, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Source.ImagePath!))).ToLowerInvariant(),
                Source.Capture.ManifestPath, Source.Capture.Status, Source.Capture.Coverage, Source.Capture.Restoration,
                columns.Select(c => new GridSchemaHeaderReading(c.ColumnKey, new((int)c.StartX, header.Y, (int)(c.EndX - c.StartX), header.Height),
                    GridCellReadStatus.Text, c.Label, null)).ToArray(), ["schema-width-unverified"]);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
