using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Reader;

[Flags]
public enum ControlSource { None = 0, UIA = 1, MSAA = 2, OCR = 4, Other = 8 }

public sealed record ControlContributor(ControlSource Source, string Provider, GraphNode Record, RectI? Bounds);

public sealed record ControlSourceEvidence(ControlSource Sources, string BoundsSource, IReadOnlyList<ControlContributor> Contributors)
{
    public ControlSource BoundsProvider { get; init; } = ControlSource.Other;

    public IEnumerable<(ControlSource Source, RectI Bounds)> Outlines(RectI visible, ControlSource enabled)
    {
        if ((BoundsProvider & enabled) != 0) yield return (BoundsProvider, visible);
        foreach (var contributor in Contributors.Where(c => c.Source != BoundsProvider && (c.Source & enabled) != 0))
            if (contributor.Bounds is { } bounds && RawObservationSemantics.Intersection(bounds, visible) is { } clipped)
                yield return (contributor.Source, clipped);
    }
}

public sealed record PopupMenuSources(UiMapSurfaceView Surface, UiMapVariantView Frame, int Direct, int FromOtherWindows);

public sealed partial class UiMappingReadModel
{
    public PopupMenuSources? PopupMenuForObservation(string bundle, long sequence, long hwnd)
    {
        var matches = LayerFor(UiUnderstandingLevel.RawWorld).Surfaces.Where(s => s.SurfaceKind == "RawPopupWindow")
            .SelectMany(s => s.Variants.Where(f => f.BundleId == bundle && f.FrameSequence == sequence && f.NativeWindowHwnd == hwnd)
                .Select(f => (Surface: s, Frame: f))).Take(2).ToArray();
        if (matches.Length != 1) return null;
        var (surface, frame) = matches[0];
        var direct = 0;
        var other = 0;
        foreach (var id in frame.ControlIds)
        {
            if (!_nodes.TryGetValue(id, out var node) || (Property(node, "controlType") != "MenuItem" && Property(node, "sceneInferredRole") != "MenuItem") ||
                !node.Evidence.Any(e => e.BundleId == bundle && e.FrameSequence == sequence)) continue;
            var observed = UiMapPresentation.ForObservation(node, bundle, sequence);
            if (Property(observed, "rawSurfaceId") != surface.Id || Property(observed, "sceneReportedSurfaceRef") is not { Length: > 0 } reported) continue;
            if (reported == surface.Id) direct++;
            else other++;
        }
        return direct + other == 0 ? null : new(surface, frame, direct, other);
    }

    public ControlSourceEvidence SourcesFor(UiMapControlView control, UiMapVariantView? frame)
    {
        var sequence = frame?.ControlSourceFrames?.GetValueOrDefault(control.Id, frame.FrameSequence) ?? frame?.FrameSequence;
        EvidenceRef? Evidence(GraphNode n) => n.Evidence.FirstOrDefault(e => e.BundleId == frame?.BundleId && e.FrameSequence == sequence);
        var records = new Dictionary<string, ControlContributor>(StringComparer.Ordinal);
        var boundsSource = "Source unavailable";
        var boundsProvider = ControlSource.Other;
        var sources = ControlSource.None;
        void Add(GraphNode record, EvidenceRef evidence)
        {
            var (kind, name) = Provider(record);
            sources |= kind;
            records.TryAdd(record.Id, new(kind, name, record, evidence.Bounds));
        }
        var worlds = control.Level == UiUnderstandingLevel.SemanticWorld
            ? control.ObservationSources ?? control.Source.Properties.Where(p => p.Name == "sourceRawControlId")
                .Select(p => _nodes.GetValueOrDefault(p.Value)).OfType<GraphNode>().ToArray()
            : [control.Source];
        foreach (var world in worlds)
        {
            if (Evidence(world) is not { } observed) continue;
            var primary = Provider(world);
            if (primary.Kind != ControlSource.Other) sources |= primary.Kind;
            if (control.Level != UiUnderstandingLevel.SemanticWorld || Property(control.Source, "semanticObservationSource") == world.Id)
            {
                if (ControlGeometry.IsRenderable(observed.Bounds) && primary.Name != "Unspecified")
                {
                    var correction = Property(world, "sceneCoordinateCorrection");
                    boundsProvider = primary.Kind;
                    boundsSource = correction is null ? primary.Name : $"{primary.Name} ({correction})";
                }
            }
            var linked = false;
            foreach (var link in world.Properties.Where(p => p.Name == "sourceRawDataStreamControlId"))
                if (_nodes.TryGetValue(link.Value, out var raw) && Evidence(raw) is { } evidence)
                {
                    Add(raw, evidence);
                    linked = true;
                }
            if (!linked) Add(world, observed);
        }
        if (records.Count == 0 && Evidence(control.Source) is { } own) Add(control.Source, own);
        return new(sources == ControlSource.None ? ControlSource.Other : sources, boundsSource, records.Values.ToArray()) { BoundsProvider = boundsProvider };
    }

    private static (ControlSource Kind, string Name) Provider(GraphNode node)
    {
        var provider = Property(node, "rawStreamSource") ?? Property(node, "providerId");
        if (RawVariantPresentation.IsSourceEnabled(provider, true, false)) return (ControlSource.UIA, "UIA");
        if (RawVariantPresentation.IsSourceEnabled(provider, false, true)) return (ControlSource.MSAA, "MSAA");
        if (Property(node, "sourceMethod") == "local-ocr" || provider?.Contains("ocr", StringComparison.OrdinalIgnoreCase) == true)
            return (ControlSource.OCR, "OCR");
        return (ControlSource.Other, string.IsNullOrWhiteSpace(provider) ? "Unspecified" : provider);
    }
}
