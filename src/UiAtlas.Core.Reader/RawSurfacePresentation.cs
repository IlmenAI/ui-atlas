using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Reader;

public sealed record RawSurfaceImageLayout(RectI SourcePixels, RectI WindowLocalBounds, string Coverage)
{
    public static RectI QueryViewport(RectI window, RectI image, IEnumerable<RectI> controlBounds)
    {
        var bounds = new[] { window }.Concat(controlBounds)
            .Where(ControlGeometry.IsRenderable)
            .Select(bound => RawObservationSemantics.Intersection(bound, image))
            .Where(bound => bound is not null).Cast<RectI>().ToArray();
        if (bounds.Length == 0) return window;
        var left = bounds.Min(bound => bound.X);
        var top = bounds.Min(bound => bound.Y);
        var right = bounds.Max(bound => (long)bound.X + bound.Width);
        var bottom = bounds.Max(bound => (long)bound.Y + bound.Height);
        return new(left, top, checked((int)(right - left)), checked((int)(bottom - top)));
    }

    public static IReadOnlyList<RectI> Occluders(FrameObservation frame, WindowObservation window) =>
        (frame.ScopedWindows ?? [frame.Window])
        .Where(other => other.Hwnd != window.Hwnd && other.IsVisible && !other.IsMinimized && !other.IsCloaked &&
            (other.ZOrder < window.ZOrder || other.OwnerHwnd == window.Hwnd))
        .Select(other => RawObservationSemantics.Intersection(window.Bounds, other.Bounds))
        .Where(bounds => bounds is not null).Cast<RectI>().ToArray();

    public static bool CanCropControl(UiEvidenceImage image, RectI bounds) =>
        !image.InheritedPixels && ControlGeometry.IsRenderable(bounds) &&
        RawObservationSemantics.Intersection(bounds, image.ScreenshotBounds) == bounds &&
        (image.DisplayBounds is null || RawObservationSemantics.Intersection(bounds, image.DisplayBounds) == bounds) &&
        !(image.OccludedRegions ?? []).Any(region => RawObservationSemantics.Intersection(bounds, region) is not null);

    public static RawSurfaceImageLayout? Create(RectI window, RectI image, int pixelWidth, int pixelHeight)
    {
        var overlap = RawObservationSemantics.Intersection(window, image);
        if (overlap is null || pixelWidth <= 0 || pixelHeight <= 0) return null;
        var scaleX = pixelWidth / (double)image.Width;
        var scaleY = pixelHeight / (double)image.Height;
        var left = Math.Clamp((int)Math.Floor(((long)overlap.X - image.X) * scaleX), 0, pixelWidth);
        var top = Math.Clamp((int)Math.Floor(((long)overlap.Y - image.Y) * scaleY), 0, pixelHeight);
        var right = Math.Clamp((int)Math.Ceiling(((long)overlap.X + overlap.Width - image.X) * scaleX), 0, pixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(((long)overlap.Y + overlap.Height - image.Y) * scaleY), 0, pixelHeight);
        if (right <= left || bottom <= top) return null;
        return new(new(left, top, right - left, bottom - top),
            overlap with { X = overlap.X - window.X, Y = overlap.Y - window.Y },
            RawObservationSemantics.Coverage(window, image));
    }
}

public static class RawVariantPresentation
{
    public static string ProviderSummary(IEnumerable<RawDataStreamObservation> streams) => string.Join(" · ",
        streams.Select(stream => $"{(ProviderRank(stream.Source) == 0 ? "UIA" : "MSAA")}: {stream.Status}, {stream.Controls.Count} records"));

    public static bool HasOutOfSurfaceGeometry(FrameObservation? frame, long hwnd, string? source)
    {
        var window = (frame?.ScopedWindows ?? (frame is null ? [] : [frame.Window])).FirstOrDefault(window => window.Hwnd == hwnd);
        if (window is null || RawObservationSemantics.NativeKind(window) != "dialog-window") return false;
        return StreamsForWindow(frame!, hwnd).Where(stream => ProviderRank(stream.Source) == ProviderRank(source))
            .Any(stream => AutomationObservationVisibility.FilterEffectivelyVisible(stream.Controls).Any(control =>
                control.ControlType is "Button" or "Edit" or "CheckBox" or "ControlType.Button" or "ControlType.Edit" or "ControlType.CheckBox" &&
                ControlGeometry.IsRenderable(control.Bounds) && RawObservationSemantics.Intersection(control.Bounds, window.Bounds) is null));
    }

