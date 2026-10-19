using System.Globalization;
using UIMaps.Protocol.V1;

namespace UIMaps.Perception.Core;

/// <summary>Observation-local coordinate reconciliation. Never changes sensor packets.</summary>
public static class OfficeMsaaGeometry
{
    public sealed record Correction(RectPx ReportedBounds, RectPx Bounds, double Scale, string Evidence);

    public static IReadOnlyDictionary<string, Correction> Resolve(SensorEpisodeBundle bundle)
    {
        var result = new Dictionary<string, Correction>(StringComparer.Ordinal);
        var nodes = bundle.SensorPackets.Where(packet => packet.Accessibility is not null)
            .SelectMany(packet => AccessibilityEvidence.Nodes(packet.Accessibility)).ToArray();
        foreach (var window in bundle.SensorPackets.Where(packet => packet.WindowTopology is not null)
                     .SelectMany(packet => packet.WindowTopology.Windows))
        {
            // Office SDM can report physical window/client roots but scaled client
            // descendants. DPI is only a candidate; independent native HWND edit
            // rectangles must corroborate the transform in this same observation.
            if (!window.ClassName.StartsWith("bosa_sdm", StringComparison.OrdinalIgnoreCase) ||
                !window.HasDpi || window.Dpi == 0 || bundle.DisplayDescriptor.DpiX <= 0 ||
                bundle.DisplayDescriptor.DpiX != bundle.DisplayDescriptor.DpiY) continue;
            var scale = bundle.DisplayDescriptor.DpiX / window.Dpi;
            if (scale is < .25 or > 4 || Math.Abs(scale - 1) < .01) continue;
            var scoped = nodes.Where(node => node.WindowEvidenceId == window.WindowEvidenceId).ToArray();
            var msaa = scoped.Where(node => node.ProviderId == "msaa").ToArray();
            var byId = msaa.GroupBy(node => node.AccessibilityEvidenceId)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var clients = msaa.Where(node => node.ControlType == "Window" &&
                    Contains(window.BoundsPx, node.BoundsPx) && node.BoundsPx.Width > window.BoundsPx.Width / 2 &&
                    node.BoundsPx.Height > window.BoundsPx.Height / 2)
                .Select(node => node.BoundsPx).Distinct().ToArray();
            if (clients.Length != 1) continue;
            var client = clients[0];
            var roots = msaa.Where(node => node.ControlType == "Window" && node.BoundsPx.Equals(client))
                .Select(node => node.AccessibilityEvidenceId).ToHashSet(StringComparer.Ordinal);
            bool Descendant(AccessibilityNodeRecord node)
            {
                if (roots.Contains(node.AccessibilityEvidenceId)) return false;
                var parent = node.ParentEvidenceId;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (seen.Add(parent))
                {
                    if (roots.Contains(parent)) return true;
                    if (!byId.TryGetValue(parent, out var ancestor)) return false;
                    parent = ancestor.ParentEvidenceId;
                }
                return false;
            }
            var descendants = msaa.Where(Descendant).ToArray();
            var nativeEdits = scoped.Where(node => node.ProviderId == "native-hwnd" && !node.IsOffscreen &&
                    node.ClassName.Equals("EDTBX", StringComparison.OrdinalIgnoreCase) && Contains(client, node.BoundsPx))
                .Select(node => node.BoundsPx).Distinct().ToArray();
            var edits = descendants.Where(node => node.ControlType == "Edit" && !node.IsOffscreen)
                .Select(node => node.BoundsPx).Distinct().ToArray();
            // Require multiple separate anchors, unique matches, and reject the
            // same hypothesis when the untransformed coordinates already match.
            var matches = edits.Select(edit => nativeEdits.Where(native => CloseInset(Transform(edit, client, scale), native)).ToArray()).ToArray();
            if (edits.Length < 2 || matches.Any(match => match.Length != 1) ||
                matches.Select(match => match[0]).Distinct().Count() != edits.Length ||
                edits.Any(edit => nativeEdits.Any(native => CloseInset(edit, native))) ||
                !descendants.Any(node => !node.IsOffscreen && node.ControlType is "Button" or "Edit" or "CheckBox" &&
                    !Contains(client, node.BoundsPx) && Contains(client, Transform(node.BoundsPx, client, scale)))) continue;
            foreach (var node in descendants.Where(node => node.BoundsPx.Width > 0 && node.BoundsPx.Height > 0))
                result[node.AccessibilityEvidenceId] = new(node.BoundsPx.Clone(), Transform(node.BoundsPx, client, scale), scale,
                    $"same-observation native EDTBX anchors ({edits.Length}); window/display DPI; MSAA client ancestry");
        }
        return result;
    }

    public static void Apply(SensorEpisodeBundle bundle, IReadOnlyList<WorldObject> objects)
    {
        var corrections = Resolve(bundle);
        foreach (var control in objects)
        {
            var source = SurfaceSceneReconstructor.Property(control, "sourceAccessibilityEvidenceId");
            if (!corrections.TryGetValue(source, out var correction)) continue;
            control.BoundsPx = correction.Bounds.Clone();
            Set("sceneCoordinateCorrection", "office-msaa-client-scale/1");
            Set("sceneReportedBounds", string.Join(",", correction.ReportedBounds.X, correction.ReportedBounds.Y,
                correction.ReportedBounds.Width, correction.ReportedBounds.Height));
            Set("sceneCoordinateScale", correction.Scale.ToString(CultureInfo.InvariantCulture));
            Set("sceneCoordinateEvidence", correction.Evidence);
            void Set(string key, string value)
            {
                for (var i = control.Properties.Count - 1; i >= 0; i--)
                    if (control.Properties[i].Key == key) control.Properties.RemoveAt(i);
                control.Properties.Add(new StringProperty { Key = key, Value = value });
            }
        }
    }

    private static bool Contains(RectPx outer, RectPx inner) => inner.Width > 0 && inner.Height > 0 &&
        inner.X >= outer.X && inner.Y >= outer.Y && (long)inner.X + inner.Width <= (long)outer.X + outer.Width &&
        (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height;

    private static bool CloseInset(RectPx outer, RectPx native) => Contains(outer, native) &&
        native.X - outer.X <= 8 && native.Y - outer.Y <= 8 &&
        (long)outer.X + outer.Width - native.X - native.Width <= 8 &&
        (long)outer.Y + outer.Height - native.Y - native.Height <= 8;

    private static RectPx Transform(RectPx value, RectPx origin, double scale)
    {
        var left = origin.X + (int)Math.Round((value.X - (double)origin.X) * scale);
        var top = origin.Y + (int)Math.Round((value.Y - (double)origin.Y) * scale);
        var right = origin.X + (int)Math.Round((value.X + (double)value.Width - origin.X) * scale);
        var bottom = origin.Y + (int)Math.Round((value.Y + (double)value.Height - origin.Y) * scale);
        return new RectPx { X = left, Y = top, Width = right - left, Height = bottom - top };
    }
}
