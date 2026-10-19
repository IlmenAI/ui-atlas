using System.Runtime.InteropServices;
using System.Text;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Cli;

/// <summary>Live overlay coordinates; never modifies a raw provider packet.</summary>
internal static class MapperMsaaGeometry
{
    internal static IReadOnlyList<AutomationObservation> ProjectLive(WindowObservation surface,
        IReadOnlyList<AutomationObservation> controls)
    {
        // Office SDM returns physical window/client roots but, on a mixed-DPI
        // desktop, client descendants can retain the dialog's logical scale.
        // Qualify against the real HWND and both Windows client coordinate
        // spaces. Never infer a scale from overflowing provider rectangles.
        if (!surface.ClassName.StartsWith("bosa_sdm", StringComparison.OrdinalIgnoreCase)) return controls;
        var hwnd = (nint)surface.Hwnd;
        _ = GetWindowThreadProcessId(hwnd, out var process);
        if (process != surface.ProcessId) return controls;
        var previous = SetThreadDpiAwarenessContext((nint)(-4));
        if (previous == 0) return controls;
        try
        {
            var current = WindowCatalog.Resolve(surface.Hwnd);
            // Recorded frames use DWM bounds; passive UIA refresh uses outer bounds.
            if (WindowSnapshotCapture.VisibleFrameBounds(current) != surface.Bounds && current.Bounds != surface.Bounds) return controls;
            if (!GetClientRect(hwnd, out var physical) || physical.Right <= 0 || physical.Bottom <= 0) return controls;
            var origin = new Point();
            if (!ClientToScreen(hwnd, ref origin)) return controls;
            var client = new RectI(origin.X, origin.Y, physical.Right, physical.Bottom);
            // GetClientRect in the target context may already be physical (notably
            // at fractional monitor scales). Monitor DPI is only a hypothesis:
            // require multiple independent native edit rectangles to prove it.
            var monitor = MonitorFromWindow(hwnd, 2);
            if (surface.Dpi > 0 && GetScaleFactorForMonitor(monitor, out var percent) == 0)
            {
                var anchored = ProjectAnchoredClient(controls, surface.Hwnd, client,
                    percent * 96d / (100 * surface.Dpi), NativeEdits(hwnd, process));
                if (!ReferenceEquals(anchored, controls)) return anchored;
            }
            var targetContext = GetWindowDpiAwarenessContext(hwnd);
            if (targetContext == 0 || SetThreadDpiAwarenessContext(targetContext) == 0) return controls;
            if (!GetClientRect(hwnd, out var logical)) return controls;
            return ProjectClient(controls, surface.Hwnd,
                new(origin.X, origin.Y, physical.Right, physical.Bottom), logical.Right, logical.Bottom);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return controls; }
        finally { _ = SetThreadDpiAwarenessContext(previous); }
    }

