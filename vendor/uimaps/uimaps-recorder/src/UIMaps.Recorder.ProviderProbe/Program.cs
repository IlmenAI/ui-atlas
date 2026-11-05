using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using Google.Protobuf;
using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;
using UIMaps.Recorder.ProviderProbe;

if (args.Length != 1)
{
    Console.Error.WriteLine("A single encoded provider request is required.");
    return 2;
}

try
{
    _ = SetProcessDpiAwarenessContext(new IntPtr(-4));
    var json = Encoding.UTF8.GetString(Convert.FromBase64String(args[0]));
    var request = JsonSerializer.Deserialize<ProbeRequest>(json) ?? throw new InvalidDataException("Request is empty.");
    IMessage response = request.Operation switch
    {
        "screen" => await CaptureScreenAsync(request),
        "windows" => await CaptureWindowsAsync(request),
        "target-window" => await new NativeWindowTopologyProvider().CaptureWindowAsync(
            ToDisplay(request), request.WindowHandle, RecorderPolicy.Default, CancellationToken.None),
        "native-accessibility" => CaptureNativeAccessibility(request),
        "uia" => CaptureAccessibility(request),
        "uia-cached" => UiaCachedCapture.Capture(request),
        "msaa" => NativeMsaaProvider.Capture(request.WindowHandle, request.WindowEvidenceId, request.MaxNodes, request.MaxDepth),
        "msaa-root-reuse" => NativeMsaaProvider.Capture(request.WindowHandle, request.WindowEvidenceId, request.MaxNodes, request.MaxDepth, reuseClientRoot: true),
        _ => throw new InvalidDataException("Unknown provider operation."),
    };
    Console.Out.Write(Convert.ToBase64String(response.ToByteArray()));
    return 0;
}
catch (UnauthorizedAccessException)
{
    Console.Error.WriteLine("Provider permission denied.");
    return 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.GetType().Name);
    return 1;
}

static async Task<ScreenshotPacket> CaptureScreenAsync(ProbeRequest request)
{
    var display = ToDisplay(request);
    return await new NativeScreenCaptureProvider().CaptureAsync(display, RecorderPolicy.Default, CancellationToken.None);
}

static async Task<WindowTopologyPacket> CaptureWindowsAsync(ProbeRequest request)
{
    var display = ToDisplay(request);
    var policy = RecorderPolicy.Default with { MaxWindows = request.MaxWindows };
    return await new NativeWindowTopologyProvider().CaptureAsync(display, policy, CancellationToken.None);
}

static DisplayDescriptor ToDisplay(ProbeRequest request) => new()
{
    DisplayId = "probe",
    BoundsPx = new RectPx { X = request.X, Y = request.Y, Width = request.Width, Height = request.Height },
    WidthPx = request.Width,
    HeightPx = request.Height,
    DpiX = 96,
    DpiY = 96,
    ScaleFactorX = 1,
    ScaleFactorY = 1,
    Health = ProtocolHealth.Complete(),
};

static AccessibilityPacket CaptureAccessibility(ProbeRequest request)
{
    var packet = new AccessibilityPacket { Health = ProtocolHealth.Complete() };
    var root = AutomationElement.FromHandle(new IntPtr(request.WindowHandle));
    if (root is null)
    {
        packet.Health = ProtocolHealth.Single(HealthState.SensorUnavailable, "RECORDER_UIA_UNAVAILABLE", "No accessibility root was available.", "accessibility");
        return packet;
    }

    var walker = TreeWalker.RawViewWalker;
    var queue = new Queue<(AutomationElement Element, string ParentId, int Depth)>();
    queue.Enqueue((root, string.Empty, 0));
    var visited = new HashSet<string>(StringComparer.Ordinal);
    var observed = 0;
    while (queue.Count > 0)
    {
        var (element, parentId, depth) = queue.Dequeue();
        observed++;
        if (packet.Nodes.Count >= request.MaxNodes)
        {
            packet.NodeLimited = true;
            packet.TraversalFrontier.Add(Frontier(request.WindowEvidenceId, parentId, "per_window_node_limit"));
            break;
        }

        var nodeId = Guid.NewGuid().ToString("N");
        var node = ReadNode(element, nodeId, parentId, request.WindowEvidenceId);
        var signature = node.RuntimeIdEvidence.Count == 0 ? string.Empty : string.Join('.', node.RuntimeIdEvidence);
        if (signature.Length > 0 && !visited.Add(signature))
        {
            packet.TraversalFrontier.Add(Frontier(request.WindowEvidenceId, parentId, "cycle_detected"));
            continue;
        }
        packet.Nodes.Add(node);

        var children = new List<AutomationElement>();
        AutomationElement? child = null;
        try { child = walker.GetFirstChild(element); } catch { }
        while (child is not null)
        {
            children.Add(child);
            try { child = walker.GetNextSibling(child); } catch { child = null; }
        }

        node.ChildCountObserved = children.Count;
        if (depth >= request.MaxDepth)
        {
            if (children.Count > 0)
            {
                packet.DepthLimited = true;
                packet.TraversalFrontier.Add(Frontier(request.WindowEvidenceId, nodeId, "depth_limit", children.Count));
            }
            continue;
        }

        node.ChildCountIncluded = Math.Min(children.Count, Math.Max(0, request.MaxNodes - packet.Nodes.Count));
        for (var index = 0; index < children.Count; index++)
        {
            queue.Enqueue((children[index], nodeId, depth + 1));
        }
    }

    packet.NodesObserved = observed;
    packet.NodesIncluded = packet.Nodes.Count;
    if (packet.NodeLimited)
    {
        packet.Health = ProtocolHealth.Single(HealthState.NodeLimited, "RECORDER_ACCESSIBILITY_NODE_LIMIT", "Accessibility capture reached its UI Automation node limit.", "accessibility");
    }
    else if (packet.DepthLimited)
    {
        packet.Health = ProtocolHealth.Single(HealthState.Partial, "RECORDER_UIA_DEPTH_LIMIT", "Accessibility capture reached its depth limit.", "accessibility");
    }
    return packet;
}

