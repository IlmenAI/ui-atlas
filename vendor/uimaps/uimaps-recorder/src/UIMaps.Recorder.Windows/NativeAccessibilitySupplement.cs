using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UIMaps.Protocol.V1;

namespace UIMaps.Recorder.Windows;

public static class NativeAccessibilitySupplement
{
    private const string ProviderId = "native-hwnd";
    private const int RuntimeIdMarker = 0x4E415449;

    public static NativeAccessibilitySupplementResult Append(
        AccessibilityPacket packet,
        IntPtr rootWindow,
        string windowEvidenceId,
        int maxNodes)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var started = DateTimeOffset.UtcNow;
        try
        {
            var facts = Enumerate(rootWindow);
            var result = Build(packet, facts, rootWindow.ToInt64(), windowEvidenceId, maxNodes);
            packet.ProviderAttempts.Add(new ProviderAttemptOutcome
            {
                AttemptId = Guid.NewGuid().ToString("N"),
                CaptureWindowId = windowEvidenceId,
                ProviderId = ProviderId,
                WorkerVersion = "0.9.0",
                WorkerArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                StartedAt = started.ToString("O"),
                EndedAt = DateTimeOffset.UtcNow.ToString("O"),
                Status = result.Limited ? "partial" : "completed",
                NodesObserved = result.Observed,
                NodesIncluded = result.Added,
                PropertiesAttempted = result.Observed * 7L,
                PropertiesRead = result.Observed * 7L,
                FrontierAdded = result.Limited ? 1 : 0,
            });
            return result;
        }
        catch
        {
            packet.ProviderAttempts.Add(new ProviderAttemptOutcome
            {
                AttemptId = Guid.NewGuid().ToString("N"),
                CaptureWindowId = windowEvidenceId,
                ProviderId = ProviderId,
                WorkerVersion = "0.9.0",
                WorkerArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                StartedAt = started.ToString("O"),
                EndedAt = DateTimeOffset.UtcNow.ToString("O"),
                Status = "provider_failed",
                FailureClass = "native_window_enumeration",
            });
            return new NativeAccessibilitySupplementResult(0, 0, 0, false);
        }
    }

    internal static NativeAccessibilitySupplementResult Build(
        AccessibilityPacket packet,
        IReadOnlyList<NativeWindowFact> facts,
        long rootHandle,
        string windowEvidenceId,
        int maxNodes)
    {
        var rootNodeId = packet.Nodes.FirstOrDefault()?.AccessibilityEvidenceId ?? string.Empty;
        var nativeNodeByHandle = packet.Nodes
            .Where(node => long.TryParse(node.NativeWindowHandleEvidence, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .GroupBy(node => long.Parse(node.NativeWindowHandleEvidence, CultureInfo.InvariantCulture))
            .ToDictionary(group => group.Key, group => group.First().AccessibilityEvidenceId);
        var directChildren = facts
            .GroupBy(fact => fact.ParentHandle)
            .ToDictionary(group => group.Key, group => group.OrderBy(fact => fact.EnumerationOrder).ToList());
        var paths = BuildPaths(facts, directChildren, rootHandle);
        var visible = facts
            .Where(fact => fact.Visible && fact.BoundsPx is { Width: > 0, Height: > 0 })
            .OrderBy(fact => Depth(fact, facts, rootHandle))
            .ThenBy(fact => fact.EnumerationOrder)
            .ToList();
        var candidates = new List<NativeWindowFact>();
        foreach (var fact in visible)
        {
            if (nativeNodeByHandle.TryGetValue(fact.Handle, out _)) continue;
            var structuralMatch = packet.Nodes.FirstOrDefault(node => SameBounds(node.BoundsPx, fact.BoundsPx)
                && EqualsNormalized(node.ClassName, fact.ClassName)
                && (string.IsNullOrWhiteSpace(node.Name)
                    || string.IsNullOrWhiteSpace(fact.Text)
                    || EqualsNormalized(node.Name, fact.Text)));
            if (structuralMatch is not null)
            {
                nativeNodeByHandle[fact.Handle] = structuralMatch.AccessibilityEvidenceId;
                if (string.IsNullOrWhiteSpace(structuralMatch.NativeWindowHandleEvidence))
                    structuralMatch.NativeWindowHandleEvidence = fact.Handle.ToString(CultureInfo.InvariantCulture);
                continue;
            }
            candidates.Add(fact);
        }

        var remaining = Math.Max(0, maxNodes - packet.Nodes.Count);
        var included = candidates.Take(remaining).ToList();
        foreach (var fact in included)
        {
            var path = paths.GetValueOrDefault(fact.Handle, Segment(fact, 0));
            var id = "native:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                windowEvidenceId + "\u001f" + path))).ToLowerInvariant()[..24];
            nativeNodeByHandle[fact.Handle] = id;
            var parentId = ResolveParentId(fact.ParentHandle, rootHandle, nativeNodeByHandle, facts, rootNodeId);
            var (controlType, localizedControlType) = ControlType(fact.ClassName);
            var isPassword = IsPasswordEdit(fact.ClassName, fact.Style);
            var node = new AccessibilityNodeRecord
            {
                AccessibilityEvidenceId = id,
                ParentEvidenceId = parentId,
                WindowEvidenceId = windowEvidenceId,
                AutomationId = "native:" + path,
                ControlType = controlType,
                LocalizedControlType = localizedControlType,
                Name = NameFor(controlType, fact.Text, isPassword),
                ClassName = fact.ClassName,
                BoundsPx = fact.BoundsPx.Clone(),
                IsEnabled = fact.Enabled,
                IsOffscreen = !fact.Visible,
                IsPassword = isPassword,
                ValueEvidence = ValueFor(controlType, fact.Text, isPassword),
                ProviderId = ProviderId,
                NativeWindowHandleEvidence = fact.Handle.ToString(CultureInfo.InvariantCulture),
                NativeControlIdEvidence = fact.ControlId.ToString(CultureInfo.InvariantCulture),
                ChildCountObserved = directChildren.GetValueOrDefault(fact.Handle)?.Count(child => child.Visible) ?? 0,
                Health = ProtocolHealth.Complete(),
                PropertyCoverage = new PropertyCoverage { Attempted = 7, Read = 7 },
            };
            node.RuntimeIdEvidence.Add(RuntimeIdMarker);
            node.RuntimeIdEvidence.Add(unchecked((int)fact.Handle));
            node.RuntimeIdEvidence.Add(unchecked((int)(fact.Handle >> 32)));
            node.SupportedPatterns.Add("NativeWindow");
            if (IsOpaqueHostedContent(fact.ClassName))
                node.SupportedPatterns.Add("NativeWindow.HostedContentOpaque");
            packet.Nodes.Add(node);
        }

        var includedHandles = included.Select(fact => fact.Handle).ToHashSet();
        foreach (var fact in included)
        {
            var nodeId = nativeNodeByHandle[fact.Handle];
            var node = packet.Nodes.First(value => value.AccessibilityEvidenceId == nodeId);
            node.ChildCountIncluded = directChildren.GetValueOrDefault(fact.Handle)?.Count(child =>
                child.Visible && (includedHandles.Contains(child.Handle) || nativeNodeByHandle.ContainsKey(child.Handle))) ?? 0;
        }

        var limited = candidates.Count > included.Count;
        if (limited)
        {
            packet.NodeLimited = true;
            packet.TraversalFrontier.Add(new TraversalFrontier
            {
                FrontierId = Guid.NewGuid().ToString("N"),
                CaptureWindowId = windowEvidenceId,
                ProviderId = ProviderId,
                ParentEvidenceId = rootNodeId,
                ReasonKnown = true,
                StopReason = "native_window_node_limit",
                EstimatedChildCount = candidates.Count - included.Count,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                TerminalState = "remaining",
            });
        }
        packet.NodesObserved += visible.Count;
        packet.NodesIncluded = packet.Nodes.Count;
        return new NativeAccessibilitySupplementResult(visible.Count, candidates.Count, included.Count, limited);
    }

    private static IReadOnlyList<NativeWindowFact> Enumerate(IntPtr rootWindow)
    {
        if (!NativeAccessibilityMethods.IsWindow(rootWindow))
            throw new InvalidOperationException("Native child-window root is no longer valid.");
        var facts = new List<NativeWindowFact>();
        var order = 0;
        _ = NativeAccessibilityMethods.EnumChildWindows(rootWindow, (handle, _) =>
        {
            if (!NativeAccessibilityMethods.GetWindowRect(handle, out var rect)) return true;
            facts.Add(new NativeWindowFact(
                handle.ToInt64(),
                NativeAccessibilityMethods.GetParent(handle).ToInt64(),
                ReadClass(handle),
                ReadText(handle),
                NativeMethods.ToRect(rect),
                NativeAccessibilityMethods.IsWindowVisible(handle),
                NativeAccessibilityMethods.IsWindowEnabled(handle),
                NativeAccessibilityMethods.GetDlgCtrlID(handle),
                NativeAccessibilityMethods.GetWindowLongPtr(handle, -16).ToInt64(),
                order++));
            return true;
        }, IntPtr.Zero);
        return facts;
    }

    private static Dictionary<long, string> BuildPaths(
        IReadOnlyList<NativeWindowFact> facts,
        IReadOnlyDictionary<long, List<NativeWindowFact>> directChildren,
        long rootHandle)
    {
        var factsByHandle = facts.ToDictionary(fact => fact.Handle);
        var paths = new Dictionary<long, string>();
        foreach (var fact in facts.OrderBy(fact => Depth(fact, facts, rootHandle)).ThenBy(fact => fact.EnumerationOrder))
        {
            var siblings = directChildren.GetValueOrDefault(fact.ParentHandle) ?? [];
            var sameClass = siblings.Where(sibling => EqualsNormalized(sibling.ClassName, fact.ClassName)).ToList();
            var ordinal = Math.Max(0, sameClass.FindIndex(sibling => sibling.Handle == fact.Handle));
            var segment = Segment(fact, ordinal);
            paths[fact.Handle] = fact.ParentHandle == rootHandle
                ? segment
                : paths.TryGetValue(fact.ParentHandle, out var parentPath)
                    ? parentPath + "/" + segment
                    : factsByHandle.ContainsKey(fact.ParentHandle) ? "orphan/" + segment : segment;
        }
        return paths;
    }

    private static string ResolveParentId(
        long parentHandle,
        long rootHandle,
        IReadOnlyDictionary<long, string> nodeByHandle,
        IReadOnlyList<NativeWindowFact> facts,
        string rootNodeId)
    {
        var parentByHandle = facts.ToDictionary(fact => fact.Handle, fact => fact.ParentHandle);
        var visited = new HashSet<long>();
        var current = parentHandle;
        while (current != 0 && current != rootHandle && visited.Add(current))
        {
            if (nodeByHandle.TryGetValue(current, out var nodeId)) return nodeId;
            if (!parentByHandle.TryGetValue(current, out current)) break;
        }
        return nodeByHandle.GetValueOrDefault(rootHandle, rootNodeId);
    }

    private static int Depth(NativeWindowFact fact, IReadOnlyList<NativeWindowFact> facts, long rootHandle)
    {
        var parentByHandle = facts.ToDictionary(value => value.Handle, value => value.ParentHandle);
        var depth = 0;
        var current = fact.ParentHandle;
        var visited = new HashSet<long>();
        while (current != 0 && current != rootHandle && visited.Add(current) && parentByHandle.TryGetValue(current, out current)) depth++;
        return depth;
    }

    private static string Segment(NativeWindowFact fact, int ordinal) =>
        Slug(fact.ClassName) + "[" + ordinal.ToString(CultureInfo.InvariantCulture) + "]";

    private static string Slug(string value)
    {
        var normalized = new string((value ?? string.Empty).Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "window" : normalized;
    }

    private static (string ControlType, string Localized) ControlType(string className)
    {
        var value = className ?? string.Empty;
        if (Contains(value, "RadioButton")) return ("RadioButton", "radio button");
        if (Contains(value, "CheckBox")) return ("CheckBox", "check box");
        if (Contains(value, "Button")) return ("Button", "button");
        if (Contains(value, "Combo", "DropDown")) return ("ComboBox", "combo box");
        if (Contains(value, "Memo", "Edit")) return ("Edit", "edit");
        if (Contains(value, "PageControl")) return ("Tab", "tab");
        if (Contains(value, "TabSheet")) return ("TabItem", "tab item");
        if (Contains(value, "Grid")) return ("DataGrid", "data grid");
        if (Contains(value, "TreeView")) return ("Tree", "tree");
        if (Contains(value, "ListView", "ListBox")) return ("List", "list");
        if (Contains(value, "Static", "Label")) return ("Text", "text");
        if (Contains(value, "ScrollBar")) return ("ScrollBar", "scroll bar");
        return ("Pane", "pane");
    }

    private static bool IsPasswordEdit(string className, long style) =>
        Contains(className, "Edit") && (style & 0x20L) != 0;

    private static bool IsOpaqueHostedContent(string className) =>
        Contains(className, "Grid", "TreeView", "ListView", "ListBox", "Calendar");

    private static string NameFor(string controlType, string text, bool password) =>
        password || controlType is "Edit" or "ComboBox" ? string.Empty : text;

    private static string ValueFor(string controlType, string text, bool password) =>
        !password && controlType is "Edit" or "ComboBox" ? text : string.Empty;

    private static bool Contains(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool SameBounds(RectPx? left, RectPx? right) => left is not null && right is not null
        && left.X == right.X && left.Y == right.Y && left.Width == right.Width && left.Height == right.Height;

    private static bool EqualsNormalized(string? left, string? right) =>
        string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static string ReadText(IntPtr handle)
    {
        var capacity = Math.Clamp(NativeAccessibilityMethods.GetWindowTextLength(handle) + 1, 2, 32_768);
        var builder = new StringBuilder(capacity);
        _ = NativeAccessibilityMethods.GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string ReadClass(IntPtr handle)
    {
        var builder = new StringBuilder(512);
        _ = NativeAccessibilityMethods.GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }
}

public sealed record NativeAccessibilitySupplementResult(int Observed, int MissingFromUia, int Added, bool Limited);

internal sealed record NativeWindowFact(
    long Handle,
    long ParentHandle,
    string ClassName,
    string Text,
    RectPx BoundsPx,
    bool Visible,
    bool Enabled,
    int ControlId,
    long Style,
    int EnumerationOrder);

internal static class NativeAccessibilityMethods
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(IntPtr parent, NativeMethods.EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr window, out NativeMethods.NativeRect rect);

    [DllImport("user32.dll")]
    internal static extern int GetDlgCtrlID(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr window, StringBuilder value, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr window, StringBuilder value, int maxCount);
}
