using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Cli;

/// <summary>Derived presentation over one retained observation. Never changes raw controls or pixels.</summary>
internal static class MapperGridDiscovery
{
    internal static IReadOnlyList<MapperHighlight> Discover(FrameObservation frame, WindowObservation surface,
        IReadOnlyList<AutomationObservation> controls, byte[] png)
    {
        var origin = frame.ScreenshotBounds ?? frame.Window.Bounds;
        if (frame.Acquisition is { StabilityStatus: not "stable" } || frame.Trigger == "mapped-resume-baseline" ||
            png.Length > 64 * 1024 * 1024 || origin.Width <= 0 || origin.Height <= 0 ||
            (long)origin.Width * origin.Height > 16_000_000) return [];
        var hosts = AutomationObservationVisibility.FilterEffectivelyVisible(controls)
            .Where(IsPotentialHost)
            .Where(c => MapperHighlightModel.Contains(origin, c.Bounds) && MapperHighlightModel.Contains(surface.Bounds, c.Bounds))
            .OrderBy(c => (long)c.Bounds.Width * c.Bounds.Height).ToArray();
        if (hosts.Length == 0) return [];
        var pixels = OpaqueSurfaceScanner.PixelFrame.Decode(png);
        // Native retained images use physical pixels, with no guessed scaling or crop offset.
        if (pixels.Width != origin.Width || pixels.Height != origin.Height) return [];
        var result = new List<MapperHighlight>();
        long scanned = 0;
        var attempts = 0;
        foreach (var host in hosts)
        {
            if (result.Any(grid => MapperHighlightModel.Contains(host.Bounds, grid.Bounds))) continue;
            // A container around an actual grid/gallery is not another table.
            if (controls.Any(c => c != host && MapperHighlightModel.Contains(host.Bounds, c.Bounds) &&
                (HasPattern(c, "Grid") || HasPattern(c, "Table") || c.ClassName == "NetUIGalleryButtonGroup"))) continue;
            if (++attempts > 8 || (scanned += (long)host.Bounds.Width * host.Bounds.Height) > 32_000_000) break;
            var region = host.Bounds with { X = host.Bounds.X - origin.X, Y = host.Bounds.Y - origin.Y };
            var table = VisualSurfaceScanner.FindBorderedTable(pixels, region);
            if (table is null || table.Width < region.Width * .6 || table.Height < region.Height * .6) continue;
            // Keep the actual provider host for scrolling and exact later review binding.
            result.Add(new(host.RuntimeId, host.Bounds, MapperHighlightKind.GridCandidate,
                string.IsNullOrWhiteSpace(host.Name) ? "Data grid" : host.Name, "Computer vision", host));
        }
        return result;
    }

    private static bool IsPotentialHost(AutomationObservation c) =>
        !MapperHighlightModel.IsVisual(c) && c.FrameworkId != "UiAtlas.NativeHwnd" &&
        c.ControlType.Replace("ControlType.", "", StringComparison.Ordinal) is "Pane" or "Custom" or "Group" or "List" &&
        c.Bounds.Width >= 180 && c.Bounds.Height >= 70 && HasPattern(c, "Scroll") &&
        !HasPattern(c, "Grid") && !HasPattern(c, "Table");

    private static bool HasPattern(AutomationObservation c, string name) =>
        c.SupportedPatterns?.Any(p => p == name || p == name + "Pattern" || p == name + "PatternIdentifiers.Pattern") == true;

    internal static long HostHwnd(AutomationObservation control) =>
        control.NativeWindowHwnd is > 0 ? control.NativeWindowHwnd.Value : control.WindowHwnd;

    internal static bool SameHost(AutomationObservation a, AutomationObservation b) =>
        a.WindowHwnd == b.WindowHwnd && a.ClassName == b.ClassName && a.AutomationId == b.AutomationId &&
        a.Name == b.Name && a.ControlType == b.ControlType && a.Bounds == b.Bounds &&
        (a.NativeWindowHwnd is not > 0 || b.NativeWindowHwnd is not > 0 || a.NativeWindowHwnd == b.NativeWindowHwnd);

    internal static AutomationObservation? ResolveRetainedHost(MapperHighlight selection, FrameObservation frame, RectI bounds)
    {
        var expected = selection.Control with { Bounds = bounds };
        var controls = MapperHighlightModel.FrameControls(frame, selection.Control.WindowHwnd);
        var matches = controls.Where(c => SameHost(expected, c)).ToArray();
        var exact = matches.Where(c => c.RuntimeId == expected.RuntimeId).ToArray();
        if (exact.Length == 1) return exact[0];
        // A new native episode has new evidence IDs. Keep provider identity, not an arbitrary duplicate.
        matches = matches.Where(c => MapperHighlightModel.IsMsaa(c) == MapperHighlightModel.IsMsaa(expected) &&
            (c.FrameworkId == "UiAtlas.NativeHwnd") == (expected.FrameworkId == "UiAtlas.NativeHwnd")).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
