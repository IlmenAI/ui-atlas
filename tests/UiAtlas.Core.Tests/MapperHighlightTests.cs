using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Tests;

public sealed class MapperHighlightTests
{
    [Fact]
    public void OfficeMsaaClientCoordinatesUseMeasuredDpiSpacesWithoutRescalingChromeOrUia()
    {
        var client = new RectI(2887, 410, 501, 522);
        var root = Control("client", "Window", client, "UiAtlas.Msaa") with { WindowHwnd = 200 };
        var ok = Control("ok", "Button", new(3558, 1385, 151, 54), "UiAtlas.Msaa") with { WindowHwnd = 200, ParentRuntimeId = root.RuntimeId };
        var close = Control("close", "Button", new(3341, 382, 47, 29), "UiAtlas.Msaa") with { WindowHwnd = 200 };
        var uia = ok with { RuntimeId = "uia", FrameworkId = "WPF" };
        var foreign = ok with { RuntimeId = "foreign", WindowHwnd = 300 };
        AutomationObservation[] original = [root, ok, close, uia, foreign];
        var projected = MapperMsaaGeometry.ProjectClient(original, 200, client, 1002, 1044);
        Assert.Equal(new RectI(3223, 898, 75, 26), projected[1].Bounds);
        Assert.Equal(ok.Bounds, original[1].Bounds);
        Assert.Equal(root, projected[0]);
        Assert.Equal(close, projected[2]);
        Assert.Equal(uia, projected[3]);
        Assert.Equal(foreign, projected[4]);
        Assert.Same(original, MapperMsaaGeometry.ProjectClient(original, 200, client, 501, 522));
        Assert.Same(original, MapperMsaaGeometry.ProjectClient(original, 200, client with { X = 0 }, 1002, 1044));
    }

    [Fact]
    public void NativeDialogUsesItsOwnMsaaResponseWhenUiaTimesOut()
    {
        var main = new WindowObservation(100, 100, 1, "XLMAIN", "Excel", Surface, true, false, false, false, 96, ZOrder: 5);
        var dialog = main with { Hwnd = 200, ClassName = "#32770", Title = "Format Cells", IsEnabled = true,
            Bounds = new(-150, 150, 500, 500), ZOrder = 4, OwnerHwnd = 100 };
        var control = Control("opaque-msaa-id", "Button", new(-100, 200, 80, 30), "") with { WindowHwnd = 200, Name = "OK" };
        var native = control with { RuntimeId = "native:geometry", Bounds = new(-100, 250, 80, 30) };
        var background = control with { RuntimeId = "main", WindowHwnd = 100, Bounds = new(-100, 300, 80, 30) };
        var now = DateTimeOffset.UtcNow;
        var frame = new FrameObservation(3, now, "", main, [background, native, control], true, "partial", "manual-action",
            [main, dialog, dialog with { Hwnd = 300, IsVisible = false, ZOrder = 0 }, dialog with { Hwnd = 400, ProcessId = 2, ZOrder = 0 }],
            RawStreams: [new("uia", "uia", now, now, "timeout", [], TargetWindowHwnd: 200),
                new("msaa", "msaa", now, now, "ok", [control], TargetWindowHwnd: 200),
                new("hwnd", "native-hwnd", now, now, "ok", [native], TargetWindowHwnd: 200)]);

        Assert.Equal(dialog, RecordingHighlightOverlay.ResolveMapperSurface(frame));
        var result = Assert.Single(MapperHighlightModel.Build(dialog.Bounds, MapperHighlightModel.FrameControls(frame, dialog.Hwnd)));
        Assert.Equal("OK", result.Name);
        Assert.Equal("MSAA", result.Source);
        Assert.Equal(control.Bounds, result.Bounds);
        Assert.Equal("", control.FrameworkId); // Presentation never rewrites the recording.
        Assert.Contains("UIA timeout", UiAtlas.Core.Cli.Program.IncompleteObservationStatus(frame));
        Assert.Contains("incomplete evidence", UiAtlas.Core.Cli.Program.IncompleteObservationStatus(frame));
    }

