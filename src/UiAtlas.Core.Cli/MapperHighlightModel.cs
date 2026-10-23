using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Cli;

internal enum MapperHighlightKind { Uia, ComputerVision, NativeGrid, GridCandidate }

internal sealed record MapperHighlight(
    string Id, RectI Bounds, MapperHighlightKind Kind, string Name, string Source,
    AutomationObservation Control)
{
    public bool IsGrid => Kind is MapperHighlightKind.NativeGrid or MapperHighlightKind.GridCandidate;
    public string Color => Kind switch
    {
        MapperHighlightKind.Uia => "#0A84FF",
        MapperHighlightKind.ComputerVision => "#8B5CF6",
        MapperHighlightKind.NativeGrid => "#008F83",
        _ => "#C98208"
    };
    public string Label => Kind == MapperHighlightKind.NativeGrid ? "Native grid" : "Grid candidate";
    public string Classification => Kind == MapperHighlightKind.NativeGrid ? "UIA-native data grid" : "Potential data grid";
    public string NativeSupport => Kind == MapperHighlightKind.NativeGrid ? "Available" : "Not detected";
    public string Status => Kind == MapperHighlightKind.NativeGrid ? "Native structure detected" : "Candidate · not yet verified";
}

/// <summary>Presentation of current evidence only. Never qualifies extraction or saves a schema.</summary>
internal static class MapperHighlightModel
{
    // Native evidence IDs are opaque. Use the recorded provider response, not
    // an ID prefix, to distinguish UIA, MSAA and bare HWND geometry.
    internal static IReadOnlyList<AutomationObservation> FrameControls(FrameObservation frame, long hwnd,
        Func<long, bool>? isLiveChildOfSurface = null)
    {
        var providers = (frame.RawStreams ?? []).SelectMany(stream => stream.Controls
            .Select(control => (Key: (control.WindowHwnd, control.RuntimeId), stream.Source)))
            .GroupBy(item => item.Key).ToDictionary(group => group.Key, group => group.First().Source);
        return frame.Automation.Where(control => control.WindowHwnd == hwnd ||
                control.WindowHwnd == 0 && hwnd == frame.Window.Hwnd ||
                control.WindowHwnd != 0 && isLiveChildOfSurface?.Invoke(control.WindowHwnd) == true)
            .Select(control => providers.TryGetValue((control.WindowHwnd, control.RuntimeId), out var provider)
                ? provider switch
                {
                    "msaa" => control with { FrameworkId = "UiAtlas.Msaa" },
                    "native-hwnd" => control with { FrameworkId = "UiAtlas.NativeHwnd" },
                    _ => control
                } : control).ToArray();
    }

    internal static bool IsMsaa(AutomationObservation control) =>
        control.FrameworkId == "UiAtlas.Msaa" || control.RuntimeId.StartsWith("msaa:", StringComparison.OrdinalIgnoreCase);

