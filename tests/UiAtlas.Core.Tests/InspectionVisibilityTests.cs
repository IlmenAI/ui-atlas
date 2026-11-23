using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Desktop;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Tests;

[Collection("Mapper desktop")]
public sealed class InspectionVisibilityTests
{
    [Fact]
    public void Visibility_requires_same_frame_pixels_verified_topology_and_full_coverage()
    {
        using var temp = new TempDirectory();
        var sample = SyntheticBundleFactory.Create(temp.Path, includeScreenshot: true);
        using var source = RecordingBundle.Open(sample);
        var frame = source.ReadJson<FrameObservation>("raw/observations/frame-000001.json");
        var window = frame.Window with { ZOrder = 2 };
        frame = frame with { Window = window, ScopedWindows = [window], ScreenshotBounds = window.Bounds,
            Acquisition = new("raw-observation/2", "fixture", frame.TimestampUtc, frame.TimestampUtc, "ok", "fixture", "stable",
                WindowCoverage: [new(window.Hwnd, window.Bounds, "full")]) };
        string? Reason(FrameObservation f)
        {
            using var bundle = RecordingBundle.Open(RawFrameEvidenceTests.Rewrite(sample, temp.Path, original => original.Sequence == 1 ? f : original));
            return SurfaceFrameVisibility.Read(bundle, 1).Exclusion(window.Hwnd, window.Bounds);
        }
        Assert.Null(Reason(frame with { Automation = [] }));
        var popup = window with { Hwnd = 99, OwnerHwnd = window.Hwnd, ZOrder = 1, Title = "Guest Cards",
            Bounds = window.Bounds with { Width = 10, Height = 10 } };
        Assert.Contains("Guest Cards", Reason(frame with { ScopedWindows = [window, popup] }));
        Assert.Null(Reason(frame with { ScopedWindows = [window, popup with { IsVisible = false }] }));
        Assert.NotNull(Reason(frame with { Window = window with { IsMinimized = true }, ScopedWindows = [window with { IsMinimized = true }] }));
        Assert.NotNull(Reason(frame with { ScreenshotBounds = window.Bounds with { Width = 10 } }));
        Assert.NotNull(Reason(frame with { FrameEntry = "" }));
        Assert.Contains("changed", Reason(frame with { Acquisition = frame.Acquisition! with { StabilityStatus = "changed" } }));
        var atLimit = Enumerable.Range(1, RecordingContractLimits.MaxScopedWindows - 1)
            .Select(i => popup with { Hwnd = window.Hwnd + i, IsVisible = false }).Prepend(window).ToArray();
        Assert.Contains("full window order", Reason(frame with { ScopedWindows = atLimit, Acquisition = frame.Acquisition! with { Status = "partial" } }));
        Assert.NotNull(Reason(frame with { Acquisition = null }));
        Assert.NotNull(Reason(frame with { ScopedWindows = null }));
    }

    private sealed class NewBookingFactAttribute : FactAttribute
    {
        public NewBookingFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("MAPLOCK_PERMISSION_MAP_SAMPLE") is null)
                Skip = "Set the retained New Booking map and catalog fixture paths to verify both editor levels.";
        }
    }

    [NewBookingFact]
    public void Retained_new_booking_switches_only_to_clear_frames_in_both_worlds()
    {
        var graphPath = Path.Combine(Environment.GetEnvironmentVariable("MAPLOCK_PERMISSION_SAMPLE_ATLAS")!, "maps",
            Environment.GetEnvironmentVariable("MAPLOCK_PERMISSION_SAMPLE_MAP_ID") + ".db");
        var revision = UiMapRevision.Parser.ParseFrom(File.ReadAllBytes(Environment.GetEnvironmentVariable("MAPLOCK_PERMISSION_MAP_SAMPLE")!));
        Assert.Equal("04f6afc6745f2e625d67d1f26cb89249cbe9cc33f539bfd01fcdbd5f277bd829", revision.CanonicalPayloadHash);
        var workspace = AtlasMapWorkspace.Open(graphPath);
        var model = new UiMappingReadModel(AtlasMapProjection.ForRevision(workspace, revision, default));
        using var reader = UiEvidenceReader.Open(workspace.Index.BundleFiles.Select(workspace.Resolve));
        MapperOverlayWindowTests.Sta(() =>
        {
            var editor = new ExplorerWindow(null);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            T Field<T>(string name) => (T)typeof(ExplorerWindow).GetField(name, flags)!.GetValue(editor)!;
            void Call(string name, params object?[] args) => typeof(ExplorerWindow).GetMethod(name, flags)!.Invoke(editor, args);
            typeof(ExplorerWindow).GetField("_model", flags)!.SetValue(editor, model);
            typeof(ExplorerWindow).GetField("_evidence", flags)!.SetValue(editor, reader);
            try
            {
                foreach (var level in new[] { UiUnderstandingLevel.RawWorld, UiUnderstandingLevel.SemanticWorld })
                {
                    var surface = Assert.Single(model.LayerFor(level).Surfaces, s => s.Variants.Any(f =>
                        f.NativeWindowHwnd == 0x419D4 && f.FrameSequence == 4));
                    var covered = Assert.Single(surface.Variants, f => f.NativeWindowHwnd == 0x419D4 && f.FrameSequence == 1);
                    Assert.Equal(level == UiUnderstandingLevel.RawWorld ? 47 : 46, covered.ControlCount);
                    Assert.Contains("Guest Cards", reader.SurfaceExclusion(covered));
                    Call("SelectSurface", surface, null);
                    var clear = Field<UiMapVariantView>("_selectedVariant");
                    Assert.Equal(4, clear.FrameSequence);
                    var image = Assert.IsAssignableFrom<BitmapSource>(Field<Image>("_appMapImage").Source);
                    Assert.Equal((668, 466), (image.PixelWidth, image.PixelHeight));
                    Assert.DoesNotContain(covered, Field<IReadOnlyList<UiMapVariantView>>("_visibleVariants"));
                    if (level == UiUnderstandingLevel.SemanticWorld)
                    {
                        var variants = Field<ListBox>("_inspectionVariantSelector");
                        variants.SelectedItem = Assert.Single(variants.Items.OfType<UiInspectionFrameGroup>(), g => g.Frames.Count == 0);
                        Assert.Null(Field<Image>("_appMapImage").Source);
                        Assert.Empty(Field<Canvas>("_appMapOverlay").Children);
                        Assert.Contains("Guest Cards", Field<string>("_inspectionUnavailableVariant"));
                        variants.SelectedItem = variants.Items.OfType<UiInspectionFrameGroup>().Single(g => g.Frames.Contains(clear));
                        Assert.NotNull(Field<Image>("_appMapImage").Source);
                    }
                }
            }
            finally { editor.Close(); }
        });
    }
}