    private static readonly RectI Surface = new(-200, 100, 1000, 800);
    internal static AutomationObservation Control(string id, string type, RectI bounds,
        string framework = "WPF", string[]? patterns = null) =>
        new(id, "", id, id, "ControlType." + type, "Test", bounds, true, false, framework,
            SupportedPatterns: patterns);

    [Theory]
    [InlineData("WPF", "Grid", MapperHighlightKind.NativeGrid)]
    [InlineData("Win32", "Table", MapperHighlightKind.NativeGrid)]
    [InlineData("UiAtlas.Visual.Ocr", "Grid", MapperHighlightKind.GridCandidate)]
    [InlineData("UiAtlas.Visual.Geometry", "Table", MapperHighlightKind.GridCandidate)]
    [InlineData("UiAtlas.GridHostHint", "", MapperHighlightKind.GridCandidate)]
    public void NativeSupportRequiresActualProviderEvidence(string framework, string pattern, object expected)
    {
        var grid = Control("orders", "Table", new(-170, 160, 600, 400), framework, [pattern]);
        var result = Assert.Single(MapperHighlightModel.Build(Surface, [grid]));
        Assert.Equal(expected, result.Kind);
        Assert.Equal((MapperHighlightKind)expected == MapperHighlightKind.NativeGrid ? "Available" : "Not detected", result.NativeSupport);
    }

    [Fact]
    public void NamedLegacyUiaLeavesAreVisibleWithoutTheirLayoutContainersOrGridCells()
    {
        var toolbar = Control("toolbar", "Pane", new(-180, 120, 600, 60), "Win32");
        var paneButton = Control("legacy-button", "Pane", new(-170, 130, 100, 40), "Win32") with
            { ParentRuntimeId = "toolbar" };
        var customButton = Control("custom-button", "Custom", new(-60, 130, 100, 40), "Win32") with
            { ParentRuntimeId = "toolbar" };
        var result = MapperHighlightModel.Build(Surface,
        [toolbar, paneButton, customButton,
            Control("grid", "DataGrid", new(-180, 220, 600, 300), "UiAtlas.GridHostHint"),
            Control("cell", "Pane", new(-160, 250, 80, 30), "Win32"),
            Control("hidden", "Pane", new(60, 130, 100, 40), "Win32") with { IsOffscreen = true },
            Control("cached", "Pane", new(170, 130, 100, 40), "UiAtlas.Cached")]);
        Assert.Equal(3, result.Count);
        Assert.All(result.Where(c => !c.IsGrid), c => Assert.Equal(MapperHighlightKind.Uia, c.Kind));
        Assert.Contains(result, c => c.Id == paneButton.RuntimeId);
        Assert.Contains(result, c => c.Id == customButton.RuntimeId);
    }

    [Theory]
    [InlineData(100, 100, 1200, 700, 0, 0, 1920, 1080)]
    [InlineData(0, 0, 1920, 1080, -1920, 0, 1920, 1080)]
    [InlineData(-1600, 0, 1400, 900, -1920, 0, 1920, 1080)]
    public void LegendUsesExteriorDesktopSpace(int x, int y, int width, int height,
        int screenX, int screenY, int screenWidth, int screenHeight)
    {
        var target = new RectI(x, y, width, height);
        var screen = new RectI(screenX, screenY, screenWidth, screenHeight);
        var legend = MapperLegendWindow.FindExteriorPosition(target, [screen], 222, 178);
        Assert.NotNull(legend);
        Assert.True(MapperHighlightModel.Contains(screen, legend));
        Assert.True(legend.X + legend.Width <= target.X || legend.X >= target.X + target.Width ||
            legend.Y + legend.Height <= target.Y || legend.Y >= target.Y + target.Height);
    }

    [Fact]
    public void FullscreenTargetHasNoInventedExteriorSpace()
    {
        var target = new RectI(0, 0, 1920, 1080);
        Assert.Null(MapperLegendWindow.FindExteriorPosition(target, [target], 222, 178));
        var almostFull = new RectI(0, 0, 1870, 1080);
        Assert.Null(MapperLegendWindow.FindExteriorPosition(almostFull, [target], 222, 178));
        Assert.NotNull(MapperLegendWindow.FindExteriorPosition(almostFull, [target], 34, 34));
        Assert.True(MapperHighlightModel.Contains(target,
            MapperLegendWindow.ClampToWorkArea(new(1880, 1040, 222, 178), target)));
    }