    internal static bool IsVisual(AutomationObservation control) =>
        control.FrameworkId.StartsWith("UiAtlas.Visual", StringComparison.OrdinalIgnoreCase) ||
        control.RuntimeId.StartsWith("visual:", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<MapperHighlight> Build(RectI surface,
        IReadOnlyList<AutomationObservation> controls, IReadOnlyList<MapperHighlight>? visualGrids = null)
    {
        var visibleNative = AutomationObservationVisibility.FilterEffectivelyVisible(controls)
            .Select(c => (c.WindowHwnd, c.RuntimeId)).ToHashSet();
        var byIdentity = controls.Where(c => !string.IsNullOrWhiteSpace(c.RuntimeId))
            .GroupBy(c => (c.WindowHwnd, c.RuntimeId)).ToDictionary(group => group.Key, group => group.First());
        var eligible = controls.Where(c => IsVisual(c) || visibleNative.Contains((c.WindowHwnd, c.RuntimeId)))
            .Select(c => (Control: c, Bounds: VisibleBounds(c, byIdentity)))
            .Where(item => item.Bounds is { Width: > 0, Height: > 0 } && Contains(surface, item.Bounds)).ToArray();
        var grids = eligible.Select(item => ClassifyGrid(item.Control, item.Bounds!)).OfType<MapperHighlight>()
            .Concat((visualGrids ?? []).Where(g => Contains(surface, g.Bounds)))
            .OrderBy(g => g.Kind == MapperHighlightKind.NativeGrid ? 0 :
                g.Source == "Native window hint" ? 1 : 2)
            .ThenByDescending(g => Area(g.Bounds)).ToArray();
        var distinct = new List<MapperHighlight>();
        foreach (var grid in grids)
        {
            if (!distinct.Any(g => Equivalent(g.Bounds, grid.Bounds) ||
                (g.Kind == MapperHighlightKind.NativeGrid || g.Source == "Native window hint") &&
                grid.Source == "Computer vision" && Contains(g.Bounds, grid.Bounds))) distinct.Add(grid);
        }

        var parents = eligible.Select(item => item.Control).Where(c => IsAccessibility(c) && !string.IsNullOrWhiteSpace(c.ParentRuntimeId))
            .Select(c => (c.WindowHwnd, c.ParentRuntimeId)).ToHashSet();
        var ordinary = eligible.Where(item => !distinct.Any(g => Contains(g.Bounds, item.Bounds!)))
            .Where(item => IsOrdinaryControl(item.Control, parents))
            .Where(item => IsVisual(item.Control) || IsAccessibility(item.Control))
            .Where(item => Area(item.Bounds!) < Area(surface) / 3)
            .Select(item => new MapperHighlight(item.Control.RuntimeId, item.Bounds!,
                IsVisual(item.Control) ? MapperHighlightKind.ComputerVision : MapperHighlightKind.Uia,
                DisplayName(item.Control), IsVisual(item.Control) ? "Computer vision" : IsMsaa(item.Control) ? "MSAA" : "UI Automation", item.Control))
            .OrderBy(c => c.Kind).ToArray();
        foreach (var control in ordinary)
            if (!distinct.Any(c => Equivalent(c.Bounds, control.Bounds))) distinct.Add(control);
        return distinct.Take(500).ToArray();
    }

    private static bool IsOrdinaryControl(AutomationObservation control,
        HashSet<(long, string)> parents)
    {
        if (IsCommandGallery(control)) return true;
        if (Type(control) is "Button" or "Edit" or "ComboBox" or "CheckBox" or "RadioButton" or
            "TabItem" or "MenuItem" or "ListItem" or "Slider" or "Hyperlink" or "SplitButton") return true;
        // Legacy providers often expose real buttons as named Pane/Custom leaves (e.g. Delphi).
        // Preserve their UIA evidence without outlining the surrounding layout containers.
        return IsAccessibility(control) && Type(control) is ("Pane" or "Custom" or "Group" or "Text") &&
            !string.IsNullOrWhiteSpace(control.Name) &&
            !parents.Contains((control.WindowHwnd, control.RuntimeId));
    }

    private static MapperHighlight? ClassifyGrid(AutomationObservation c, RectI bounds)
    {
        if (bounds.Width < 60 || bounds.Height < 40 || IsCommandGallery(c)) return null;
        var type = Type(c);
        if (type is "DataItem" or "HeaderItem" || c.TableRow.HasValue || c.TableColumn.HasValue) return null;
        var native = IsUia(c) && c.SupportedPatterns?.Any(p => p is "Grid" or "Table" or "GridPattern" or "TablePattern" or
            "GridPatternIdentifiers.Pattern" or "TablePatternIdentifiers.Pattern") == true;
        var hint = c.FrameworkId == "UiAtlas.GridHostHint";
        if (!native && !hint && type is not ("Table" or "DataGrid")) return null;
        return new(c.RuntimeId, bounds,
            native ? MapperHighlightKind.NativeGrid : MapperHighlightKind.GridCandidate,
            DisplayName(c), native || IsUia(c) ? "UI Automation" : IsVisual(c) ? "Computer vision" : "Native window hint", c);
    }

    // Office command galleries use the DataGrid role for a palette of choices.
    // That role alone does not make the gallery a business-data extraction target.
    private static bool IsCommandGallery(AutomationObservation c) => IsUia(c) &&
        string.Equals(c.ClassName, "NetUIGalleryButtonGroup", StringComparison.OrdinalIgnoreCase);

    private static RectI? VisibleBounds(AutomationObservation control,
        IReadOnlyDictionary<(long, string), AutomationObservation> byIdentity)
    {
        var bounds = control.Bounds;
        if (IsVisual(control)) return bounds;
        var current = control;
        var visited = new HashSet<(long, string)>();
        while (!string.IsNullOrWhiteSpace(current.ParentRuntimeId))
        {
            var key = (current.WindowHwnd, current.ParentRuntimeId);
            if (!visited.Add(key) || !byIdentity.TryGetValue(key, out current!)) break;
            // Only a provider-reported scroll viewport supplies clipping evidence.
            // Ordinary layout ancestors may legitimately have smaller bounds.
            if (current.SupportedPatterns?.Any(pattern => pattern is "Scroll" or "ScrollPattern" or
                    "ScrollPatternIdentifiers.Pattern") != true) continue;
            var left = Math.Max(bounds.X, current.Bounds.X);
            var top = Math.Max(bounds.Y, current.Bounds.Y);
            var right = Math.Min((long)bounds.X + bounds.Width, (long)current.Bounds.X + current.Bounds.Width);
            var bottom = Math.Min((long)bounds.Y + bounds.Height, (long)current.Bounds.Y + current.Bounds.Height);
            if (right <= left || bottom <= top) return null;
            bounds = new(left, top, (int)(right - left), (int)(bottom - top));
        }
        return bounds; // Presentation only: MapperHighlight.Control keeps the original record.
    }

    private static bool IsUia(AutomationObservation c) =>
        !IsVisual(c) && !c.FrameworkId.StartsWith("UiAtlas.", StringComparison.OrdinalIgnoreCase) &&
        !c.RuntimeId.StartsWith("win32:", StringComparison.OrdinalIgnoreCase) &&
        !c.RuntimeId.StartsWith("msaa:", StringComparison.OrdinalIgnoreCase);

    private static bool IsAccessibility(AutomationObservation c) => IsUia(c) || IsMsaa(c);

    private static string DisplayName(AutomationObservation c) =>
        string.IsNullOrWhiteSpace(c.Name) ? "Data grid" : c.Name.Length > 64 ? c.Name[..61] + "…" : c.Name;
    private static string Type(AutomationObservation c) => c.ControlType.Replace("ControlType.", "", StringComparison.Ordinal);
    private static long Area(RectI b) => (long)b.Width * b.Height;
    internal static bool Contains(RectI outer, RectI inner) => inner.X >= outer.X - 2 && inner.Y >= outer.Y - 2 &&
        (long)inner.X + inner.Width <= (long)outer.X + outer.Width + 2 &&
        (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height + 2;
    internal static bool Equivalent(RectI a, RectI b)
    {
        var width = Math.Max(0L, Math.Min((long)a.X + a.Width, (long)b.X + b.Width) - Math.Max(a.X, b.X));
        var height = Math.Max(0L, Math.Min((long)a.Y + a.Height, (long)b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return width * height >= Math.Min(Area(a), Area(b)) * .85 &&
            Math.Max(Area(a), Area(b)) <= Math.Min(Area(a), Area(b)) * 1.3;
    }
}
