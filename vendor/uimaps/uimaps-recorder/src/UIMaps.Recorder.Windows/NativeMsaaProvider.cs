using System.Runtime.InteropServices;
using Accessibility;
using UIMaps.Protocol.V1;

namespace UIMaps.Recorder.Windows;

/// <summary>Unmerged MSAA window/client trees. No invented Office controls,
/// inferred patterns, geometry clipping or cross-root ownership assertions.</summary>
public static class NativeMsaaProvider
{
    public static AccessibilityPacket Capture(long hwnd, string windowId, int maxNodes, int maxDepth)
    {
        var packet = new AccessibilityPacket { Health = ProtocolHealth.Complete() };
        var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        foreach (var objectId in new uint[] { 0, unchecked((uint)-4) })
        {
            if (AccessibleObjectFromWindow((nint)hwnd, objectId, ref iid, out var root) < 0 || root is null) continue;
            try { Visit(root, 0, "", 0); }
            finally { if (Marshal.IsComObject(root)) Marshal.ReleaseComObject(root); }
        }
        packet.NodesIncluded = packet.Nodes.Count;
        if (packet.Nodes.Count == 0) packet.Health = ProtocolHealth.Single(HealthState.SensorUnavailable,
            "RECORDER_MSAA_UNAVAILABLE", "No MSAA tree was returned for this query window.", "msaa");
        else if (packet.NodeLimited || packet.DepthLimited) packet.Health = ProtocolHealth.Single(HealthState.Partial,
            "RECORDER_MSAA_LIMIT", "MSAA traversal reached a configured bound.", "msaa");
        return packet;

        void Visit(IAccessible accessible, object child, string parent, int depth)
        {
            packet.NodesObserved++;
            if (packet.Nodes.Count >= maxNodes) { packet.NodeLimited = true; return; }
            var node = new AccessibilityNodeRecord { AccessibilityEvidenceId = Guid.NewGuid().ToString("N"),
                ParentEvidenceId = parent, WindowEvidenceId = windowId, ProviderId = "msaa",
                Health = ProtocolHealth.Complete(), PropertyCoverage = new PropertyCoverage() };
            T Read<T>(Func<T> read, T fallback)
            {
                node.PropertyCoverage.Attempted++;
                try { var value = read(); node.PropertyCoverage.Read++; return value; }
                catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or OverflowException)
                { node.PropertyCoverage.Failed++; return fallback; }
            }
            node.Name = Read(() => accessible.get_accName(child) ?? "", "");
            var role = Read(() => Convert.ToInt32(accessible.get_accRole(child)), 0);
            var state = Read(() => Convert.ToInt32(accessible.get_accState(child)), 0);
            node.ControlType = Role(role); node.LocalizedControlType = "MSAA.Role" + role;
            node.IsEnabled = (state & 1) == 0; node.IsOffscreen = (state & 0x18000) != 0;
            node.IsPassword = (state & 0x20000000) != 0;
            node.BoundsPx = Read(() => { accessible.accLocation(out var x, out var y, out var w, out var h, child);
                return new RectPx { X = x, Y = y, Width = Math.Max(0, w), Height = Math.Max(0, h) }; }, new RectPx());
            node.NativeControlIdEvidence = child is int id ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            packet.Nodes.Add(node);
            if (child is not int childId || childId != 0) return;
            var count = Read(() => accessible.accChildCount, 0); node.ChildCountObserved = count;
            if (depth >= maxDepth) { packet.DepthLimited |= count > 0; return; }
            var children = new object[Math.Min(Math.Max(0, count), Math.Max(0, maxNodes - packet.Nodes.Count))];
            if (children.Length == 0) { packet.NodeLimited |= count > 0; return; }
            if (AccessibleChildren(accessible, 0, children.Length, children, out var obtained) < 0) return;
            packet.NodeLimited |= obtained < count;
            foreach (var value in children.Take(obtained))
            {
                var includedBefore = packet.Nodes.Count;
                if (value is IAccessible nested)
                {
                    try { Visit(nested, 0, node.AccessibilityEvidenceId, depth + 1); }
                    finally { if (Marshal.IsComObject(nested)) Marshal.ReleaseComObject(nested); }
                }
                else if (value is int number && number != 0) Visit(accessible, number, node.AccessibilityEvidenceId, depth + 1);
                if (packet.Nodes.Count > includedBefore) node.ChildCountIncluded++;
            }
        }
    }

    private static string Role(int role) => role switch
    {
        3 => "ScrollBar", 9 or 18 => "Window", 10 or 16 => "Pane", 11 => "Menu", 12 => "MenuItem",
        15 => "Document", 20 or 38 => "Group", 21 => "Separator", 22 => "ToolBar", 24 => "DataGrid",
        25 => "Header", 29 => "DataItem", 30 => "Hyperlink", 33 => "List", 34 => "ListItem",
        35 => "Tree", 36 => "TreeItem", 37 => "TabItem", 40 => "Image", 41 => "Text", 42 => "Edit",
        43 => "Button", 44 => "CheckBox", 45 => "RadioButton", 46 or 47 => "ComboBox", 51 => "Slider",
        52 => "Spinner", 56 or 57 or 58 or 62 => "SplitButton", 60 => "Tab", _ => "Custom"
    };

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(nint hwnd, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleChildren([MarshalAs(UnmanagedType.Interface)] IAccessible accessible,
        int start, int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int obtained);
}