    [Fact]
    public void HiddenZeroSizedApplicationOwnerUsesTheSelectedVisibleWindow()
    {
        var hiddenOwner = new WindowObservation(100, 100, 1, "TApplication", "", new(1440, 960, 0, 0),
            true, true, false, false, 96);
        Assert.Equal(200, RecordingHighlightOverlay.ResolveMapperSurfaceHwnd(200, hiddenOwner));
        var visibleSurface = hiddenOwner with { Hwnd = 300, Bounds = new(3088, 429, 1478, 975) };
        Assert.Equal(300, RecordingHighlightOverlay.ResolveMapperSurfaceHwnd(200, visibleSurface));
    }

    [Fact]
    public void RecognizesTheCollectorsRealUiaPatternNames()
    {
        foreach (var pattern in new[] { System.Windows.Automation.GridPattern.Pattern, System.Windows.Automation.TablePattern.Pattern })
        {
            var result = Assert.Single(MapperHighlightModel.Build(Surface,
                [Control("native", "DataGrid", new(-180, 200, 300, 200), patterns: [pattern.ProgrammaticName])]));
            Assert.Equal(MapperHighlightKind.NativeGrid, result.Kind);
        }
    }

    [Fact]
    public void NativeGridWinsOverVisualDuplicateAndSuppressesCells()
    {
        var bounds = new RectI(-180, 160, 600, 400);
        var native = Control("native", "DataGrid", bounds, patterns: ["Grid"]);
        var visual = Control("visual:grid", "Table", bounds with { X = -178, Width = 596 }, "UiAtlas.Visual.Ocr", ["Grid"]);
        var cell = Control("cell", "Edit", new(-100, 200, 80, 30));
        var cellText = Control("text", "Text", new(-90, 240, 70, 15));
        var result = Assert.Single(MapperHighlightModel.Build(Surface, [visual, native, cell, cellText]));
        Assert.Equal("native", result.Id);
    }

    [Fact]
    public void MultipleGridsRemainDistinctAndOrdinarySourcesRetainTheirColors()
    {
        var result = MapperHighlightModel.Build(Surface,
        [
            Control("a", "DataGrid", new(-180, 200, 300, 200), patterns: ["Grid"]),
            Control("b", "Table", new(150, 200, 300, 200), "UiAtlas.Visual.Ocr", ["Grid"]),
            Control("button", "Button", new(-180, 130, 80, 30)),
            Control("visual:button", "Button", new(10, 130, 80, 30), "UiAtlas.Visual.Ocr")
        ]);
        Assert.Equal(2, result.Count(g => g.IsGrid));
        Assert.Equal(4, result.Select(g => g.Color).Distinct().Count());
        Assert.Equal("Computer vision", result.Single(g => g.Id == "b").Source);
    }

    [Fact]
    public void HiddenNativeAndOutsideControlsAreNotClickableButVisualOffscreenSentinelIsSupported()
    {
        var result = MapperHighlightModel.Build(Surface,
        [
            Control("hidden", "DataGrid", new(-180, 200, 300, 200), patterns: ["Grid"]) with { IsOffscreen = true },
            Control("outside", "DataGrid", new(900, 200, 300, 200), patterns: ["Grid"]),
            Control("visual:grid", "Table", new(-180, 200, 300, 200), "UiAtlas.Visual.Ocr", ["Grid"]) with { IsOffscreen = true }
        ]);
        Assert.Equal("visual:grid", Assert.Single(result).Id);
    }

    [Fact]
    public void EmptyScanClearsAndGridItemsNeverBecomeNativeGrids()
    {
        Assert.Empty(MapperHighlightModel.Build(Surface, []));
        Assert.Empty(MapperHighlightModel.Build(Surface,
            [Control("cell", "DataItem", new(-180, 200, 100, 80), patterns: ["GridItem"])]));
    }

