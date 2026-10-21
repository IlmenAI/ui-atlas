using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Desktop;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Tests;

public sealed class RawOfficeCoordinatesTests
{
    [Fact]
    public void SingleEditAndIndependentScrollbarRegisterBothProvidersWithoutChangingEvidence()
    {
        var frame = Frame();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
        var registered = RawOfficeCoordinates.Resolve(frame, 2);
        foreach (var stream in new[] { "uia", "msaa" })
        {
            Assert.Equal(new RectI(3174, 760, 75, 27), registered[(stream, "ok")].Display);
            Assert.Equal(new RectI(2937, 440, 312, 36), registered[(stream, "edit")].Display);
            Assert.Equal(new RectI(2937, 537, 396, 107), registered[(stream, "list")].Display);
            Assert.DoesNotContain((stream, "close"), registered.Keys);
            Assert.DoesNotContain((stream, "physical-edit"), registered.Keys);
            Assert.DoesNotContain((stream, "physical-child"), registered.Keys);
            Assert.DoesNotContain((stream, "invalid"), registered.Keys);
        }
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(frame));
        Assert.Equal(registered.OrderBy(p => p.Key).ToArray(), RawOfficeCoordinates.Resolve(frame, 2).OrderBy(p => p.Key).ToArray());
    }

    [Theory]
    [InlineData("no-native")]
    [InlineData("one-anchor")]
    [InlineData("wrong-window")]
    [InlineData("wrong-dpi")]
    [InlineData("wrong-class")]
    [InlineData("ambiguous-scroll")]
    [InlineData("hidden-scroll")]
    [InlineData("unknown-scope")]
    [InlineData("missing-client")]
    public void UnqualifiedEvidenceNeverMovesRectangles(string mode)
    {
        var frame = Frame();
        if (mode == "wrong-dpi") frame = frame with { Window = frame.Window with { Dpi = 144 } };
        if (mode == "wrong-class") frame = frame with { ScopedWindows = frame.ScopedWindows!.Select(w => w with { ClassName = "Other" }).ToArray() };
        frame = frame with { RawStreams = frame.RawStreams!.Select(s => {
            if (mode == "missing-client" && s.Source == "msaa") return s with { Controls = [] };
            if (s.Source != "native-hwnd") return s;
            return mode switch {
                "no-native" => s with { Controls = [] },
                "one-anchor" => s with { Controls = [s.Controls[0]] },
                "wrong-window" => s with { TargetWindowHwnd = 3 },
                "ambiguous-scroll" => s with { Controls = [..s.Controls, s.Controls[1] with { RuntimeId = "extra", Bounds = s.Controls[1].Bounds with { X = 3313 } }] },
                "hidden-scroll" => s with { Controls = [s.Controls[0], s.Controls[1] with { IsOffscreen = true }] },
                "unknown-scope" => s with { Controls = s.Controls.Select(c => c with { ScopeStatus = "unverified" }).ToArray() },
                _ => s
            };
        }).ToArray() };
        Assert.Empty(RawOfficeCoordinates.Resolve(frame, 2));
    }

    [Fact]
    public void AttemptsQualifyIndependentlyAndAlreadyPhysicalProviderIsNotScaledTwice()
    {
        var frame = Frame();
        var projection = RawOfficeCoordinates.Resolve(frame, 2);
        var uia = frame.RawStreams!.Single(s => s.Source == "uia");
        frame = frame with { RawStreams = [..frame.RawStreams!.Where(s => s.Source != "uia"),
            uia with { Controls = uia.Controls.Select(c => projection.TryGetValue((uia.StreamId, c.RuntimeId), out var r) ? c with { Bounds = r.Display } : c).ToArray() },
            uia with { StreamId = "unrelated-attempt", Controls = uia.Controls.Where(c => c.RuntimeId != "root").ToArray() }] };
        Assert.All(RawOfficeCoordinates.Resolve(frame, 2).Keys, key => Assert.Equal("msaa", key.Stream));
    }

    [Fact]
    public void ActualRendererRegistersEveryRawModeAndCanShowLiteralReportedCoordinates()
    {
        MapperOverlayWindowTests.Sta(() =>
        {
            using var temp = new TempDirectory();
            var path = RawFrameEvidenceTests.Rewrite(SyntheticBundleFactory.Create(temp.Path, includeScreenshot: true), temp.Path,
                original => Frame() with { Sequence = original.Sequence, TimestampUtc = original.TimestampUtc, FrameEntry = original.FrameEntry });
            using var evidence = UiEvidenceReader.Open(path);
            var graph = new RecordingGraphBuilder().Build(path);
            // Atlas native projections show the real provider RuntimeId separately
            // from the sealed source record ID; display lookup must use provenance.
            graph = graph with { Nodes = graph.Nodes.Select(n => n with { Properties = n.Properties.Select(p =>
                p.Name == "runtimeId" ? p with { Value = "native-runtime-array" } : p).ToArray() }).ToArray() };
            var model = new UiMappingReadModel(graph);
            var dialog = model.LayerFor(UiUnderstandingLevel.RawDataStreams).Surfaces.Single(s => UiMappingReadModel.Property(s.Source, "hwnd") == "0x2" && s.Variants.Any(v => v.FrameSequence == 1));
            var viewer = new ExplorerWindow(null);
            try
            {
                Set("_model", model); Set("_evidence", evidence); Set("_level", UiUnderstandingLevel.RawDataStreams);
                Set("_inspectionLevel", UiUnderstandingLevel.RawDataStreams); Set("_selectedSurfaceScope", new[] { dialog });
                Set("_selectedSurface", dialog); Set("_selectedVariant", dialog.Variants[0]);
                foreach (var sources in new[] { (true, false), (false, true), (true, true), (false, false), (true, false) })
                foreach (var mode in new[] { "Window", "Controls", "Overlay", "Structure", "Structure Overlay" })
                {
                    Get<ToggleButton>("_showRawUia").IsChecked = sources.Item1;
                    Get<ToggleButton>("_showRawMsaa").IsChecked = sources.Item2;
                    Set("_viewMode", mode); Render();
                    Assert.Equal(new RectI(2921, 381, 418, 414), Get<RectI>("_renderedViewportBounds"));
                    var ok = Get<Canvas>("_appMapOverlay").Children.OfType<Rectangle>()
                        .Where(r => r.ToolTip?.ToString()?.StartsWith("OK •") == true).ToArray();
                    Assert.Equal(mode == "Window" ? 0 : (sources.Item1 ? 1 : 0) + (sources.Item2 ? 1 : 0), ok.Length);
                    foreach (var r in ok)
                    {
                        Assert.Equal(253, Canvas.GetLeft(r)); Assert.Equal(379, Canvas.GetTop(r));
                        Assert.Equal(75, r.Width); Assert.Equal(27, r.Height);
                        Assert.Contains("Reported:", r.ToolTip.ToString());
                        if (mode is "Controls" or "Overlay") Assert.IsType<ImageBrush>(r.Fill);
                    }
                }
                Get<CheckBox>("_showRawReportedCoordinates").IsChecked = true; Render();
                Assert.DoesNotContain(Get<Canvas>("_appMapOverlay").Children.OfType<Rectangle>(), r => r.ToolTip?.ToString()?.StartsWith("OK •") == true);
                Get<CheckBox>("_showRawReportedCoordinates").IsChecked = false; Render();
                Assert.Contains(Get<Canvas>("_appMapOverlay").Children.OfType<Rectangle>(), r => r.ToolTip?.ToString()?.StartsWith("OK •") == true);
                var rawEdit = model.LayerFor(UiUnderstandingLevel.RawDataStreams).Controls.First(c =>
                    UiMappingReadModel.Property(c.Source, "rawStreamId") == "uia" &&
                    c.Source.Observations?.Any(o => o.FrameSequence == 1 && o.Control.RuntimeId == "edit") == true);
                var semantic = rawEdit with { Level = UiUnderstandingLevel.SemanticWorld,
                    SourceSurfaceIds = new HashSet<string> { "source-dialog" },
                    ObservationSources = [rawEdit.Source with { Properties = [
                        new("rawSurfaceId", "source-dialog"), new("sourceRawDataStreamControlId", rawEdit.Id)] }] };
                var parameterVariant = dialog.Variants.First(v => v.FrameSequence == 1) with { NativeWindowHwnd = 2 };
                var frame = evidence.ReadObservation("golden", 1);
                Assert.Equal(new RectI(2937, 440, 312, 36), Assert.Single(ParameterReviewPresentation.Regions(
                    semantic, parameterVariant, model: model, frameEvidence: frame)));
                Assert.Empty(ParameterReviewPresentation.Regions(semantic, parameterVariant, model: model,
                    frameEvidence: frame with { RawStreams = frame.RawStreams!.Where(s => s.Source != "native-hwnd").ToArray() }));
                Assert.Equal(new RectI(3425, 1110, 151, 54), evidence.ReadObservation("golden", 1).RawStreams!.Single(s => s.Source == "uia").Controls.Single(c => c.RuntimeId == "ok").Bounds);
            }
            finally { viewer.Close(); }
            void Set(string field, object value) => viewer.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, value);
            T Get<T>(string field) => (T)viewer.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
            void Render() => viewer.GetType().GetMethod("RenderAppMap", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewer, null);
        });
    }

    private static FrameObservation Frame()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var main = new WindowObservation(1, 1, 7, "XLMAIN", "Excel", new(2880,381,1920,1032), true, false, false, false, 96);
        var dialog = main with { Hwnd = 2, OwnerHwnd = 1, ClassName = "bosa_sdm_XL9", Title = "Insert Function",
            Bounds = new(2921,381,418,414), Dpi = 192, IsEnabled = true, Style = 0x00C40000 };
        AutomationObservation Node(string id, string parent, string name, string type, RectI bounds, long native = 0, string cls = "") =>
            new(id, parent, "", name, type, cls, bounds, true, false, "", 2, QueryWindowHwnd: 2, NativeWindowHwnd: native, ScopeStatus: "verified");
        var edit = Node("native-edit", "", "", "Pane", new(2941,443,306,30), 22, "EDTBX");
        var scroll = Node("native-scroll", "", "", "ScrollBar", new(3314,539,17,103), 23, "ScrollBar");
        var controls = new[] {
            Node("root", "", "Insert Function", "Window", new(2915,381,430,420), 2),
            Node("edit", "root", "Search", "Edit", new(2952,470,624,72)),
            Node("list", "root", "Functions", "List", new(2952,664,792,214)),
            Node("item", "list", "SUM", "ListItem", new(2952,664,792,30)),
            Node("ok", "root", "OK", "Button", new(3425,1110,151,54)),
            Node("title", "root", "Title", "TitleBar", new(2922,388,417,23)),
            Node("close", "title", "Close", "Button", new(3292,382,47,29)),
            edit with { RuntimeId = "physical-edit", ParentRuntimeId = "root" },
            Node("physical-child", "physical-edit", "Text", "Text", edit.Bounds),
            Node("invalid", "root", "Unavailable", "Button", new(int.MaxValue,int.MaxValue,-1,-1)) };
        RawDataStreamObservation Stream(string source, params AutomationObservation[] nodes) =>
            new(source, source, now, now, "ok", nodes, TargetWindowHwnd: 2, ReturnedCount: nodes.Length);
        return new(1, now, "", main, [], false, "ok", "test", [main, dialog],
            ScreenshotBounds: dialog.Bounds, RawStreams: [Stream("native-hwnd", edit, scroll), Stream("uia", controls),
                Stream("msaa", controls.Select(c => c.RuntimeId == "root" ? c with { Bounds = new(2922,410,417,384), NativeWindowHwnd = 0 } : c).ToArray())]);
    }
}
