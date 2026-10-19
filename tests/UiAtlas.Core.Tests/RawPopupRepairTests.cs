using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Desktop;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Mapping.Atlas;

namespace UiAtlas.Core.Tests;

public sealed class RawPopupRepairTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DialogProviderOverflowCannotExpandCanvasOrCropUnderlyingWindowInAnyMode(bool isolatedImages)
    {
        MapperOverlayWindowTests.Sta(() =>
        {
            using var temp = new TempDirectory();
            var source = CreateColoredBundle(temp.Path, "stable", isolatedImages: isolatedImages, dialog: true);
            var path = RawFrameEvidenceTests.Rewrite(source, temp.Path, frame => frame with {
                RawStreams = frame.RawStreams!.Select(stream => stream.TargetWindowHwnd != 2 || stream.Controls.Count == 0 ? stream : stream with {
                    ReturnedCount = stream.Controls.Count + 1,
                    Controls = [..stream.Controls, stream.Controls[0] with { RuntimeId = "overflow", Name = "Overflow",
                        Bounds = new(70, 60, 20, 20) }] }).ToArray() });
            var report = RecordingBundleValidator.Validate(path);
            Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Code + ": " + issue.Message)));
            using var reader = UiEvidenceReader.Open(path);
            using var bundle = RecordingBundle.Open(path);
            var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(bundle.ReadJson<RecordingManifest>("manifest.json"),
                [reader.ReadObservation("golden", 1)], []));
            var model = new UiMappingReadModel(graph);
            var layer = model.LayerFor(UiUnderstandingLevel.RawDataStreams);
            var dialog = layer.Surfaces.Single(surface => UiMappingReadModel.Property(surface.Source, "hwnd") == "0x2");
            var viewer = new ExplorerWindow(null);
            try
            {
                Set(viewer, "_model", model); Set(viewer, "_evidence", reader);
                Set(viewer, "_level", UiUnderstandingLevel.RawDataStreams); Set(viewer, "_inspectionLevel", UiUnderstandingLevel.RawDataStreams);
                Set(viewer, "_selectedSurfaceScope", new[] { dialog }); Set(viewer, "_selectedSurface", dialog);
                Set(viewer, "_selectedVariant", dialog.Variants[0]);
                foreach (var msaa in new[] { false, true, false })
                {
                    Get<System.Windows.Controls.Primitives.ToggleButton>(viewer, "_showRawMsaa").IsChecked = msaa;
                    foreach (var mode in new[] { "Window", "Controls", "Overlay", "Structure", "Structure Overlay" })
                    {
                        Set(viewer, "_viewMode", mode); Call(viewer, "RenderAppMap");
                        Assert.Equal(new RectI(20, 50, 40, 70), Get<RectI>(viewer, "_renderedViewportBounds"));
                        var bitmap = (BitmapSource)Get<Image>(viewer, "_appMapImage").Source;
                        Assert.Equal(isolatedImages ? Colors.Lime : Colors.Blue, PixelAt(bitmap, 5, 10));
                        var rectangles = Get<Canvas>(viewer, "_appMapOverlay").Children.OfType<Rectangle>().ToArray();
                        Assert.DoesNotContain(rectangles, rectangle => rectangle.ToolTip?.ToString()?.StartsWith("Overflow") == true);
                        if (!msaa || mode == "Window") Assert.Empty(rectangles);
                        else Assert.All(rectangles, rectangle => Assert.IsNotType<ImageBrush>(rectangle.Fill));
                        var banner = Assert.IsType<StackPanel>(Get<ContentControl>(viewer, "_traceBannerHost").Content);
                        Assert.Contains(banner.Children.OfType<TextBlock>(), text => text.Text.Contains("UIA: timeout, 0 records"));
                    }
                }
                Assert.Contains(reader.ReadObservation("golden", 1).RawStreams!.SelectMany(stream => stream.Controls), control => control.RuntimeId == "overflow");
            }
            finally { viewer.Close(); }
        });
    }

    [Theory]
    [InlineData(true, UiUnderstandingLevel.RawWorld)]
    [InlineData(false, UiUnderstandingLevel.RawWorld)]
    [InlineData(true, UiUnderstandingLevel.SemanticWorld)]
    [InlineData(false, UiUnderstandingLevel.SemanticWorld)]
    public void RawWorldUsesIsolatedPixelsOrMasksTheDialogAndExcludesBackgroundObservations(bool isolatedImages, UiUnderstandingLevel level)
    {
        MapperOverlayWindowTests.Sta(() =>
        {
            using var temp = new TempDirectory();
            var path = CreateColoredBundle(temp.Path, "stable", isolatedImages: isolatedImages, dialog: true);
            var graph = AtlasMapWorkspace.BuildAsync([path], System.IO.Path.Combine(temp.Path, "world.db")).GetAwaiter().GetResult();
            var model = new UiMappingReadModel(graph);
            Assert.True(GraphValidator.Validate(graph).IsValid);
            var layer = model.LayerFor(level);
            var main = layer.Surfaces.Single(surface => surface.Variants[0].NativeWindowHwnd == 1);
            var dialog = layer.Surfaces.Single(surface => surface.Variants[0].NativeWindowHwnd == 2);
            if (level == UiUnderstandingLevel.SemanticWorld)
            {
                Assert.Equal("SemanticDialogWindow", dialog.SurfaceKind);
                Assert.All(dialog.Variants, v => Assert.NotEmpty(v.SemanticVariantId));
                Assert.Contains(graph.Edges, edge => edge.Kind == "semantic-interpreted-from" && edge.ToId == dialog.Id);
            }
            Assert.All(main.Variants, variant => {
                Assert.False(variant.IsVisibleByDefault);
                Assert.Equal("foreground-dialog-observation", variant.Reason);
            });
            Assert.True(dialog.Variants[0].IsVisibleByDefault);
            using var reader = UiEvidenceReader.Open(path);
            var viewer = new ExplorerWindow(null);
            try
            {
                Set(viewer, "_model", model); Set(viewer, "_evidence", reader);
                Set(viewer, "_level", level);
                Set(viewer, "_inspectionLevel", level);
                foreach (var surface in new[] { main, dialog })
                {
                    Call(viewer, "SelectSurface", surface, new[] { surface });
                    var variant = surface == main ? surface.Variants[0] : model.VariantsFor([surface])[0];
                    Call(viewer, "SelectVariant", variant);
                    Assert.Equal(surface == main ? 1 : 2, Get<UiMapVariantView>(viewer, "_selectedVariant").NativeWindowHwnd);
                    if (level == UiUnderstandingLevel.SemanticWorld)
                        Assert.NotEmpty(Get<UiMapVariantView>(viewer, "_selectedVariant").SemanticVariantId);
                    foreach (var mode in new[] { "Window", "Controls", "Overlay", "Structure", "Structure Overlay" })
                    {
                        Set(viewer, "_viewMode", mode); Call(viewer, "RenderAppMap");
                        var pixels = (BitmapSource)Get<Image>(viewer, "_appMapImage").Source;
                        // Composite pixels are blue here; the main plane is red and
                        // the dialog plane is lime. This rejects composite leakage.
                        var expected = surface == main
                            ? isolatedImages ? Colors.Red : Color.FromRgb(0xF4, 0xF5, 0xF9)
                            : isolatedImages ? Colors.Lime : Colors.Blue;
                        Assert.Equal(expected, PixelAt(pixels, surface == main ? 25 : 5, surface == main ? 60 : 10));
                    }
                }
                var raw = reader.ReadRawSurface(main.Evidence[0], 1)!;
                Assert.Equal("raw/frames/frame-000001.png", raw.Entry);
                Assert.Null(raw.OccludedRegions);
            }
            finally { viewer.Close(); }
        });
    }

    [Fact]
    public void GeometryAndLegacyCropPolicyRespectOverlappingWindowsWithoutDeletingRecords()
    {
        using var temp = new TempDirectory();
        using var bundle = RecordingBundle.Open(SyntheticBundleFactory.Create(temp.Path));
        var frame = WithPopup(bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json"));
        var covered = new RectI(25, 60, 10, 10);
        var original = new UiEvidenceImage([], null, "frame", new(0, 0, 100, 120), null, null, frame,
            OccludedRegions: RawSurfaceImageLayout.Occluders(frame, frame.Window));
        Assert.False(RawSurfaceImageLayout.CanCropControl(original, covered));
        Assert.True(RawSurfaceImageLayout.CanCropControl(original, new(5, 5, 10, 10)));
        Assert.False(RawSurfaceImageLayout.CanCropControl(original, new(-5, 0, 10, 10)));
        Assert.True(RawSurfaceImageLayout.CanCropControl(original with { OccludedRegions = null }, covered));
        Assert.False(RawSurfaceImageLayout.CanCropControl(original with { ScreenshotFrameSequence = 99 }, new(5, 5, 10, 10)));
        Assert.Null(RawVariantPresentation.CaptureWarning(frame));
        var interrupted = frame with { Acquisition = frame.Acquisition! with { Status = "interrupted", StabilityStatus = "changed" } };
        Assert.Contains("changed during capture", RawVariantPresentation.CaptureWarning(interrupted));
        Assert.Null(RawVariantPresentation.ExclusionReason(interrupted, frame.Window.Hwnd));
    }

    [Theory]
    [InlineData("stable", false)]
    [InlineData("changed", false)]
    [InlineData("unverified", false)]
    [InlineData("legacy", false)]
    [InlineData("stable", true)]
    [InlineData("changed", true)]
    [InlineData("unverified", true)]
    [InlineData("legacy", true)]
    [InlineData("stable", true, true)]
    [InlineData("legacy", true, true)]
    [InlineData("legacy", false, true)]
    public void RendererKeepsSelectedQueryAndUsesItsRecordedScreenPixelsAcrossModes(string stability, bool mapsFoundation, bool ownerQuery = false)
    {
        MapperOverlayWindowTests.Sta(() =>
        {
            using var temp = new TempDirectory();
            var path = CreateColoredBundle(temp.Path, stability, ownerQuery);
            var validation = RecordingBundleValidator.Validate(path);
            Assert.True(validation.IsValid, string.Join("; ", validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
            using var reader = UiEvidenceReader.Open(path);
            var graph = mapsFoundation
                ? AtlasMapWorkspace.BuildAsync([path], System.IO.Path.Combine(temp.Path, "native.db")).GetAwaiter().GetResult()
                : new RecordingGraphBuilder().Build(path);
            var model = new UiMappingReadModel(graph);
            var raw = model.LayerFor(UiUnderstandingLevel.RawDataStreams);
            var main = raw.Surfaces.First(surface => UiMappingReadModel.Property(surface.Source, "hwnd") == "0x1");
            var family = model.BuildPipeline(UiUnderstandingLevel.RawDataStreams).Nodes.Single(node => node.SourceIds.Contains(main.Id));
            var scope = model.ResolvePipelineSurfaces(family);
            var viewer = new ExplorerWindow(null);
            try
            {
                Set(viewer, "_model", model); Set(viewer, "_evidence", reader);
                Set(viewer, "_level", UiUnderstandingLevel.RawDataStreams);
                Set(viewer, "_inspectionLevel", UiUnderstandingLevel.RawDataStreams);
                Set(viewer, "_selectedSurfaceScope", scope);
                Set(viewer, "_selectedSurface", main);
                Set(viewer, "_selectedVariant", main.Variants[0]);
                Set(viewer, "_viewMode", "Controls");
                Call(viewer, "DrawTopology"); Call(viewer, "RenderAppMap");
                var uiaToggle = Get<System.Windows.Controls.Primitives.ToggleButton>(viewer, "_showRawUia");
                var msaaToggle = Get<System.Windows.Controls.Primitives.ToggleButton>(viewer, "_showRawMsaa");
                Assert.True(uiaToggle.IsChecked); Assert.False(msaaToggle.IsChecked);
                if (!ownerQuery)
                {
                    // No silent MSAA fallback when UIA is absent.
                    Assert.Empty(Get<Canvas>(viewer, "_appMapOverlay").Children.OfType<Rectangle>());
                    msaaToggle.IsChecked = true;
                }
                var rectangles = Get<Canvas>(viewer, "_appMapOverlay").Children.OfType<Rectangle>().ToArray();
                var primary = Assert.Single(rectangles, rectangle => rectangle.ToolTip?.ToString()?.StartsWith("Owner button •") == true);
                Assert.Single(rectangles, rectangle => rectangle.ToolTip?.ToString()?.StartsWith("Covered button •") == true);
                Assert.Equal(Colors.Red, Pixel(Assert.IsType<ImageBrush>(primary.Fill)));
                var popupItems = rectangles.Where(rectangle => rectangle.ToolTip?.ToString()?.StartsWith("Popup item •") == true).ToArray();
                if (ownerQuery)
                {
                    var item = Assert.Single(popupItems);
                    Assert.Contains("query window 0x1", item.ToolTip.ToString());
                    Assert.Equal(Colors.Blue, Pixel(Assert.IsType<ImageBrush>(item.Fill)));
                    var bottom = Assert.Single(rectangles, shape => shape.ToolTip?.ToString()?.StartsWith("Popup bottom •") == true);
                    Assert.Equal(Colors.Blue, Pixel(Assert.IsType<ImageBrush>(bottom.Fill)));
                    Assert.Equal(120, Get<RectI>(viewer, "_renderedViewportBounds").Height);
                }
                else Assert.Empty(popupItems);
                var textCrop = Assert.Single(rectangles, rectangle => rectangle.ToolTip?.ToString()?.StartsWith("Scale: •") == true);
                Assert.Equal(Colors.Red, Pixel(Assert.IsType<ImageBrush>(textCrop.Fill)));
                Assert.DoesNotContain(Get<Canvas>(viewer, "_appMapOverlay").Children.OfType<TextBlock>(), text => text.Text == "Accessible description");
                Get<CheckBox>(viewer, "_showRawLabels").IsChecked = true;
                Call(viewer, "RenderAppMap");
                Assert.Contains(Get<Canvas>(viewer, "_appMapOverlay").Children.OfType<TextBlock>(), text => text.Text == "Accessible description");
                Get<CheckBox>(viewer, "_showRawLabels").IsChecked = false;
                Call(viewer, "RenderAppMap");
                if (stability != "stable")
                {
                    var banner = Assert.IsType<StackPanel>(Get<ContentControl>(viewer, "_traceBannerHost").Content);
                    Assert.Contains("alignment is unverified", Assert.IsType<TextBlock>(Assert.Single(banner.Children.OfType<Border>()).Child).Text);
                }
                Assert.Equal(System.Windows.Visibility.Collapsed, Get<Image>(viewer, "_appMapImage").Visibility);
                foreach (var mode in new[] { "Window", "Overlay", "Structure", "Structure Overlay" })
                {
                    Set(viewer, "_viewMode", mode); Call(viewer, "RenderAppMap");
                    var shapes = Get<Canvas>(viewer, "_appMapOverlay").Children.OfType<Rectangle>()
                        .Where(rectangle => rectangle.ToolTip?.ToString()?.StartsWith("Owner button •") == true).ToArray();
                    if (mode != "Window") Assert.NotEmpty(shapes);
                    if (mode == "Window") Assert.Empty(shapes);
                    if (mode is "Structure" or "Structure Overlay") Assert.All(shapes, shape => Assert.IsNotType<ImageBrush>(shape.Fill));
                    if (mode == "Overlay") Assert.Equal(Colors.Red, Pixel(Assert.IsType<ImageBrush>(Assert.Single(shapes).Fill)));
                    var scene = Get<Image>(viewer, "_appMapImage");
                    Assert.Equal(mode == "Structure" ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible, scene.Visibility);
                    if (mode == "Structure Overlay") Assert.InRange(scene.Opacity, 0.1, 0.9);
                    if (mode is "Window" or "Overlay") Assert.Equal(1.0, scene.Opacity);
                    Assert.Equal(Colors.Blue, PixelAt((BitmapSource)scene.Source, 25, 60));
                    var menuShapes = Get<Canvas>(viewer, "_appMapOverlay").Children.OfType<Rectangle>()
                        .Where(shape => shape.ToolTip?.ToString()?.StartsWith("Popup item •") == true).ToArray();
                    if (!ownerQuery || mode == "Window") Assert.Empty(menuShapes);
                    else if (mode == "Overlay") Assert.Equal(Colors.Blue, Pixel(Assert.IsType<ImageBrush>(Assert.Single(menuShapes).Fill)));
                }
                // Selecting a crop must highlight its border, not replace its pixels.
                Set(viewer, "_viewMode", "Controls");
                Set(viewer, "_selectedControl", raw.Controls.Single(control => control.DisplayName == "Owner button" && control.OwnerSurfaceId == main.Id));
                Call(viewer, "RenderAppMap");
                var selectedCrop = Get<Canvas>(viewer, "_appMapOverlay").Children.OfType<Rectangle>()
                    .Single(rectangle => rectangle.ToolTip?.ToString()?.StartsWith("Owner button •") == true);
                Assert.Equal(Colors.Red, Pixel(Assert.IsType<ImageBrush>(selectedCrop.Fill)));
                Assert.Equal(4, selectedCrop.StrokeThickness);
                Assert.Equal(stability == "stable", RawVariantPresentation.HasVerifiedAlignment(reader.ReadObservation("golden", 1)));
                // Actual card navigation must highlight one family for every instance.
                foreach (var variant in scope.SelectMany(surface => surface.Variants))
                {
                    Call(viewer, "SelectVariant", variant);
                    var selected = Get<Dictionary<string, Border>>(viewer, "_topologyShapes").Values.Where(border => border.BorderThickness.Left == 3).ToArray();
                    Assert.Single(selected);
                    Assert.True(Assert.IsType<UiPipelineNodeView>(selected[0].Tag).RepresentsSurface(variant.SurfaceId));
                }
                var popupSurface = raw.Surfaces.First(surface => UiMappingReadModel.Property(surface.Source, "hwnd") == "0x2");
                Call(viewer, "SelectSurface", popupSurface, new[] { popupSurface });
                Call(viewer, "SelectVariant", popupSurface.Variants[0]);
                foreach (var mode in new[] { "Window", "Controls", "Overlay", "Structure", "Structure Overlay" })
                {
                    Set(viewer, "_viewMode", mode); Call(viewer, "RenderAppMap");
                    var popupShapes = Get<Canvas>(viewer, "_appMapOverlay").Children.OfType<Rectangle>().ToArray();
                    Assert.All(popupShapes, shape => Assert.Contains("query window 0x2", shape.ToolTip.ToString()));
                    Assert.DoesNotContain(popupShapes, shape => shape.ToolTip?.ToString()?.StartsWith("Owner button •") == true);
                    var items = popupShapes.Where(shape => shape.ToolTip?.ToString()?.StartsWith("Popup item •") == true).ToArray();
                    if (ownerQuery || mode == "Window") Assert.Empty(items);
                    else if (mode is "Controls" or "Overlay") Assert.Equal(Colors.Blue, Pixel(Assert.IsType<ImageBrush>(Assert.Single(items).Fill)));
                    var scene = (BitmapSource)Get<Image>(viewer, "_appMapImage").Source;
                    Assert.Equal(40, scene.PixelWidth); Assert.Equal(70, scene.PixelHeight);
                    Assert.Null(reader.ReadWindow(popupSurface.Evidence[0], 2)!.DisplayBounds);
                }
                // Turning both sources off never enables a replacement source.
                uiaToggle.IsChecked = false; msaaToggle.IsChecked = false;
                Set(viewer, "_viewMode", "Overlay"); Call(viewer, "RenderAppMap");
                Assert.Empty(Get<Canvas>(viewer, "_appMapOverlay").Children.OfType<Rectangle>());
                Call(viewer, "SelectSurface", main, new[] { main });
                Call(viewer, "SelectVariant", main.Variants[0]);
                Assert.False(uiaToggle.IsChecked); Assert.False(msaaToggle.IsChecked);
                Assert.Empty(Get<Canvas>(viewer, "_appMapOverlay").Children.OfType<Rectangle>());
                var details = Get<ContentControl>(viewer, "_traceBannerHost");
                Assert.IsType<StackPanel>(details.Content).Children.OfType<Expander>().Single().IsExpanded = true;
                Call(viewer, "RenderAppMap");
                Assert.True(Assert.IsType<StackPanel>(details.Content).Children.OfType<Expander>().Single().IsExpanded);
            }
            finally { viewer.Close(); }
        });
    }

    [Theory]
    [InlineData("uia", "msaa")]
    [InlineData("uia.raw/0x1", "msaa/0x1")]
    public void DefaultProviderNeverFallsBackToMsaaEvenForEmptyOrFailedUia(string uia, string msaa)
    {
        using var temp = new TempDirectory();
        using var bundle = RecordingBundle.Open(SyntheticBundleFactory.Create(temp.Path));
        var frame = WithPopup(bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json"));
        var first = frame.RawStreams![0] with { Source = uia, StreamId = "uia" };
        var larger = first with { Source = msaa, StreamId = "msaa", Controls = Enumerable.Range(0, 30)
            .Select(i => first.Controls[0] with { RuntimeId = "msaa-" + i }).ToArray() };
        Assert.Equal(first, RawVariantPresentation.PreferredAttempt([larger, first]));
        var failed = first with { Status = "timeout", Controls = [] };
        Assert.Equal(failed, RawVariantPresentation.PreferredAttempt([larger, failed]));
        var container = first with { Controls = [first.Controls[0] with { ParentRuntimeId = "" }] };
        Assert.Equal(container, RawVariantPresentation.PreferredAttempt([larger, container]));
        Assert.Null(RawVariantPresentation.PreferredAttempt([larger]));
        Assert.True(RawVariantPresentation.IsSourceEnabled(uia, true, false));
        Assert.False(RawVariantPresentation.IsSourceEnabled(msaa, true, false));
        Assert.True(RawVariantPresentation.IsSourceEnabled(msaa, false, true));
        Assert.False(RawVariantPresentation.IsSourceEnabled(uia, false, true));
        var native = first with { Source = "native-hwnd", StreamId = "native" };
        Assert.Null(RawVariantPresentation.PreferredAttempt([native]));
        Assert.DoesNotContain(native, RawVariantPresentation.StreamsForWindow(frame with { RawStreams = [native, first] }, 1));
    }

    private static FrameObservation WithPopup(FrameObservation frame, bool ownerQuery = false)
    {
        var main = frame.Window with { Hwnd = 1, Bounds = new(0, 0, 100, 100), ZOrder = 2 };
        var popup = main with { Hwnd = 2, OwnerHwnd = 1, ClassName = "Net UI Tool Window", Title = "Palette",
            Style = 0x80000000L, IsToolWindow = true, Bounds = new(20, 50, 40, 70), ZOrder = 0 };
        AutomationObservation Control(string id, string name, long hwnd, RectI bounds) => frame.Automation[0] with {
            RuntimeId = id, ParentRuntimeId = "root", Name = name, ControlType = "Button", WindowHwnd = hwnd,
            QueryWindowHwnd = hwnd, ScopeStatus = "verified", Bounds = bounds, IsOffscreen = false };
        var result = frame with { Window = main, ScopedWindows = [main, popup], ScreenshotBounds = new(0, 0, 100, 120),
            Automation = [], Acquisition = new(RawObservationSemantics.Version, $"observation:{frame.Sequence}", frame.TimestampUtc,
                frame.TimestampUtc, "ok", "screen", "stable", WindowCoverage: [new(1, main.Bounds, "full"), new(2, popup.Bounds, "full")]), RawStreams = [
                RawFrameEvidenceTests.Stream(frame, "owner", Control("owner", "Owner button", 1, new(5, 5, 10, 10)),
                    Control("covered", "Covered button", 1, new(25, 60, 10, 10)),
                    Control("text", "Scale:", 1, new(5, 20, 20, 10)) with { ControlType = "Text" },
                    Control("image", "Accessible description", 1, main.Bounds) with { ControlType = "Image" }) with { Source = "msaa/0x1", TargetWindowHwnd = 1 },
                RawFrameEvidenceTests.Stream(frame, "popup", Control("popup", "Popup item", 2, new(25, 60, 10, 10))) with { Source = "msaa/0x2", TargetWindowHwnd = 2 },
                RawFrameEvidenceTests.Stream(frame, "empty") with { Source = "uia.raw/0x2", TargetWindowHwnd = 2, Status = "timeout" }] };
        if (!ownerQuery) return result;
        var branch = Control("popup-branch", "Palette", 1, popup.Bounds) with {
            ClassName = "NetUIToolWindow", ControlType = "Menu", NativeWindowHwnd = 20 };
        return result with { RawStreams = [
            result.RawStreams![0] with { Source = "uia", ReturnedCount = 7, Controls = [.. result.RawStreams[0].Controls, branch,
                Control("popup", "Popup item", 1, new(25, 60, 10, 10)) with { ParentRuntimeId = branch.RuntimeId },
                Control("popup-bottom", "Popup bottom", 1, new(25, 105, 10, 10)) with { ParentRuntimeId = branch.RuntimeId }] },
            result.RawStreams[1] with { Source = "uia", Controls = [
                Control("container", "Popup container", 2, popup.Bounds) with { ControlType = "Menu", ParentRuntimeId = "", NativeWindowHwnd = 2 }] },
            result.RawStreams[2]] };
    }

    private static string CreateColoredBundle(string directory, string stability, bool ownerQuery = false,
        bool isolatedImages = true, bool dialog = false)
    {
        using var original = RecordingBundle.Open(SyntheticBundleFactory.Create(directory, includeScreenshot: true));
        using var writer = new RecordingBundleWriter(System.IO.Path.Combine(directory, "colors-stage"));
        foreach (var entry in original.Entries.Where(entry => entry is not ("manifest.json" or "hashes.sha256") && !entry.StartsWith("raw/frames/")))
        {
            if (!entry.StartsWith("raw/observations/")) { writer.WriteBytes(entry, original.ReadBytes(entry)); continue; }
            var frame = WithPopup(original.ReadJson<FrameObservation>(entry), ownerQuery);
            if (dialog) frame = frame with { ScopedWindows = frame.ScopedWindows!.Select(window => window.Hwnd == 2
                ? window with { ClassName = "#32770", Title = "Font", IsToolWindow = false, Style = 0x80C40000L }
                : window with { IsEnabled = false }).ToArray() };
            var frameEntry = $"raw/frames/frame-{frame.Sequence:D6}.png";
            var planes = frame.ScopedWindows!.Select(window => new WindowImageObservation(window.Hwnd, window.Bounds,
                $"raw/frames/frame-{frame.Sequence:D6}-window-{window.Hwnd:X}.png", frame.TimestampUtc, frame.TimestampUtc,
                "windows-graphics-capture", "ok")).ToArray();
            if (isolatedImages)
                foreach (var plane in planes) writer.WriteBytes(plane.Entry!, Png(plane.Bounds.Width, plane.Bounds.Height, plane.Hwnd == 1 ? Colors.Red : Colors.Lime));
            writer.WriteBytes(frameEntry, ScreenPng());
            writer.WriteJson(entry, frame with { FrameEntry = frameEntry, WindowImages = isolatedImages ? planes : [],
                Acquisition = stability == "legacy" ? null : frame.Acquisition! with {
                    Status = stability == "changed" ? "interrupted" : "partial", StabilityStatus = stability } });
        }
        writer.WriteJson("manifest.json", original.ReadJson<RecordingManifest>("manifest.json") with { Files = writer.DescribeEntries() });
        var path = System.IO.Path.Combine(directory, "colors.mlrec"); writer.Complete(path); return path;
    }

    private static byte[] ScreenPng()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Red, null, new System.Windows.Rect(0, 0, 100, 100));
            dc.DrawRectangle(Brushes.Blue, null, new System.Windows.Rect(20, 50, 40, 70));
        }
        var image = new RenderTargetBitmap(100, 120, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }

    private static Color PixelAt(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4]; image.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static byte[] Png(int width, int height, Color color)
    {
        var pixels = Enumerable.Range(0, width * height).SelectMany(_ => new byte[] { color.B, color.G, color.R, color.A }).ToArray();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static Color Pixel(ImageBrush brush)
    {
        var pixel = new byte[4]; ((BitmapSource)brush.ImageSource).CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
    private static T Get<T>(object obj, string field) => (T)obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    private static void Set(object obj, string field, object value) => obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    private static void Call(object obj, string method, params object[] args) => obj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, args);
}
