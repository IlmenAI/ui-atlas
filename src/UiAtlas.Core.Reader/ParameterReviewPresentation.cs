using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Reader;

public static class ParameterReviewPresentation
{
    public static IReadOnlyList<RectI> Regions(UiMapControlView? control, UiMapVariantView? variant,
        IReadOnlyList<UiMapControlView>? peers = null, UiMappingReadModel? model = null, FrameObservation? frameEvidence = null)
    {
        if (control is null || variant is null ||
            !control.Evidence.Any(e => e.BundleId == variant.BundleId && e.FrameSequence == variant.FrameSequence) ||
            variant.ControlSourceFrames?.TryGetValue(control.Id, out var frame) == true && frame != variant.FrameSequence)
            return [];
        if (HasMoreSpecificOwner(control, variant, peers)) return [];
        var observed = UiMapPresentation.ForVariant(control, variant);
        if (observed.Source.Properties.Any(p => p.Name is "offscreen" or "isOffscreen" or "sceneOffscreen" &&
                p.Value.Equals("true", StringComparison.OrdinalIgnoreCase))) return [];
        if (observed.Source.Properties.Any(p => p.Name == "semanticObservationDiagnostic")) return [];
        if (model is not null && frameEvidence is not null && variant.NativeWindowHwnd is { } hwnd &&
            frameEvidence.Sequence == variant.FrameSequence)
        {
            var sources = (control.ObservationSources ?? []).Where(n => n.Evidence.Any(e =>
                e.BundleId == variant.BundleId && e.FrameSequence == variant.FrameSequence)).ToArray();
            var rawIds = sources.Select(n => UiMappingReadModel.Property(n, "sourceRawDataStreamControlId")).ToHashSet();
            var rawControls = model.LayerFor(UiUnderstandingLevel.RawDataStreams).Controls.Where(c => rawIds.Contains(c.Id))
                .Select(c => UiMapPresentation.ForVariant(c, variant)).ToArray();
            var registrations = RawOfficeCoordinates.Resolve(frameEvidence, hwnd);
            var matches = rawControls.Select(c => RawOfficeCoordinates.ForControl(registrations, c)).ToArray();
            if (matches.Any(r => r is not null))
            {
                // Never combine registered and unregistered interpretations of one target.
                if (matches.Any(r => r is null) || matches.Select(r => r!.Display).Distinct().Count() != 1) return [];
                var window = (frameEvidence.ScopedWindows ?? [frameEvidence.Window]).SingleOrDefault(w => w.Hwnd == hwnd);
                var registered = window is null ? null : RawObservationSemantics.Intersection(matches[0]!.Display, window.Bounds);
                if (registered is null || RawSurfaceImageLayout.Occluders(frameEvidence, window!).Any(r =>
                    RawObservationSemantics.Intersection(r, registered) is not null)) return [];
                return [registered];
            }
            if (rawControls.Any(c => RawVariantPresentation.HasOutOfSurfaceGeometry(frameEvidence, hwnd,
                UiMappingReadModel.Property(c.Source, "rawStreamSource")))) return [];
        }
        return RawWorldPresentation.VisibleRegions(observed).Where(ControlGeometry.IsRenderable).ToArray();
    }

    public static UiMapVariantView? Variant(UiMapControlView control, IReadOnlyList<UiMapVariantView> variants,
        IReadOnlyList<UiMapControlView>? peers = null) =>
        UiMapPresentation.ResolveControlVariant(control, variants.Where(v =>
            Regions(control, v, peers).Count > 0 && !string.IsNullOrWhiteSpace(v.Evidence?.ScreenshotEntry)).ToArray())
        ?? UiMapPresentation.ResolveControlVariant(control, variants);

    // Legacy owner-window queries sometimes retained a dialog descendant as well
    // as the dialog's own native-window query. Require identical provider identity,
    // process scope and observation; labels or overlapping geometry are not enough.
    public static bool HasMoreSpecificOwner(UiMapControlView control, UiMapVariantView variant,
        IReadOnlyList<UiMapControlView>? peers)
    {
        if (peers is null || control.ObservationSources is null) return false;
        static string P(GraphNode n, string key) => UiMappingReadModel.Property(n, key) ?? "";
        bool InFrame(GraphNode n) => n.Evidence.Any(e => e.BundleId == variant.BundleId && e.FrameSequence == variant.FrameSequence);
        static string Part(string scope, string key) => scope.Split('|').FirstOrDefault(p => p.StartsWith(key, StringComparison.Ordinal))?[key.Length..] ?? "";
        foreach (var source in control.ObservationSources.Where(InFrame))
        {
            var identity = P(source, "runtimeIdentityEvidence");
            var provider = P(source, "providerId");
            var scope = P(source, "windowScopeRef");
            var hwnd = Part(scope, "hwnd:");
            var process = scope.Split("|hwnd:", StringSplitOptions.None)[0];
            if (identity.Length == 0 || provider.Length == 0 || hwnd.Length == 0 ||
                !process.StartsWith("pid:", StringComparison.Ordinal) || Part(scope, "root:") != hwnd) continue;
            if (peers.Where(c => c.OwnerSurfaceId != control.OwnerSurfaceId)
                .SelectMany(c => c.ObservationSources ?? []).Where(InFrame).Any(other =>
                {
                    var otherScope = P(other, "windowScopeRef");
                    return P(other, "runtimeIdentityEvidence") == identity && P(other, "providerId") == provider &&
                        otherScope.StartsWith(process + "|hwnd:", StringComparison.Ordinal) &&
                        Part(otherScope, "root:") == hwnd && Part(otherScope, "hwnd:").Length > 0 &&
                        Part(otherScope, "hwnd:") != hwnd;
                })) return true;
        }
        return false;
    }
}