    internal static IReadOnlyList<AutomationObservation> ProjectAnchoredClient(
        IReadOnlyList<AutomationObservation> controls, long hwnd, RectI client, double scale,
        IReadOnlyList<RectI> nativeEdits)
    {
        if (!double.IsFinite(scale) || scale is < .25 or > 4 || Math.Abs(scale - 1) < .01) return controls;
        var anchors = nativeEdits.Where(r => MapperHighlightModel.Contains(client, r)).Distinct().ToArray();
        if (anchors.Length < 2) return controls;
        var changed = new Dictionary<AutomationObservation, RectI>();
        // UIA and MSAA are qualified independently. A correct native UIA child
        // must not inherit the scale of a virtual Office sibling or another provider.
        foreach (var msaa in new[] { true, false })
        {
            var scoped = controls.Where(c => c.WindowHwnd == hwnd && MapperHighlightModel.IsMsaa(c) == msaa &&
                !MapperHighlightModel.IsVisual(c) && c.FrameworkId != "UiAtlas.NativeHwnd").ToArray();
            var byId = scoped.GroupBy(c => c.RuntimeId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var roots = scoped.Where(c => Type(c) == "Window" &&
                (msaa ? c.Bounds == client : MapperHighlightModel.Contains(c.Bounds, client)))
                .Select(c => c.RuntimeId).ToHashSet(StringComparer.Ordinal);
            bool VirtualClientChild(AutomationObservation c)
            {
                if (roots.Contains(c.RuntimeId) || Type(c) is "Window" or "TitleBar" || c.NativeWindowHwnd is > 0) return false;
                var parent = c.ParentRuntimeId;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (seen.Add(parent))
                {
                    if (roots.Contains(parent)) return true;
                    if (!byId.TryGetValue(parent, out var ancestor) || Type(ancestor) == "TitleBar" || ancestor.NativeWindowHwnd is > 0) return false;
                    parent = ancestor.ParentRuntimeId;
                }
                return false;
            }
            var children = scoped.Where(VirtualClientChild).ToArray();
            var edits = children.Where(c => Type(c) == "Edit" && !c.IsOffscreen && c.Bounds is { Width: > 0, Height: > 0 })
                .Select(c => c.Bounds).Distinct().ToArray();
            var matches = edits.Select(edit => anchors.Where(native => CloseInset(Transform(edit, client, scale), native)).ToArray()).ToArray();
            if (edits.Length < 2 || matches.Any(m => m.Length != 1) ||
                matches.Select(m => m[0]).Distinct().Count() != edits.Length ||
                edits.Any(edit => anchors.Any(native => CloseInset(edit, native)))) continue;
            foreach (var child in children.Where(c => c.Bounds is { Width: > 0, Height: > 0 }))
                changed[child] = Transform(child.Bounds, client, scale);
        }
        return changed.Count == 0 ? controls : controls.Select(c => changed.TryGetValue(c, out var bounds) ? c with { Bounds = bounds } : c).ToArray();
    }

    private static string Type(AutomationObservation c) => c.ControlType.Replace("ControlType.", "", StringComparison.Ordinal);
    private static bool CloseInset(RectI outer, RectI native) => native.X >= outer.X && native.Y >= outer.Y &&
        native.X - outer.X <= 8 && native.Y - outer.Y <= 8 &&
        (long)outer.X + outer.Width - native.X - native.Width is >= 0 and <= 8 &&
        (long)outer.Y + outer.Height - native.Y - native.Height is >= 0 and <= 8;
    private static RectI Transform(RectI bounds, RectI client, double scale)
    {
        var left = client.X + (int)Math.Round((bounds.X - (double)client.X) * scale);
        var top = client.Y + (int)Math.Round((bounds.Y - (double)client.Y) * scale);
        var right = client.X + (int)Math.Round((bounds.X + (double)bounds.Width - client.X) * scale);
        var bottom = client.Y + (int)Math.Round((bounds.Y + (double)bounds.Height - client.Y) * scale);
        return new(left, top, right - left, bottom - top);
    }

    private static IReadOnlyList<RectI> NativeEdits(nint root, uint process)
    {
        var result = new List<RectI>();
        var visited = 0;
        _ = EnumChildWindows(root, (hwnd, data) =>
        {
            if (++visited > 512) return false;
            _ = GetWindowThreadProcessId(hwnd, out var owner);
            var name = new StringBuilder(128); _ = GetClassName(hwnd, name, name.Capacity);
            if (owner == process && IsWindowVisible(hwnd) &&
                (name.ToString().Equals("EDTBX", StringComparison.OrdinalIgnoreCase) ||
                 name.ToString().StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) && GetWindowRect(hwnd, out var rect))
                result.Add(new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            return true;
        }, 0);
        return result;
    }

    internal static IReadOnlyList<AutomationObservation> ProjectClient(IReadOnlyList<AutomationObservation> controls,
        long hwnd, RectI physicalClient, int logicalWidth, int logicalHeight)
    {
        if (logicalWidth <= 0 || logicalHeight <= 0 || physicalClient.Width <= 0 || physicalClient.Height <= 0) return controls;
        var sx = (double)physicalClient.Width / logicalWidth;
        var sy = (double)physicalClient.Height / logicalHeight;
        if (Math.Abs(sx - 1) < .01 && Math.Abs(sy - 1) < .01 || Math.Abs(sx - sy) > .01) return controls;
        var msaa = controls.Where(c => c.WindowHwnd == hwnd && MapperHighlightModel.IsMsaa(c)).ToArray();
        var clientRoots = msaa.Where(c => c.ControlType is "ControlType.Window" or "Window" &&
            c.Bounds == physicalClient).Select(c => c.RuntimeId).ToHashSet(StringComparer.Ordinal);
        if (clientRoots.Count == 0) return controls;
        var byId = msaa.GroupBy(c => c.RuntimeId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        bool IsClientDescendant(AutomationObservation control)
        {
            if (control.WindowHwnd != hwnd || !MapperHighlightModel.IsMsaa(control) || clientRoots.Contains(control.RuntimeId)) return false;
            var parent = control.ParentRuntimeId;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (seen.Add(parent))
            {
                if (clientRoots.Contains(parent)) return true;
                if (!byId.TryGetValue(parent, out var ancestor)) return false;
                parent = ancestor.ParentRuntimeId;
            }
            return false;
        }
        // Do not rescale an SDM provider that already reports physical children.
        // The observed failure has ordinary visible commands outside the real
        // client, while the measured logical-to-physical transform puts them in it.
        if (!msaa.Any(c => !c.IsOffscreen && IsClientDescendant(c) &&
            c.ControlType is "ControlType.Button" or "ControlType.Edit" or "ControlType.CheckBox" &&
            !MapperHighlightModel.Contains(physicalClient, c.Bounds) &&
            MapperHighlightModel.Contains(physicalClient, Transform(c.Bounds)))) return controls;
        return controls.Select(c => !IsClientDescendant(c) || c.Bounds.Width <= 0 || c.Bounds.Height <= 0 ? c : c with
        {
            Bounds = Transform(c.Bounds)
        }).ToArray();

        RectI Transform(RectI bounds)
        {
            var left = physicalClient.X + (int)Math.Round((bounds.X - (double)physicalClient.X) * sx);
            var top = physicalClient.Y + (int)Math.Round((bounds.Y - (double)physicalClient.Y) * sy);
            var right = physicalClient.X + (int)Math.Round((bounds.X + (double)bounds.Width - physicalClient.X) * sx);
            var bottom = physicalClient.Y + (int)Math.Round((bounds.Y + (double)bounds.Height - physicalClient.Y) * sy);
            return new(left, top, right - left, bottom - top);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    private delegate bool EnumWindow(nint hwnd, nint data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint hwnd, EnumWindow callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(nint monitor, out int scale);
}