    public static bool IsAccessibilitySource(string? source) => ProviderRank(source) < 2;

    public static bool IsSourceEnabled(string? source, bool uia, bool msaa) => ProviderRank(source) switch
    {
        0 => uia,
        1 => msaa,
        _ => false
    };

    private static int ProviderRank(string? source) => source switch
    {
        "uia" => 0,
        "msaa" => 1,
        not null when source.StartsWith("uia.", StringComparison.Ordinal) || source.StartsWith("uia/", StringComparison.Ordinal) => 0,
        not null when source.StartsWith("msaa/", StringComparison.Ordinal) => 1,
        _ => 2
    };

    public static RawDataStreamObservation? PreferredAttempt(IReadOnlyList<RawDataStreamObservation> streams) =>
        // UIA is the default even when empty or failed; MSAA is an explicit choice.
        streams.Where(stream => ProviderRank(stream.Source) == 0)
            .OrderBy(stream => stream.Status == "ok" ? 0 : 1)
            .ThenByDescending(stream => AutomationObservationVisibility.FilterEffectivelyVisible(stream.Controls)
                .Any(control => !string.IsNullOrEmpty(control.ParentRuntimeId) && ControlGeometry.IsRenderable(control.Bounds)))
            .ThenByDescending(stream => stream.Controls.Count).FirstOrDefault();

    public static bool ShouldUseControlCrop(UiMapControlView control, UiMapSurfaceView surface) =>
        // Static text, titles and small images are visible content too. Their
        // recorded pixels must survive disabling accessibility annotations.
        (surface.SurfaceKind is "RawTooltipWindow" or "SemanticTooltipWindow" ||
            !UiMapPresentation.IsLargeStructuralControl(control, surface)) &&
        ControlGeometry.IsRenderable(control.Bounds);

    public static string? ExclusionReason(FrameObservation frame, long hwnd)
    {
        var windows = frame.ScopedWindows ?? [frame.Window];
        var window = windows.FirstOrDefault(item => item.Hwnd == hwnd);
        if (window is null) return "Window is not present in this frame.";
        if (!window.IsVisible || window.IsMinimized || window.IsCloaked)
            return "Window was not visible.";
        static bool Standalone(WindowObservation item) => RawObservationSemantics.NativeKind(item)
            is "normal-window" or "dialog-window" or "tool-window";
        var front = windows.Where(item => item.ProcessId == window.ProcessId && item.IsVisible &&
            !item.IsMinimized && !item.IsCloaked && Standalone(item))
            .OrderBy(item => item.ZOrder).FirstOrDefault();
        // Menus belong to the immediate owner chain; never follow a root owner
        // through a foreground dialog back to a background application window.
        var owner = window;
        var visited = new HashSet<long>();
        while (!Standalone(owner) && visited.Add(owner.Hwnd) &&
               windows.FirstOrDefault(item => item.Hwnd == owner.OwnerHwnd && item.ProcessId == owner.ProcessId) is { } parent)
            owner = parent;
        if (front is not null && owner.Hwnd != front.Hwnd && front.ZOrder < owner.ZOrder)
            return $"Background window; {(string.IsNullOrEmpty(front.Title) ? front.ClassName : front.Title)} (0x{front.Hwnd:X}) is in front.";
        // Acquisition timing qualifies alignment, not whether a successful
        // provider response exists. Keep foreground evidence inspectable even
        // when another attempt was interrupted.
        // Image-only menus/tooltips are still valid raw surfaces. Normal windows
        // and dialogs need a usable native response, not just a root rectangle.
        if (Standalone(window) && !StreamsForWindow(frame, hwnd).Any(stream =>
                stream.Status is "ok" or "partial" && AutomationObservationVisibility.FilterEffectivelyVisible(stream.Controls)
                    .Any(control => control.WindowHwnd == hwnd && control.ScopeStatus is not ("outside-target" or "unverified") &&
                        !string.IsNullOrEmpty(control.ParentRuntimeId) && RawObservationSemantics.Intersection(control.Bounds, window.Bounds) is not null)))
            return "No visible native control records were returned for this window.";
        return null;
    }

