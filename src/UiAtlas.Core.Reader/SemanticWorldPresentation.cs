using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Reader;

/// <summary>Projects semantic meaning onto one exact upstream scene observation.</summary>
public static class SemanticWorldPresentation
{
    private static string P(GraphNode n, string key) => UiMappingReadModel.Property(n, key) ?? "";

    public static UiMapControlView ForObservation(UiMapControlView control, string? bundle, long? frame)
    {
        if (control.Level != UiUnderstandingLevel.SemanticWorld || control.ObservationSources is null) return control;
        var candidates = control.ObservationSources.Where(n => n.Evidence.Any(e => e.BundleId == bundle && e.FrameSequence == frame)).ToArray();
        var properties = control.Source.Properties.Where(p => !p.Name.StartsWith("scene", StringComparison.Ordinal)
            && p.Name is not ("semanticObservationSource" or "semanticObservationDiagnostic")).ToList();
        properties.Add(new("sceneReconstruction", "semantic-source/1"));
        string? diagnostic = null;
        if (P(control.Source, "semanticEntityKind") == "MapGridColumn")
            diagnostic = "Reviewed column schema; host evidence does not establish a column control rectangle.";
        else if (candidates.Length == 0) diagnostic = "No Raw World control evidence for this observation.";
        else if (candidates.Any(n => control.SourceSurfaceIds?.Contains(P(n, "rawSurfaceId")) != true))
            diagnostic = "Semantic owner differs from the observed Raw World owner; assignment retained for review, geometry withheld.";
        else if (candidates.Select(n => (Bounds(n, bundle, frame), P(n, "sceneVisibility"), P(n, "sceneOffscreen")))
            .Distinct().Count() > 1)
            diagnostic = "Multiple Raw World interpretations disagree in this observation; geometry withheld pending review.";
        if (diagnostic is not null)
        {
            properties.Add(new("semanticObservationDiagnostic", diagnostic));
            return control with { Source = control.Source with { Properties = properties }, Bounds = new(0, 0, 0, 0) };
        }
        var observed = candidates[0];
        properties.AddRange(observed.Properties.Where(p => p.Name.StartsWith("scene", StringComparison.Ordinal)
            && p.Name != "sceneReconstruction"));
        properties.AddRange(observed.Properties.Where(p => p.Name is "automationId" or "frameworkId" or "providerId" or
            "supportedPattern" or "keyboardShortcut" or "accessKey" or "acceleratorKey"));
        properties.Add(new("semanticObservationSource", observed.Id));
        // Old, non-reconstructed source graphs remain explicitly limited. They
        // can supply geometry only when they report the control as visible.
        var bounds = Bounds(observed, bundle, frame);
        if (!observed.Properties.Any(p => p.Name == "sceneReconstruction") && bounds.IsValid &&
            P(observed, "offscreen") != "True" && P(observed, "isOffscreen") != "True")
            properties.Add(new("sceneVisibleRegion", $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}"));
        return control with { Source = control.Source with { Properties = properties }, Bounds = bounds };
    }

    private static RectI Bounds(GraphNode node, string? bundle, long? frame) =>
        node.Evidence.FirstOrDefault(e => e.BundleId == bundle && e.FrameSequence == frame)?.Bounds ?? new(0, 0, 0, 0);

    public static string EvidenceStatus(UiMapSurfaceView surface, UiMapVariantView? variant)
    {
        if (variant is null) return "No selected observation";
        if (variant.NativeWindowHwnd is null) return "Limited: exact source window is unresolved";
        if (variant.Evidence?.Bounds is not { } bounds || !ControlGeometry.IsRenderable(bounds)) return "Limited: surface bounds are missing";
        return "Same-observation Raw World evidence; capture coverage and timing still apply";
    }
}
