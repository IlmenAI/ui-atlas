using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Accessibility;
using UIMaps.Protocol.V1;

namespace UIMaps.Recorder.Windows;

/// <summary>Unmerged MSAA window/client trees. No invented Office controls,
/// inferred patterns, geometry clipping or cross-root ownership assertions.</summary>
public static class NativeMsaaProvider
{
    public static AccessibilityPacket Capture(long hwnd, string windowId, int maxNodes, int maxDepth, bool reuseClientRoot = false)
    {
        var packet = new AccessibilityPacket { Health = ProtocolHealth.Complete() };
        var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        IAccessible? client = null;
        byte[]? clientIdentity = null;
        AccessibilityNodeRecord[]? alias = null;
        var identityQueries = 0;
        var checkingAlias = false;
        var aliasMatched = false;
        var clientPass = false;
        try
        {
        if (reuseClientRoot && AccessibleObjectFromWindow((nint)hwnd, unchecked((uint)-4), ref iid, out client) >= 0 && client is not null)
            clientIdentity = ReadIdentity(client);
        foreach (var objectId in new uint[] { 0, unchecked((uint)-4) })
        {
            clientPass = objectId != 0;
            IAccessible? root = clientPass ? client : null;
            var retainedClient = root is not null;
            if (root is null && (AccessibleObjectFromWindow((nint)hwnd, objectId, ref iid, out root) < 0 || root is null)) continue;
            try
            {
                if (clientPass && alias is not null && clientIdentity is not null
                    && ReadIdentity(root) is { } currentIdentity && currentIdentity.AsSpan().SequenceEqual(clientIdentity)
                    && TryAppendRootAlias(packet, alias, maxNodes))
                {
                    packet.ProviderAttempts.Add(new ProviderAttemptOutcome
                    {
                        ProviderId = "msaa", WorkerVersion = "msaa-root-reuse/1", Status = "completed",
                        ResourceUsageSummary =
                        {
                            new StringProperty { Key = "root_alias_reused_nodes", Value = alias.Length.ToString() },
                            new StringProperty { Key = "root_alias_source_evidence_id", Value = alias[0].AccessibilityEvidenceId },
                            new StringProperty { Key = "root_alias_identity_sha256", Value = Convert.ToHexString(SHA256.HashData(clientIdentity)) },
                            new StringProperty { Key = "root_alias_identity_queries", Value = identityQueries.ToString() },
                        }
                    });
                }
                else Visit(root, 0, "", 0);
            }
            finally { if (!retainedClient && Marshal.IsComObject(root)) Marshal.ReleaseComObject(root); }
        }
        }
        finally { if (client is not null && Marshal.IsComObject(client)) Marshal.ReleaseComObject(client); }
        packet.NodesIncluded = packet.Nodes.Count;
        if (packet.Nodes.Count == 0) packet.Health = ProtocolHealth.Single(HealthState.SensorUnavailable,
            "RECORDER_MSAA_UNAVAILABLE", "No MSAA tree was returned for this query window.", "msaa");
        else if (packet.NodeLimited || packet.DepthLimited) packet.Health = ProtocolHealth.Single(HealthState.Partial,
            "RECORDER_MSAA_LIMIT", "MSAA traversal reached a configured bound.", "msaa");
        return packet;

        void Visit(IAccessible accessible, object child, string parent, int depth)
        {
            var first = packet.Nodes.Count;
            var match = !clientPass && !checkingAlias && !aliasMatched && clientIdentity is not null && child is int id && id == 0
                && identityQueries < 128 && ReadIdentity(accessible) is { } identity && identity.AsSpan().SequenceEqual(clientIdentity);
            if (match) { checkingAlias = true; aliasMatched = true; }
            try { VisitCore(accessible, child, parent, depth); }
            finally { if (match) { checkingAlias = false; alias = packet.Nodes.Skip(first).ToArray(); } }
        }

        byte[]? ReadIdentity(IAccessible accessible)
        {
            identityQueries++;
            if (accessible is not IMsaaIdentity identity) return null;
            nint buffer = 0;
            try
            {
                if (identity.GetIdentityString(0, out buffer, out var length) != 0 || buffer == 0 || length is 0 or > 4096) return null;
                var bytes = new byte[length]; Marshal.Copy(buffer, bytes, 0, (int)length); return bytes;
            }
            catch (COMException) { return null; }
            finally { if (buffer != 0) Marshal.FreeCoTaskMem(buffer); }
        }

        void VisitCore(IAccessible accessible, object child, string parent, int depth)
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

    internal static bool TryAppendRootAlias(AccessibilityPacket packet, IReadOnlyList<AccessibilityNodeRecord> source, int maximum)
    {
        if (source.Count == 0 || source.Count > maximum - packet.Nodes.Count) return false;
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in source)
        {
            if (node.AccessibilityEvidenceId.Length == 0 || !ids.TryAdd(node.AccessibilityEvidenceId, Guid.NewGuid().ToString("N"))
                || node.ChildCountObserved != node.ChildCountIncluded || node.PropertyCoverage is not { Failed: 0 }
                || node.Health?.State != HealthState.Complete) return false;
        }
        var copies = new List<AccessibilityNodeRecord>(source.Count);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in source)
        {
            var copy = node.Clone(); copy.AccessibilityEvidenceId = ids[node.AccessibilityEvidenceId];
            if (copies.Count == 0) copy.ParentEvidenceId = "";
            else if (!emitted.Contains(node.ParentEvidenceId) || !ids.TryGetValue(node.ParentEvidenceId, out var parent)) return false;
            else copy.ParentEvidenceId = parent;
            copies.Add(copy); emitted.Add(node.AccessibilityEvidenceId);
        }
        packet.Nodes.AddRange(copies); packet.NodesObserved += copies.Count;
        return true;
    }

    [ComImport, Guid("7852B78D-1CFD-41C1-A615-9C0C85960B5F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMsaaIdentity
    {
        [PreserveSig] int GetIdentityString(uint child, out nint bytes, out uint length);
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
