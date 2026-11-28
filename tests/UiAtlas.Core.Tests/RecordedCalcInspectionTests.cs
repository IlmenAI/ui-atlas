using System.Reflection;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using UiAtlas.Core.Desktop;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Reader;

namespace UiAtlas.Core.Tests;

[Collection("Mapper desktop")]
public sealed class RecordedCalcInspectionTests
{
    private sealed class CalcMapFactAttribute : FactAttribute
    {
        public CalcMapFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("UIATLAS_CALC_INSPECTION_MAP") is null)
                Skip = "Set UIATLAS_CALC_INSPECTION_MAP to the retained three-recording Calc map.";
        }
    }

    [CalcMapFact]
    public void Explorer_replays_dialogs_popup_ownership_and_source_filters_from_the_recorded_map()
    {
        var workspace = AtlasMapWorkspace.Open(Environment.GetEnvironmentVariable("UIATLAS_CALC_INSPECTION_MAP")!);
        var model = new UiMappingReadModel(workspace.Project());
        using var reader = UiEvidenceReader.Open(workspace.Index.BundleFiles.Select(workspace.Resolve));
        MapperOverlayWindowTests.Sta(() =>
        {
            var editor = new ExplorerWindow(null);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            T Field<T>(string name) => (T)typeof(ExplorerWindow).GetField(name, flags)!.GetValue(editor)!;
            void Set(string name, object value) => typeof(ExplorerWindow).GetField(name, flags)!.SetValue(editor, value);
            void Call(string name, params object?[] args) => typeof(ExplorerWindow).GetMethod(name, flags)!.Invoke(editor, args);
            var shapes = Field<Canvas>("_appMapOverlay").Children;
            var image = Field<Image>("_appMapImage");
            var toggles = Field<Dictionary<ControlSource, ToggleButton>>("_worldSources");
            Set("_model", model);
            Set("_evidence", reader);
            Set("_viewMode", "Overlay");
            try
            {
                var family = Assert.Single(model.BuildPipeline(UiUnderstandingLevel.RawWorld).Nodes,
                    n => n.Kind == UiPipelineNodeKind.RawSurface && n.DisplayName == "Format Cells");
                Assert.True(model.ResolvePipelineSurfaces(family).Count >= 2);
                foreach (var level in new[] { UiUnderstandingLevel.RawWorld, UiUnderstandingLevel.SemanticWorld })
                {
                    var layer = model.LayerFor(level);
                    var frames = new HashSet<(long?, long)>();
                    var pages = new HashSet<string>();
                    foreach (var surface in layer.Surfaces.Where(s => s.DisplayName == "Format Cells"))
                    {
                        Call("SelectSurface", surface, null);
                        foreach (var variant in Field<IReadOnlyList<UiMapVariantView>>("_visibleVariants"))
                        {
                            Call("SelectVariant", variant);
                            frames.Add((variant.NativeWindowHwnd, variant.FrameSequence));
                            var preview = reader.SurfacePreview(variant);
                            if (preview.SelectedPage is { } page) pages.Add(page);
                            if (variant.NativeWindowHwnd is 0x15611B6 or 0x1231566)
                                Assert.Contains("full window order", reader.SurfaceExclusion(variant));
                            Assert.True(preview.HasWindowImage || preview.CanMaskComposite || preview.DesktopExclusion is null);
                            var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                            Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0);
                            Assert.NotEmpty(shapes.OfType<Rectangle>());
                            Assert.True(toggles.Values.All(t => t.Visibility == System.Windows.Visibility.Visible));
                        }
                    }
                    Assert.Contains(((long?)0x15611B6, 9L), frames);
                    Assert.Contains(((long?)0x1231566, 3L), frames);
                    Assert.Contains("Numbers", pages);
                    Assert.Contains("Alignment", pages);
                    foreach (var toggle in toggles.Values) toggle.IsChecked = false;
                    Assert.Empty(shapes.OfType<Rectangle>());
                    toggles[ControlSource.UIA].IsChecked = true;
                    Assert.NotEmpty(shapes.OfType<Rectangle>());
                    Assert.All(shapes.OfType<Rectangle>(), shape => Assert.Contains("UIA", shape.ToolTip.ToString()));
                    foreach (var toggle in toggles.Values) toggle.IsChecked = true;
                    var wizard = Assert.Single(layer.Surfaces, s => s.DisplayName == "Function Wizard" && !s.SurfaceKind.Contains("Popup"));
                    Call("SelectSurface", wizard, null);
                    Assert.Equal(25, Field<UiMapVariantView>("_selectedVariant").FrameSequence);
                    var helper = Assert.Single(layer.Surfaces, s => s.DisplayName == "Function Wizard" && s.SurfaceKind.Contains("Popup"));
                    Assert.Contains("popup · 92 × 22", UiMappingReadModel.SurfaceNavigationLabel(helper));
                    var browse = typeof(ExplorerWindow).GetMethod("BrowseSurface", flags)!;
                    Assert.False((bool)browse.Invoke(editor, [helper])!);
                    Assert.True((bool)browse.Invoke(editor, [wizard])!);
                    Call("SelectSurface", helper, null);
                    var limited = Field<IReadOnlyList<UiMapVariantView>>("_visibleVariants");
                    Assert.Equal(new long[] { 23, 24 }, limited.Select(f => f.FrameSequence));
                    foreach (var frame in limited)
                    {
                        Call("SelectVariant", frame);
                        Assert.Null(image.Source);
                        Assert.Contains("changed", Assert.IsType<TextBlock>(Field<ContentControl>("_traceBannerHost").Content).Text);
                    }
                }
                var world = model.LayerFor(UiUnderstandingLevel.RawWorld);
                foreach (var (name, ownerReported) in new[] { ("Open Remote...", true), ("Data Validation...", false) })
                {
                    var command = world.Controls.First(c => c.DisplayName == name && c.CanonicalKind == "MenuItem" &&
                        c.Evidence.Any(e => e.FrameSequence == 2) && c.Source.Properties.Any(p => p.Name == "providerId" && p.Value == "uia") &&
                        c.Source.Properties.Any(p => p.Name == "sceneReportedSurfaceRef" && (p.Value != c.OwnerSurfaceId) == ownerReported));
                    var surface = world.Surfaces.Single(s => s.Id == command.OwnerSurfaceId);
                    Assert.Contains("Popup", surface.SurfaceKind);
                    var variant = surface.Variants.First(f => f.FrameSequence == 2 && f.ControlIds.Contains(command.Id));
                    var source = model.SourcesFor(UiMapPresentation.ForVariant(command, variant), variant);
                    Assert.Equal("UIA", source.BoundsSource);
                    Assert.Contains(source.Contributors, item => item.Source == ControlSource.UIA &&
                        item.Record.Properties.Any(p => p.Name == "queryWindowHwnd" && (p.Value != $"{variant.NativeWindowHwnd:X}") == ownerReported));
                    var hwnd = variant.NativeWindowHwnd!.Value;
                    var menu = Assert.IsType<PopupMenuSources>(model.PopupMenuForObservation(variant.BundleId, variant.FrameSequence, hwnd));
                    Assert.True((ownerReported ? menu.FromOtherWindows : menu.Direct) > 0);
                    var rawSurface = Assert.Single(model.LayerFor(UiUnderstandingLevel.RawDataStreams).Surfaces,
                        s => s.Variants.Any(f => f.BundleId == variant.BundleId && f.FrameSequence == variant.FrameSequence) &&
                             RawWorldPresentation.WindowHandle(s.Source) == hwnd);
                    Call("SelectSurface", rawSurface, null);
                    Call("SelectVariant", rawSurface.Variants.Single(f => f.BundleId == variant.BundleId && f.FrameSequence == variant.FrameSequence));
                    var banner = Assert.IsType<StackPanel>(Field<ContentControl>("_traceBannerHost").Content);
                    var link = Assert.Single(banner.Children.OfType<Button>(), b => b.Content.ToString()!.StartsWith("Inspect menu in Raw World"));
                    link.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(surface.Id, Field<UiMapSurfaceView>("_selectedSurface").Id);
                    Assert.Equal(variant.Id, Field<UiMapVariantView>("_selectedVariant").Id);
                }
            }
            finally { editor.Close(); }
        });
    }
}