static AccessibilityPacket CaptureNativeAccessibility(ProbeRequest request)
{
    var packet = new AccessibilityPacket { Health = ProtocolHealth.Complete() };
    _ = NativeAccessibilitySupplement.Append(
        packet,
        new IntPtr(request.WindowHandle),
        request.WindowEvidenceId,
        request.MaxNodes);
    if (packet.NodeLimited)
    {
        packet.Health = ProtocolHealth.Single(HealthState.NodeLimited, "RECORDER_NATIVE_ACCESSIBILITY_NODE_LIMIT", "Native accessibility capture reached its node limit.", "accessibility");
    }
    else if (packet.ProviderAttempts.Any(attempt => attempt.Status == "provider_failed"))
    {
        packet.Health = ProtocolHealth.Single(HealthState.ProviderFailed, "RECORDER_NATIVE_ACCESSIBILITY_FAILED", "Native child-window enumeration failed.", "accessibility");
    }
    return packet;
}

static TraversalFrontier Frontier(string windowId, string parentId, string reason, int estimated = 0) => new()
{
    FrontierId = Guid.NewGuid().ToString("N"), CaptureWindowId = windowId, ProviderId = "uia",
    ParentEvidenceId = parentId, ReasonKnown = true, StopReason = reason, EstimatedChildCount = estimated,
    CreatedAt = DateTimeOffset.UtcNow.ToString("O"), TerminalState = "remaining",
};

static AccessibilityNodeRecord ReadNode(AutomationElement element, string id, string parentId, string windowId)
{
    var attempted = 0L;
    var read = 0L;
    var failed = 0L;
    string ReadString(AutomationProperty property)
    {
        attempted++;
        try
        {
            var value = element.GetCurrentPropertyValue(property, true);
            read++;
            return ProviderPropertyValue.ToProtocolString(value, AutomationElement.NotSupported);
        }
        catch { failed++; return string.Empty; }
    }

    string ReadControlType(AutomationProperty property)
    {
        attempted++;
        try
        {
            var value = element.GetCurrentPropertyValue(property, true);
            read++;
            return ProviderPropertyValue.ToControlTypeName(value, AutomationElement.NotSupported);
        }
        catch { failed++; return string.Empty; }
    }

    bool ReadBool(AutomationProperty property)
    {
        attempted++;
        try { var value = element.GetCurrentPropertyValue(property, true) is bool flag && flag; read++; return value; } catch { failed++; return false; }
    }

    var isPassword = ReadBool(AutomationElement.IsPasswordProperty);
    var bounds = Rect.Empty;
    try { bounds = element.Current.BoundingRectangle; } catch { }
    var record = new AccessibilityNodeRecord
    {
        AccessibilityEvidenceId = id,
        ParentEvidenceId = parentId,
        WindowEvidenceId = windowId,
        AutomationId = ReadString(AutomationElement.AutomationIdProperty),
        ControlType = ReadControlType(AutomationElement.ControlTypeProperty),
        LocalizedControlType = ReadString(AutomationElement.LocalizedControlTypeProperty),
        Name = ReadString(AutomationElement.NameProperty),
        ClassName = ReadString(AutomationElement.ClassNameProperty),
        ProviderId = "uia",
        NativeWindowHandleEvidence = ReadString(AutomationElement.NativeWindowHandleProperty),
        BoundsPx = new RectPx { X = (int)Math.Round(bounds.X), Y = (int)Math.Round(bounds.Y), Width = (int)Math.Round(bounds.Width), Height = (int)Math.Round(bounds.Height) },
        IsEnabled = ReadBool(AutomationElement.IsEnabledProperty),
        IsOffscreen = ReadBool(AutomationElement.IsOffscreenProperty),
        IsPassword = isPassword,
        ValueEvidence = string.Empty,
        Health = ProtocolHealth.Complete(),
        PropertyCoverage = new PropertyCoverage(),
    };
    try { record.RuntimeIdEvidence.AddRange(element.GetRuntimeId()); } catch { }
    if (!isPassword)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
            {
                record.ValueEvidence = valuePattern.Current.Value;
            }
        }
        catch { }
    }
    record.PropertyCoverage.Attempted = attempted;
    record.PropertyCoverage.Read = read;
    record.PropertyCoverage.Failed = failed;
    var patterns = new List<AutomationPattern>
    {
        InvokePattern.Pattern,
        ValuePattern.Pattern,
        SelectionPattern.Pattern,
        SelectionItemPattern.Pattern,
        ExpandCollapsePattern.Pattern,
        ScrollPattern.Pattern,
        ScrollItemPattern.Pattern,
        GridPattern.Pattern,
        TablePattern.Pattern,
        TextPattern.Pattern,
    };
    var legacyPattern = AutomationPattern.LookupById(10018);
    if (legacyPattern is not null) patterns.Add(legacyPattern);
    foreach (var pattern in patterns)
    {
        try { if (element.TryGetCurrentPattern(pattern, out _)) record.SupportedPatterns.Add(pattern.ProgrammaticName); } catch { }
    }
    return record;
}

[DllImport("user32.dll")]
static extern bool SetProcessDpiAwarenessContext(IntPtr value);
