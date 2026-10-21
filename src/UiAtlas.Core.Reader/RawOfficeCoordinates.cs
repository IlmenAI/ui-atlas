using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Reader;

/// <summary>Same-frame coordinate registration for display; never rewrites a raw record.</summary>
public static class RawOfficeCoordinates
{
    public sealed record Registration(RectI Reported, RectI Display, double Scale, string Evidence);

    public static IReadOnlyDictionary<(string Stream, string Runtime), Registration> Resolve(FrameObservation? frame, long hwnd)
    {
        var result = new Dictionary<(string, string), Registration>();
        var window = (frame?.ScopedWindows ?? []).SingleOrDefault(w => w.Hwnd == hwnd);
        if (frame is null || window is null || !window.ClassName.StartsWith("bosa_sdm", StringComparison.OrdinalIgnoreCase) ||
            window.ProcessId != frame.Window.ProcessId || window.Dpi <= 0 || frame.Window.Dpi <= 0) return result;
        var scale = (double)frame.Window.Dpi / window.Dpi;
        if (scale is < .25 or > 4 || Math.Abs(scale - 1) < .01) return result;
        var streams = RawVariantPresentation.StreamsForWindow(frame, hwnd).Where(s => s.Status is "ok" or "partial").ToArray();
        bool Scoped(AutomationObservation c) => c.WindowHwnd == hwnd && (c.QueryWindowHwnd is null || c.QueryWindowHwnd == hwnd) &&
            c.ScopeStatus is not ("unverified" or "outside-target");
        // The MSAA client root is physical even when its virtual children are not.
        // Its rectangle supplies an origin, not UIA records or inferred membership.
        var clients = streams.Where(s => s.Source.StartsWith("msaa", StringComparison.Ordinal)).SelectMany(s => s.Controls)
            .Where(c => Scoped(c) && Type(c) == "Window" && Contains(window.Bounds, c.Bounds) &&
                c.Bounds.Width > window.Bounds.Width / 2 && c.Bounds.Height > window.Bounds.Height / 2)
            .Select(c => c.Bounds).Distinct().ToArray();
        if (clients.Length != 1) return result;
        var client = clients[0];
        var native = (frame.RawStreams ?? []).Where(s => s.Source == "native-hwnd" && s.TargetWindowHwnd == hwnd && s.Status is "ok" or "partial")
            .SelectMany(s => s.Controls).Where(c => Scoped(c) && c.NativeWindowHwnd is > 0 && !c.IsOffscreen && Contains(client, c.Bounds)).ToArray();
        var edits = native.Where(c => c.ClassName.Equals("EDTBX", StringComparison.OrdinalIgnoreCase) ||
                c.ClassName.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)).Select(c => c.Bounds).Distinct().ToArray();
        var scrolls = native.Where(c => c.ClassName.Equals("ScrollBar", StringComparison.OrdinalIgnoreCase) &&
                c.Bounds.Height > 2L * c.Bounds.Width && c.Bounds.Width <= 40).Select(c => c.Bounds).Distinct().ToArray();
        foreach (var stream in streams)
        {
            var msaa = stream.Source.StartsWith("msaa", StringComparison.Ordinal);
            var scoped = stream.Controls.Where(Scoped).ToArray();
            // Duplicate identifiers cannot establish a unique ancestry chain.
            if (scoped.Select(c => c.RuntimeId).Distinct(StringComparer.Ordinal).Count() != scoped.Length) continue;
            var byId = scoped.ToDictionary(c => c.RuntimeId, StringComparer.Ordinal);
            var roots = scoped.Where(c => Type(c) == "Window" && (msaa ? c.Bounds == client : Contains(c.Bounds, client)))
                .Select(c => c.RuntimeId).ToHashSet(StringComparer.Ordinal);
            bool VirtualChild(AutomationObservation c)
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
            var children = scoped.Where(VirtualChild).ToArray();
            var visible = AutomationObservationVisibility.FilterEffectivelyVisible(scoped).ToHashSet();
            var virtualEdits = children.Where(c => Type(c) == "Edit" && visible.Contains(c) && ControlGeometry.IsRenderable(c.Bounds))
                .Select(c => c.Bounds).Distinct().ToArray();
            var matches = virtualEdits.Select(e => edits.Where(n => EditInset(Transform(e, client, scale), n)).ToArray()).ToArray();
            if (virtualEdits.Length == 0 || matches.Any(m => m.Length != 1) ||
                matches.Select(m => m[0]).Distinct().Count() != virtualEdits.Length ||
                virtualEdits.Any(e => edits.Any(n => EditInset(e, n)))) continue;
            var anchorCount = virtualEdits.Length;
            // Insert Function has one EDTBX. Its list's separate native scrollbar
            // independently checks the scale and client origin on both axes.
            if (anchorCount == 1)
            {
                var lists = children.Where(c => Type(c) == "List" && visible.Contains(c) && ControlGeometry.IsRenderable(c.Bounds))
                    .Select(c => c.Bounds).Distinct().ToArray();
                var pairs = lists.SelectMany(l => scrolls.Where(s => ListScrollbar(Transform(l, client, scale), s))
                    .Select(s => (List: l, Scroll: s))).ToArray();
                if (pairs.Length != 1 || lists.Any(l => scrolls.Any(s => ListScrollbar(l, s)))) continue;
                anchorCount++;
            }
            foreach (var c in children.Where(c => ControlGeometry.IsRenderable(c.Bounds)))
            {
                var display = Transform(c.Bounds, client, scale);
                if (ControlGeometry.IsRenderable(display)) result[(stream.StreamId, c.RuntimeId)] = new(c.Bounds, display, scale,
                    $"{anchorCount} independent native anchors; recorded client origin and DPI; {stream.Source} ancestry");
            }
        }
        return result;
    }

    public static Registration? ForControl(IReadOnlyDictionary<(string Stream, string Runtime), Registration> registrations, UiMapControlView control)
    {
        if (registrations.Count == 0) return null;
        var stream = UiMappingReadModel.Property(control.Source, "rawStreamId");
        // Native map projections expose the provider's RuntimeId array as the
        // runtimeId property. The sealed observation retains our source record ID.
        var runtime = control.Source.Observations is { Count: 1 } observations ? observations[0].Control.RuntimeId :
            UiMappingReadModel.Property(control.Source, "sourceAccessibilityEvidenceId") ?? UiMappingReadModel.Property(control.Source, "runtimeId");
        var registration = stream is not null && runtime is not null ? registrations.GetValueOrDefault((stream, runtime)) : null;
        return registration?.Reported == control.Bounds ? registration : null;
    }

    private static string Type(AutomationObservation c) => c.ControlType.Replace("ControlType.", "", StringComparison.Ordinal);
    private static bool Contains(RectI outer, RectI inner) => ControlGeometry.IsRenderable(inner) &&
        inner.X >= outer.X && inner.Y >= outer.Y && (long)inner.X + inner.Width <= (long)outer.X + outer.Width &&
        (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height;
    private static bool EditInset(RectI outer, RectI native) => Contains(outer, native) &&
        native.X - outer.X <= 8 && native.Y - outer.Y <= 8 &&
        (long)outer.X + outer.Width - native.X - native.Width <= 8 &&
        (long)outer.Y + outer.Height - native.Y - native.Height <= 8;
    private static bool ListScrollbar(RectI list, RectI scroll) => Contains(list, scroll) &&
        scroll.Y - list.Y <= 8 && (long)list.Y + list.Height - scroll.Y - scroll.Height <= 8 &&
        (long)list.X + list.Width - scroll.X - scroll.Width <= 8;
    private static RectI Transform(RectI b, RectI origin, double scale)
    {
        var left = origin.X + (long)Math.Round((b.X - (double)origin.X) * scale);
        var top = origin.Y + (long)Math.Round((b.Y - (double)origin.Y) * scale);
        var right = origin.X + (long)Math.Round((b.X + (double)b.Width - origin.X) * scale);
        var bottom = origin.Y + (long)Math.Round((b.Y + (double)b.Height - origin.Y) * scale);
        if (left is < int.MinValue or > int.MaxValue || top is < int.MinValue or > int.MaxValue ||
            right is < int.MinValue or > int.MaxValue || bottom is < int.MinValue or > int.MaxValue ||
            right - left > int.MaxValue || bottom - top > int.MaxValue) return new(0, 0, 0, 0);
        return new((int)left, (int)top, (int)(right - left), (int)(bottom - top));
    }
}