    public static IReadOnlySet<string> VisibleControlIds(IEnumerable<UiMapControlView> controls)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attempt in controls.GroupBy(control => control.Source.Properties
                     .FirstOrDefault(property => property.Name == "rawStreamId")?.Value ?? "legacy"))
        {
            var facts = attempt.Select(control => (View: control, Fact: control.Source.Observations?.SingleOrDefault()?.Control)).ToArray();
            var visible = AutomationObservationVisibility.FilterEffectivelyVisible(facts.Where(item => item.Fact is not null)
                .Select(item => item.Fact!)).ToHashSet();
            foreach (var item in facts)
                if (item.Fact is not null ? visible.Contains(item.Fact) :
                    !item.View.Source.Properties.Any(property => property.Name is "offscreen" && property.Value == "True" ||
                        property.Name == "effectivelyVisible" && property.Value == "False")) result.Add(item.View.Id);
        }
        return result;
    }

    public static IReadOnlyList<RawDataStreamObservation> StreamsForWindow(FrameObservation frame, long hwnd) =>
        (frame.RawStreams ?? []).Where(stream =>
            IsAccessibilitySource(stream.Source) &&
            (stream.TargetWindowHwnd is { } target ? target == hwnd :
             stream.Source.EndsWith($"/0x{hwnd:X}", StringComparison.OrdinalIgnoreCase) ||
             stream.Controls.Any(control => (control.QueryWindowHwnd ?? control.WindowHwnd) == hwnd))).ToArray();

    public static string ScopeSummary(IEnumerable<AutomationObservation> controls, long hwnd)
    {
        var entries = controls.ToArray();
        var verified = entries.Count(control => control.QueryWindowHwnd == hwnd && control.ScopeStatus == "verified");
        var rejected = entries.Count(control => control.ScopeStatus is "outside-target" or "unverified" ||
            control.QueryWindowHwnd is { } target && target != hwnd);
        return $"Query target 0x{hwnd:X} • {verified} verified • {rejected} rejected/unresolved • " +
            $"{entries.Length - verified - rejected} without ownership verification";
    }

    public static UiMapProjectionPolicy PolicyFor(UiMapProjectionMode mode, FrameObservation? frame, bool showLabels = false) =>
        UiMapPresentation.PolicyFor(mode) with {
            ShowsControlLabels = showLabels && UiMapPresentation.PolicyFor(mode).ShowsControlGeometry,
            // Offline inspection can compare this frame's recorded pixels and
            // provider bounds without asserting that acquisition was atomic.
            // The evidence reader and crop geometry still enforce image scope.
            ShowsControlCrops = frame is not null && mode is UiMapProjectionMode.Controls or UiMapProjectionMode.Overlay };

    public static bool HasVerifiedAlignment(FrameObservation? frame) =>
        frame?.Acquisition is { StabilityStatus: "stable", Status: "ok" or "partial" };

    // Compatibility for callers that ask whether an overlay is verified.
    // Display eligibility is separate from this acquisition-quality predicate.
    public static bool AllowsOverlay(FrameObservation? frame) => HasVerifiedAlignment(frame);

    public static string? CaptureWarning(FrameObservation? frame) => HasVerifiedAlignment(frame) ? null :
        (frame?.Acquisition?.StabilityStatus == "changed"
            ? "The application changed during capture. " : "Capture timing was not verified. ") +
        "Crops and overlays compare this observation's recorded pixels and control bounds; their alignment is unverified. " +
        "Capture quality and provider records are unchanged.";

    public static UiMapVariantView? SelectionInScope(UiMapVariantView? current,
        IEnumerable<UiMapSurfaceView> surfaces) => current is null ? null : surfaces
        .SelectMany(surface => surface.Variants)
        .FirstOrDefault(variant => variant.Id == current.Id);

    public static IReadOnlyList<UiMapVariantView> Filter(IEnumerable<UiMapVariantView> variants,
        bool skipEmpty, bool includeSupplementary, Func<UiMapVariantView, string?>? exclusionReason = null) => variants
        .Where(variant => includeSupplementary || variant.RecordKind != "supplementary")
        .Where(variant => !skipEmpty || (exclusionReason is null ? variant.ControlCount > 0 : exclusionReason(variant) is null))
        .ToArray();
}
