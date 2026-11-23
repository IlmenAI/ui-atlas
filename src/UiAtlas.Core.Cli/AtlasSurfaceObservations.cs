using UiAtlas.Catalog;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

internal static class AtlasSurfaceObservations
{
    internal static CatalogSurfaceObservations Read(AtlasMapWorkspace workspace, UiMapRevision revision, string mapId, CancellationToken token)
    {
        var model = new UiMappingReadModel(AtlasMapProjection.ForRevision(workspace, revision, token));
        var layer = model.LayerFor(UiUnderstandingLevel.SemanticWorld);
        var pinned = revision.Sources.Select(s => s.SourceObservationSetRef).ToHashSet(StringComparer.Ordinal);
        var sources = workspace.Index.Sources.Where(s => pinned.Contains(s.SourceRef)).ToDictionary(s => (s.BundleId, s.Frame));
        var visibility = new Dictionary<(string Bundle, long Frame), SurfaceFrameVisibility>();
        foreach (var batch in sources.Values.GroupBy(s => s.BundleHash))
        {
            using var bundle = RecordingBundle.Open(workspace.Resolve("recordings/" + batch.Key + ".mlrec"));
            foreach (var source in batch)
            {
                token.ThrowIfCancellationRequested();
                visibility[(source.BundleId, source.Frame)] = SurfaceFrameVisibility.Read(bundle, source.Frame);
            }
        }
        var frames = new List<CatalogSurfaceFrame>();
        foreach (var surface in layer.Surfaces)
        {
            var controls = layer.Controls.Where(c => RawWorldPresentation.IsAvailableOnSurface(c, surface.Id)).ToDictionary(c => c.Id);
            var eligible = UiInspectionPresentation.IncludeEquivalentFrames(model.VariantsFor([surface]), surface.Variants);
            foreach (var group in UiInspectionPresentation.FrameGroups(eligible))
            foreach (var frame in group.Frames)
            {
                token.ThrowIfCancellationRequested();
                if (frame.NativeWindowHwnd is not { } hwnd || frame.Evidence?.Bounds is not { } bounds || !ControlGeometry.IsRenderable(bounds)) continue;
                var source = sources[(frame.BundleId, frame.FrameSequence)];
                var projected = frame.ControlIds.Where(controls.ContainsKey).Select(id => controls[id])
                    .Where(c => SurfaceFrameVisibility.ObservedControl(c, frame)).Select(c => UiMapPresentation.ForVariant(c, frame))
                    .Select(c => new CatalogSceneControl(c.Id, RawWorldPresentation.VisibleRegions(c)
                        .Where(ControlGeometry.IsRenderable).Select(Rect).ToArray(), RawVariantPresentation.ShouldUseControlCrop(c, surface))).ToArray();
                var exclusion = visibility[(source.BundleId, source.Frame)].Exclusion(hwnd, bounds);
                frames.Add(new(surface.Id, group.Id, group.Label, source.SourceRef, source.BundleId, source.BundleHash,
                    source.Frame, hwnd, Rect(bounds), projected, exclusion is null, exclusion));
            }
        }
        return new(mapId, revision.MapRevisionRef, Path.GetFileName(workspace.DirectoryPath), frames.ToArray());
    }

    private static CatalogSceneRect Rect(RectI r) => new(r.X, r.Y, r.Width, r.Height);
}
