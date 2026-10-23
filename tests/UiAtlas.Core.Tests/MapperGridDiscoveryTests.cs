using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Tests;

public sealed class MapperGridDiscoveryTests
{
    internal static readonly RectI Surface = new(-600, 200, 600, 400);
    internal static AutomationObservation Host => new("pane-evidence", "", "28", "Arrival Guest List",
        "ControlType.Pane", "LegacyBrowse", new(-580, 220, 560, 320), true, false, "Win32", 42,
        ["ScrollPatternIdentifiers.Pattern"], NativeWindowHwnd: 84);

    internal static FrameObservation Frame(params AutomationObservation[] controls) => new(1, DateTimeOffset.UtcNow,
        "raw/frames/frame-000001.png", new(42, 42, 7, "Window", "Fixture", Surface, true, true, false, false, 96),
        controls, false, "ok", "manual-initial-surface", ScreenshotBounds: Surface, NativeEpisodeEntry: "raw/native/frame-000001.episode.pb");

    [Fact]
    public void RetainedPixelsMakeOpaqueScrollPaneClickableWithoutChangingProviderEvidence()
    {
        var host = Host;
        var frame = Frame(host);
        Assert.DoesNotContain(MapperHighlightModel.Build(Surface, frame.Automation), g => g.IsGrid);
        var candidates = MapperGridDiscovery.Discover(frame, frame.Window, frame.Automation, Pixels());
        var grid = Assert.Single(MapperHighlightModel.Build(Surface, frame.Automation, candidates), g => g.IsGrid);
        Assert.Equal(MapperHighlightKind.GridCandidate, grid.Kind);
        Assert.Equal("Computer vision", grid.Source);
        Assert.Same(host, grid.Control);
        Assert.Equal("ControlType.Pane", Assert.Single(frame.Automation).ControlType);
        Assert.Equal(84, MapperGridDiscovery.HostHwnd(grid.Control));
        Assert.Equal("Not detected", grid.NativeSupport);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EmptyPaneAndOneDimensionalRulesAreNotTables(bool horizontal, bool vertical)
    {
        var frame = Frame(Host);
        Assert.Empty(MapperGridDiscovery.Discover(frame, frame.Window, frame.Automation, Pixels(horizontal, vertical)));
    }

    [Fact]
    public void HiddenHostsAndWrongPixelOriginCannotCreateCandidates()
    {
        var frame = Frame(Host with { IsOffscreen = true });
        Assert.Empty(MapperGridDiscovery.Discover(frame, frame.Window, frame.Automation, Pixels()));
        frame = Frame(Host) with { ScreenshotBounds = Surface with { Width = 601 } };
        Assert.Empty(MapperGridDiscovery.Discover(frame, frame.Window, frame.Automation, Pixels()));
    }

    [Fact]
    public void SmallestHostWinsAndNativeGridsKeepTheirClassification()
    {
        var host = Host;
        var parent = host with { RuntimeId = "parent", Bounds = Surface, AutomationId = "parent" };
        var frame = Frame(parent, host);
        Assert.Same(host, Assert.Single(MapperGridDiscovery.Discover(frame, frame.Window, frame.Automation, Pixels())).Control);
        var native = host with { ControlType = "ControlType.DataGrid", SupportedPatterns = ["Grid"] };
        frame = Frame(parent, native);
        var derived = MapperGridDiscovery.Discover(frame, frame.Window, frame.Automation, Pixels());
        Assert.Empty(derived);
        Assert.Equal(MapperHighlightKind.NativeGrid, Assert.Single(MapperHighlightModel.Build(Surface, frame.Automation),
            g => g.IsGrid).Kind);
    }

    [Fact]
    public void NewEpisodeBindsToSameProviderAndRejectsAmbiguousOrChangedHosts()
    {
        var grid = Assert.Single(MapperGridDiscovery.Discover(Frame(Host), Frame(Host).Window, [Host], Pixels()));
        var fresh = Host with { RuntimeId = "fresh-uia" };
        var native = fresh with { RuntimeId = "fresh-hwnd", FrameworkId = "UiAtlas.NativeHwnd" };
        var msaa = fresh with { RuntimeId = "fresh-msaa", FrameworkId = "UiAtlas.Msaa" };
        Assert.Same(fresh, MapperGridDiscovery.ResolveRetainedHost(grid, Frame(fresh, native, msaa), Host.Bounds));
        Assert.Null(MapperGridDiscovery.ResolveRetainedHost(grid, Frame(fresh, fresh with { RuntimeId = "duplicate" }), Host.Bounds));
        Assert.Null(MapperGridDiscovery.ResolveRetainedHost(grid, Frame(fresh with { NativeWindowHwnd = 85 }), Host.Bounds));
    }

    internal static byte[] Pixels(bool horizontal = true, bool vertical = true)
    {
        var pixels = Enumerable.Repeat((byte)255, 600 * 400 * 4).ToArray();
        if (horizontal) for (var y = 20; y <= 340; y += 32) for (var x = 20; x < 580; x++) Ink(x, y);
        if (vertical) for (var x = 20; x <= 580; x += 112) for (var y = 20; y < 340; y++) Ink(x, y);
        var bitmap = BitmapSource.Create(600, 400, 96, 96, PixelFormats.Bgra32, null, pixels, 600 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
        void Ink(int x, int y) { var offset = (y * 600 + x) * 4; pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 160; }
    }
}
