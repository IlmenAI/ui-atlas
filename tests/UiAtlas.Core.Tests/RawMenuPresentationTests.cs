using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Desktop;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class RawMenuPresentationTests
{
    private static WindowObservation Window(long hwnd, long owner, string kind, RectI bounds, int z) =>
        new(hwnd, 1, 42, kind, kind, bounds, true, true, false, false, 96, OwnerHwnd: owner, ZOrder: z,
            Style: kind == "Net UI Tool Window" ? 0x80000000L : 0x00C40000L,
            IsToolWindow: kind == "Net UI Tool Window");

    private static FrameObservation Frame(params WindowObservation[] windows) =>
        new(1, DateTimeOffset.UtcNow, "", windows[0], [], false, "ok", "test", windows);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MenuViewUsesExactRecordedFrameBytesAndNeverBorrowsPixels(bool screenshot)
    {
        using var temp = new TempDirectory();
        var source = SyntheticBundleFactory.Create(temp.Path, includeScreenshot: screenshot);
        var path = RawFrameEvidenceTests.Rewrite(source, temp.Path, frame => frame with {
            ScopedWindows = [frame.Window, Window(987, frame.Window.Hwnd, "Net UI Tool Window",
                new(frame.Window.Bounds.X + 20, frame.Window.Bounds.Y + 20, 20, 20), -1) with { ProcessId = frame.Window.ProcessId }] });
        using var reader = UiEvidenceReader.Open(path);
        var frame = reader.ReadObservation("golden", 1);
        var evidence = new EvidenceRef("golden", 1, "raw/observations/frame-000001.json", null);
        var image = reader.ReadRawSurface(evidence, frame.Window.Hwnd);
        if (!screenshot) { Assert.Null(image); return; }
        Assert.Equal(reader.Read(evidence)!.Png, image!.Png);
        Assert.Equal(frame.FrameEntry, image.Entry);
        Assert.Contains("recorded frame screenshot", image.PixelProvenance);
        Assert.Null(image.VisibleRegions);
        Assert.Null(image.DisplayBounds);
        Assert.Null(image.OccludedRegions);
        Assert.False(image.InheritedPixels);
    }

    [Fact]
    public void AttemptChoicesUseProviderTargetAndPreserveEmptyFailuresWithoutMovingControls()
    {
        using var temp = new TempDirectory();
        using var bundle = RecordingBundle.Open(SyntheticBundleFactory.Create(temp.Path));
        var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
        var frame = bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json");
        var main = frame.Window;
        var popup = main with { Hwnd = 987, OwnerHwnd = main.Hwnd, ClassName = "Net UI Tool Window", IsToolWindow = true, Style = 0x80000000L };
        var mainControl = frame.Automation[0] with { QueryWindowHwnd = main.Hwnd, WindowHwnd = main.Hwnd, ScopeStatus = "verified" };
        // Identical geometry does not make this main-window record belong to the popup.
        var popupControl = mainControl with { RuntimeId = "popup", QueryWindowHwnd = popup.Hwnd, WindowHwnd = popup.Hwnd };
        var popupStream = RawFrameEvidenceTests.Stream(frame, "uia.raw", popupControl) with { TargetWindowHwnd = popup.Hwnd };
        var emptyStream = RawFrameEvidenceTests.Stream(frame, "uia.control/0x3DB") with { Status = "timeout" };
        frame = frame with { ScopedWindows = [main, popup], RawStreams = [
            RawFrameEvidenceTests.Stream(frame, "uia.raw/main", mainControl) with { TargetWindowHwnd = main.Hwnd },
            popupStream, emptyStream, RawFrameEvidenceTests.Stream(frame, "ocr", popupControl)] };
        Assert.Equal(new[] { popupStream, emptyStream }, RawVariantPresentation.StreamsForWindow(frame, popup.Hwnd));
        Assert.Contains("1 verified", RawVariantPresentation.ScopeSummary(popupStream.Controls, popup.Hwnd));
        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [frame], []));
        var path = Path.Combine(temp.Path, "scope.db");
        SqliteGraphStore.Save(graph, path);
        var model = new UiMappingReadModel(SqliteGraphStore.Load(path));
        var raw = model.LayerFor(UiUnderstandingLevel.RawDataStreams);
        var popupSurface = raw.Surfaces.Single(surface => UiMappingReadModel.Property(surface.Source, "hwnd") == "0x3DB");
        var retained = Assert.Single(raw.ControlsForSurface(popupSurface.Id));
        Assert.Equal("popup", UiMappingReadModel.Property(retained.Source, "runtimeId"));
    }

    [Theory]
    [InlineData(UiMapProjectionMode.Controls)]
    [InlineData(UiMapProjectionMode.Overlay)]
    [InlineData(UiMapProjectionMode.Structure)]
    [InlineData(UiMapProjectionMode.StructureOverlay)]
    public void RawLabelsAreOptInAndUnknownFramesDoNotSupplyControlCrops(UiMapProjectionMode mode)
    {
        Assert.False(RawVariantPresentation.PolicyFor(mode, null).ShowsControlLabels);
        Assert.False(RawVariantPresentation.PolicyFor(mode, null).ShowsControlCrops);
        Assert.True(RawVariantPresentation.PolicyFor(mode, null, showLabels: true).ShowsControlLabels);
        var frame = Frame(Window(1, 0, "Main", new(0, 0, 100, 100), 0));
        frame = frame with { Acquisition = new(RawObservationSemantics.Version, "observation:1", frame.TimestampUtc,
            frame.TimestampUtc, "partial", "screen", "stable") };
        Assert.Equal(mode is UiMapProjectionMode.Controls or UiMapProjectionMode.Overlay,
            RawVariantPresentation.PolicyFor(mode, frame).ShowsControlCrops);
    }
}
