using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Reader;

public sealed record SurfacePreviewEvidence(string? DesktopExclusion, bool HasWindowImage, bool Stable, bool CanMaskComposite = false)
{
    public string? SelectedPage { get; init; }
    public bool CanAlignControls => DesktopExclusion is null || HasWindowImage && Stable;
    public int Preference => DesktopExclusion is null ? 0 : CanAlignControls ? 1 : HasWindowImage ? 2 : 3;
    public static readonly SurfacePreviewEvidence Unavailable = new(SurfaceFrameVisibility.Unknown, false, false);
}

/// <summary>Visibility of each window in the recorded application scene. Never consults a different frame.</summary>
public sealed class SurfaceFrameVisibility
{
    public const string Unknown = "The recording does not establish a clear view of this surface.";
    private readonly Dictionary<long, (RectI Bounds, string? Reason)> windows = [];
    private readonly Dictionary<long, RectI> independentImages = [];
    private readonly HashSet<long> maskedComposite = [];
    private readonly Dictionary<long, string> selectedPages = [];
    private bool stable;

    private readonly string unavailable;

    private SurfaceFrameVisibility(string unavailable = Unknown) => this.unavailable = unavailable;

    public static bool ObservedControl(UiMapControlView control, UiMapVariantView frame) =>
        control.Evidence.Any(e => e.BundleId == frame.BundleId && e.FrameSequence == frame.FrameSequence) &&
        (frame.ControlSourceFrames is null || frame.ControlSourceFrames.TryGetValue(control.Id, out var sequence) && sequence == frame.FrameSequence);

    public string? Exclusion(long? hwnd, RectI? bounds) =>
        hwnd is { } handle && windows.TryGetValue(handle, out var entry) && entry.Bounds == bounds
            ? entry.Reason : unavailable;

    public SurfacePreviewEvidence Preview(long? hwnd, RectI? bounds) => new(Exclusion(hwnd, bounds),
        hwnd is { } handle && independentImages.TryGetValue(handle, out var imageBounds) && imageBounds == bounds, stable,
        hwnd is { } h && maskedComposite.Contains(h) && windows[h].Bounds == bounds) { SelectedPage = hwnd is { } id ? selectedPages.GetValueOrDefault(id) : null };

    public static SurfaceFrameVisibility Read(RecordingBundle bundle, long sequence)
    {
        var observation = $"raw/observations/frame-{sequence:D6}.json";
        var screenshot = $"raw/frames/frame-{sequence:D6}.png";
        if (!bundle.Entries.Contains(observation)) return new();
        var frame = bundle.ReadJson<FrameObservation>(observation);
        if (frame.Sequence != sequence) return new();
        var result = frame.FrameEntry == screenshot && bundle.Entries.Contains(screenshot) ? From(frame) : new();
        result.stable = RawVariantPresentation.HasVerifiedAlignment(frame);
        foreach (var window in (frame.ScopedWindows ?? [frame.Window]).Where(w => w.OwnerHwnd != 0))
        {
            // Owned dialogs can expose their page selector as tabs or as a tall side list.
            var direct = frame.Automation.Where(c => (c.QueryWindowHwnd ?? c.WindowHwnd) == window.Hwnd &&
                !c.IsOffscreen && c.ProviderId == "uia").ToArray();
            var sideLists = direct.Where(c => c.ControlType is "List" or "ControlType.List" &&
                c.Bounds.Height > window.Bounds.Height / 2 && c.Bounds.Width < window.Bounds.Width / 2 &&
                c.Bounds.X >= window.Bounds.X && c.Bounds.X - window.Bounds.X < window.Bounds.Width / 10)
                .Select(c => c.RuntimeId).ToHashSet();
            var selected = direct.Where(c => c.IsSelected && !string.IsNullOrWhiteSpace(c.Name) &&
                (c.ControlType is "TabItem" or "ControlType.TabItem" ||
                 c.ControlType is "ListItem" or "ControlType.ListItem" && sideLists.Contains(c.ParentRuntimeId)))
                .Select(c => c.Name).Distinct().Take(2).ToArray();
            if (selected.Length == 1) result.selectedPages[window.Hwnd] = selected[0];
        }
        foreach (var plane in frame.WindowImages ?? [])
        {
            if (plane.Status == "ok" && plane.Entry == $"raw/frames/frame-{sequence:D6}-window-{plane.Hwnd:X}.png" &&
                bundle.Entries.Contains(plane.Entry) && ControlGeometry.IsRenderable(plane.Bounds) &&
                (frame.ScopedWindows ?? [frame.Window]).Any(w => w.Hwnd == plane.Hwnd && w.Bounds == plane.Bounds))
                result.independentImages[plane.Hwnd] = plane.Bounds;
        }
        return result;
    }

    internal static SurfaceFrameVisibility From(FrameObservation frame)
    {
        if (frame.Acquisition?.StabilityStatus == "changed")
            return new("The application changed while this frame was being recorded.");
        if (frame.ScopedWindows is not { Count: > 0 } scoped || frame.ScreenshotBounds is not { } pixels ||
            !RawVariantPresentation.HasVerifiedAlignment(frame)) return new();
        if (scoped.Count >= RecordingContractLimits.MaxScopedWindows && frame.Acquisition?.Status == "partial")
            return new("The recording could not confirm the full window order for this frame.");
        var result = new SurfaceFrameVisibility();
        foreach (var window in scoped)
        {
            var reason = RawVariantPresentation.ForegroundExclusion(scoped, window);
            if (reason is null)
            {
                var overlap = scoped.Where(other => other.Hwnd != window.Hwnd && other.IsVisible && !other.IsCloaked &&
                    !other.IsMinimized && RawObservationSemantics.Intersection(window.Bounds, other.Bounds) is not null).ToArray();
                var cover = overlap.FirstOrDefault(other => other.ZOrder < window.ZOrder || other.OwnerHwnd == window.Hwnd);
                if (cover is not null)
                    reason = $"{(string.IsNullOrWhiteSpace(cover.Title) ? "Another window" : cover.Title)} covers this surface.";
                else if (overlap.Any(other => other.ZOrder == window.ZOrder) || !ControlGeometry.IsRenderable(window.Bounds) ||
                    RawObservationSemantics.Intersection(window.Bounds, pixels) != window.Bounds ||
                    frame.Acquisition?.WindowCoverage?.Any(c => c.Hwnd == window.Hwnd && c.Bounds == window.Bounds && c.Pixels == "full") != true)
                    reason = Unknown;
            }
            result.windows[window.Hwnd] = (window.Bounds, reason);
            if (reason is not null && window.IsVisible && !window.IsMinimized && !window.IsCloaked &&
                RawObservationSemantics.Intersection(window.Bounds, pixels) == window.Bounds &&
                frame.Acquisition?.WindowCoverage?.Any(c => c.Hwnd == window.Hwnd && c.Bounds == window.Bounds && c.Pixels == "full") == true &&
                !scoped.Any(other => other.Hwnd != window.Hwnd && other.IsVisible && other.ZOrder == window.ZOrder &&
                    RawObservationSemantics.Intersection(other.Bounds, window.Bounds) is not null) &&
                RawSurfaceImageLayout.Occluders(frame, window).Count > 0)
                result.maskedComposite.Add(window.Hwnd);
        }
        return result;
    }
}
