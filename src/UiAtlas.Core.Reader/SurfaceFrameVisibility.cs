using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Reader;

/// <summary>Visibility of each window in the recorded application scene. Never consults a different frame.</summary>
public sealed class SurfaceFrameVisibility
{
    public const string Unknown = "The recording does not establish a clear view of this surface.";
    private readonly Dictionary<long, (RectI Bounds, string? Reason)> windows = [];

    private readonly string unavailable;

    private SurfaceFrameVisibility(string unavailable = Unknown) => this.unavailable = unavailable;

    public static bool ObservedControl(UiMapControlView control, UiMapVariantView frame) =>
        control.Evidence.Any(e => e.BundleId == frame.BundleId && e.FrameSequence == frame.FrameSequence) &&
        (frame.ControlSourceFrames is null || frame.ControlSourceFrames.TryGetValue(control.Id, out var sequence) && sequence == frame.FrameSequence);

    public string? Exclusion(long? hwnd, RectI? bounds) =>
        hwnd is { } handle && windows.TryGetValue(handle, out var entry) && entry.Bounds == bounds
            ? entry.Reason : unavailable;

    public static SurfaceFrameVisibility Read(RecordingBundle bundle, long sequence)
    {
        var observation = $"raw/observations/frame-{sequence:D6}.json";
        var screenshot = $"raw/frames/frame-{sequence:D6}.png";
        if (!bundle.Entries.Contains(observation) || !bundle.Entries.Contains(screenshot)) return new();
        var frame = bundle.ReadJson<FrameObservation>(observation);
        return frame.Sequence == sequence && frame.FrameEntry == screenshot ? From(frame) : new();
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
        }
        return result;
    }
}