    [Fact]
    public void UnknownGridSupportDoesNotClaimReadingOrCompleteness()
    {
        var grid = Assert.Single(MapperHighlightModel.Build(Surface,
            [Control("hint", "DataGrid", new(-180, 200, 300, 200), "UiAtlas.GridHostHint")]));
        Assert.Equal("Native window hint", grid.Source);
        Assert.Equal("Candidate · not yet verified", grid.Status);
        Assert.Equal("Potential data grid", grid.Classification);
    }

    [Fact]
    public void OfficeGalleryStaysAnOrdinaryControlInsideItsScrollViewport()
    {
        var surface = new RectI(2880, 381, 1920, 1032);
        var viewport = Control("styles-viewport", "Pane", new(3909, 474, 214, 56), "Win32",
            ["ScrollPatternIdentifiers.Pattern"]) with { ClassName = "NetUIScrollViewer", WindowHwnd = 42 };
        var gallery = Control("styles-gallery", "DataGrid", new(3910, 418, 212, 784), "Win32",
            ["ScrollItemPatternIdentifiers.Pattern", "SelectionPatternIdentifiers.Pattern"]) with
            { Name = "Cell Styles", ClassName = "NetUIGalleryButtonGroup", ParentRuntimeId = viewport.RuntimeId, WindowHwnd = 42 };
        var visibleButton = Control("style-normal", "Button", new(3910, 510, 100, 30), "Win32") with
            { ParentRuntimeId = gallery.RuntimeId, WindowHwnd = 42 };
        var hiddenButton = visibleButton with { RuntimeId = "style-below", Bounds = new(3910, 600, 100, 30) };
        var sheet = Control("sheet", "DataGrid", new(2880, 600, 1898, 759), patterns: ["Grid"]);
        var table = Control("inventory", "Table", new(2947, 799, 1474, 480), patterns: ["Table"]);

        var result = MapperHighlightModel.Build(surface, [viewport, gallery, visibleButton, hiddenButton, sheet, table]);

        Assert.Equal(2, result.Count(item => item.IsGrid));
        Assert.All(result.Where(item => item.IsGrid), item => Assert.Equal(MapperHighlightKind.NativeGrid, item.Kind));
        var presentedGallery = Assert.Single(result, item => item.Id == gallery.RuntimeId);
        Assert.Equal(MapperHighlightKind.Uia, presentedGallery.Kind);
        Assert.Equal(new RectI(3910, 474, 212, 56), presentedGallery.Bounds);
        Assert.Same(gallery, presentedGallery.Control); // Raw provider evidence is never rewritten.
        Assert.Equal(new RectI(3910, 510, 100, 20), Assert.Single(result, item => item.Id == visibleButton.RuntimeId).Bounds);
        Assert.DoesNotContain(result, item => item.Id == hiddenButton.RuntimeId);
    }

    [Fact]
    public void ViewportClippingUsesSameWindowAncestorsAndPreservesUnqualifiedGridCandidates()
    {
        var viewport = Control("viewport", "Pane", new(-180, 200, 300, 100), patterns: ["Scroll"]) with { WindowHwnd = 1 };
        var candidate = Control("table", "Table", new(-170, 180, 200, 400)) with
            { ParentRuntimeId = viewport.RuntimeId, WindowHwnd = 1 };
        var otherWindow = candidate with { RuntimeId = "other", Bounds = new(200, 200, 250, 400), WindowHwnd = 2 };
        var result = MapperHighlightModel.Build(Surface, [viewport, candidate, otherWindow]);
        var clipped = Assert.Single(result, item => item.Id == candidate.RuntimeId);
        Assert.Equal(MapperHighlightKind.GridCandidate, clipped.Kind);
        Assert.Equal(new RectI(-170, 200, 200, 100), clipped.Bounds);
        Assert.Equal(candidate.Bounds, clipped.Control.Bounds);
        Assert.Equal(otherWindow.Bounds, Assert.Single(result, item => item.Id == otherWindow.RuntimeId).Bounds);
    }
}
