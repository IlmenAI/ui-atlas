using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;

namespace UIMaps.Recorder.ProviderProbe;

internal static class UiaCachedCapture
{
    internal static readonly (AutomationPattern Pattern, AutomationProperty Availability)[] Patterns =
    [
        (InvokePattern.Pattern, AutomationElement.IsInvokePatternAvailableProperty),
        (TogglePattern.Pattern, AutomationElement.IsTogglePatternAvailableProperty),
        (GridItemPattern.Pattern, AutomationElement.IsGridItemPatternAvailableProperty),
        (ValuePattern.Pattern, AutomationElement.IsValuePatternAvailableProperty),
        (SelectionPattern.Pattern, AutomationElement.IsSelectionPatternAvailableProperty),
        (SelectionItemPattern.Pattern, AutomationElement.IsSelectionItemPatternAvailableProperty),
        (ExpandCollapsePattern.Pattern, AutomationElement.IsExpandCollapsePatternAvailableProperty),
        (ScrollPattern.Pattern, AutomationElement.IsScrollPatternAvailableProperty),
        (ScrollItemPattern.Pattern, AutomationElement.IsScrollItemPatternAvailableProperty),
        (GridPattern.Pattern, AutomationElement.IsGridPatternAvailableProperty),
        (TablePattern.Pattern, AutomationElement.IsTablePatternAvailableProperty),
        (TextPattern.Pattern, AutomationElement.IsTextPatternAvailableProperty),
    ];
    private static readonly AutomationProperty[] Metadata =
    [
        AutomationElement.IsPasswordProperty, AutomationElement.AutomationIdProperty, AutomationElement.ControlTypeProperty,
        AutomationElement.LocalizedControlTypeProperty, AutomationElement.NameProperty, AutomationElement.ClassNameProperty,
        AutomationElement.NativeWindowHandleProperty, AutomationElement.BoundingRectangleProperty,
        AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty, AutomationElement.RuntimeIdProperty,
    ];

    private static readonly AutomationProperty[] StateProperties =
    [
        AutomationElement.HasKeyboardFocusProperty, SelectionItemPattern.IsSelectedProperty,
        TogglePattern.ToggleStateProperty, ExpandCollapsePattern.ExpandCollapseStateProperty,
        GridItemPattern.RowProperty, GridItemPattern.ColumnProperty,
    ];

    internal static CacheRequest CreateCache()
    {
        var cache = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = Automation.RawViewCondition,
            AutomationElementMode = AutomationElementMode.Full };
        foreach (var property in Metadata.Concat(Patterns.Select(x => x.Availability)).Concat(StateProperties)) cache.Add(property);
        // ValuePattern.ValueProperty is deliberately absent. Password status must
        // be positively observed before any value is requested from the provider.
        return cache;
    }

    internal static AccessibilityPacket Capture(ProbeRequest request, bool readValues = true, bool probeLegacyPattern = true)
    {
        bool HasTime() => request.DeadlineTimestamp == 0 || Stopwatch.GetTimestamp() < request.DeadlineTimestamp;
        if (!HasTime()) return Unavailable(request, "cooperative_deadline", "Accessibility worker had no acquisition time remaining.", timeLimited: true);
        var cache = CreateCache();
        AutomationElement? root;
        try { using (cache.Activate()) root = AutomationElement.FromHandle(new IntPtr(request.WindowHandle)); }
        catch { return Unavailable(request, "root_unavailable", "No accessibility root could be acquired."); }
        if (root is null) return Unavailable(request, "root_unavailable", "No accessibility root was available.");
        return CaptureRoot(root, cache, request, readValues, probeLegacyPattern);
    }

    internal static AccessibilityPacket CaptureRoot(AutomationElement root, CacheRequest cache, ProbeRequest request, bool readValues, bool probeLegacyPattern = true)
    {
        bool HasTime() => request.DeadlineTimestamp == 0 || Stopwatch.GetTimestamp() < request.DeadlineTimestamp;
        var walker = TreeWalker.RawViewWalker;
        return UiaCaptureTraversal.Capture(root, request.WindowEvidenceId, request.MaxNodes, request.MaxDepth,
            (element, id, parent) => ReadNode(property => element.GetCachedPropertyValue(property, true),
                () => element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern value
                    ? value.Current.Value : null,
                pattern => element.TryGetCurrentPattern(pattern, out _), HasTime, id, parent, request.WindowEvidenceId, readValues: readValues, probeLegacyPattern: probeLegacyPattern),
            element => walker.GetFirstChild(element, cache), element => walker.GetNextSibling(element, cache), HasTime);
    }

    internal static AccessibilityNodeRecord ReadNode(Func<AutomationProperty, object?> cached,
        Func<string?> readValue, Func<AutomationPattern, bool> legacyPatternSupported, Func<bool> hasTime,
        string id, string parentId, string windowId, bool readValues = true, bool probeLegacyPattern = true)
    {
        var coverage = new PropertyCoverage();
        var issues = new List<Health>();
        var unsupportedMetadata = 0;
        object? Read(AutomationProperty property, bool patternAvailability = false)
        {
            coverage.Attempted++;
            try
            {
                var value = cached(property);
                if (ReferenceEquals(value, AutomationElement.NotSupported))
                {
                    coverage.Unsupported++;
                    // UIA defines an unsupported pattern-availability property's
                    // default as false. That expected negative is not a failed
                    // observation and does not require a second provider query.
                    if (patternAvailability) return false;
                    unsupportedMetadata++; return null;
                }
                if (value is null) { coverage.Failed++; return null; }
                coverage.Read++; return value;
            }
            catch { coverage.Failed++; return null; }
        }
        bool? Bool(AutomationProperty property, bool patternAvailability = false)
        {
            var value = Read(property, patternAvailability);
            if (value is null) return null;
            if (value is bool flag) return flag;
            coverage.Read--; coverage.Failed++; return null;
        }
        var password = Bool(AutomationElement.IsPasswordProperty);
        var boundsValue = Read(AutomationElement.BoundingRectangleProperty);
        RectPx? bounds = null;
        if (boundsValue is Rect rectangle && !rectangle.IsEmpty && double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y)
            && double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height) && rectangle.Width >= 0 && rectangle.Height >= 0)
        {
            try { bounds = new RectPx { X = checked((int)Math.Round(rectangle.X)), Y = checked((int)Math.Round(rectangle.Y)),
                Width = checked((int)Math.Round(rectangle.Width)), Height = checked((int)Math.Round(rectangle.Height)) }; }
            catch (OverflowException) { coverage.Read--; coverage.Failed++; }
        }
        else if (boundsValue is not null) { coverage.Read--; coverage.Failed++; }
        if (bounds is null) issues.Add(ProtocolHealth.Single(HealthState.Partial, "RECORDER_UIA_GEOMETRY_UNAVAILABLE",
            "Accessibility node geometry was unavailable or could not be represented as finite screen bounds.", "accessibility"));
        var offscreen = Bool(AutomationElement.IsOffscreenProperty);
        if (offscreen is null) issues.Add(ProtocolHealth.Single(HealthState.Partial, "RECORDER_UIA_VISIBLE_STATE_UNKNOWN",
            "The provider did not report whether this element is offscreen.", "accessibility"));
        var node = new AccessibilityNodeRecord
        {
            AccessibilityEvidenceId = id, ParentEvidenceId = parentId, WindowEvidenceId = windowId, ProviderId = "uia",
            AutomationId = Text(AutomationElement.AutomationIdProperty),
            ControlType = ProviderPropertyValue.ToControlTypeName(Read(AutomationElement.ControlTypeProperty), AutomationElement.NotSupported),
            LocalizedControlType = Text(AutomationElement.LocalizedControlTypeProperty), Name = Text(AutomationElement.NameProperty),
            ClassName = Text(AutomationElement.ClassNameProperty), NativeWindowHandleEvidence = Text(AutomationElement.NativeWindowHandleProperty),
            BoundsPx = bounds, IsEnabled = Bool(AutomationElement.IsEnabledProperty) ?? false,
            IsOffscreen = offscreen ?? false, IsPassword = password ?? false,
            PropertyCoverage = coverage,
        };
        var runtime = Read(AutomationElement.RuntimeIdProperty);
        if (runtime is int[] runtimeId) node.RuntimeIdEvidence.AddRange(runtimeId);
        else if (runtime is not null) { coverage.Read--; coverage.Failed++; }
        bool? valueAvailable = null;
        foreach (var (pattern, availability) in Patterns)
        {
            var available = Bool(availability, patternAvailability: true);
            if (available == true) node.SupportedPatterns.Add(pattern.ProgrammaticName);
            if (pattern == ValuePattern.Pattern) valueAvailable = available;
        }
        // Optional state is sampled with the same cache. Unsupported is absent;
        // it must not make an ordinary node unhealthy merely for lacking a pattern.
        if (Read(AutomationElement.HasKeyboardFocusProperty, true) is bool focused) node.HasKeyboardFocus = focused;
        if (node.SupportedPatterns.Contains(SelectionItemPattern.Pattern.ProgrammaticName)
            && Read(SelectionItemPattern.IsSelectedProperty, true) is bool selected) node.IsSelected = selected;
        if (node.SupportedPatterns.Contains(TogglePattern.Pattern.ProgrammaticName)
            && Read(TogglePattern.ToggleStateProperty, true) is ToggleState toggle) node.ToggleState = (int)toggle;
        if (node.SupportedPatterns.Contains(ExpandCollapsePattern.Pattern.ProgrammaticName)
            && Read(ExpandCollapsePattern.ExpandCollapseStateProperty, true) is ExpandCollapseState expanded) node.ExpandCollapseState = (int)expanded;
        if (node.SupportedPatterns.Contains(GridItemPattern.Pattern.ProgrammaticName))
        {
            if (Read(GridItemPattern.RowProperty, true) is int row && row >= 0) node.GridRow = row;
            if (Read(GridItemPattern.ColumnProperty, true) is int column && column >= 0) node.GridColumn = column;
        }
        // The managed UIA API has no registered legacy availability property on
        // all supported runtimes. Preserve its existing optional pattern probe.
        if (probeLegacyPattern && AutomationPattern.LookupById(10018) is { } legacy && hasTime())
        {
            coverage.Attempted++;
            try { if (legacyPatternSupported(legacy)) node.SupportedPatterns.Add(legacy.ProgrammaticName); coverage.Read++; }
            catch { coverage.Failed++; }
        }
        if (!probeLegacyPattern) issues.Add(ProtocolHealth.Single(HealthState.Partial,
            "RECORDER_UIA_LEGACY_PATTERN_NOT_QUERIED", "Periodic census retained cached standard capabilities; the optional live legacy-pattern query was omitted.", "accessibility"));
        if (password != false)
        {
            coverage.ProtectedOrRedacted++;
            if (password is null) issues.Add(ProtocolHealth.Single(HealthState.Partial, "RECORDER_UIA_PASSWORD_STATUS_UNKNOWN",
                "Value evidence was not requested because password status was unavailable.", "accessibility"));
        }
        else if (readValues && valueAvailable != false)
        {
            if (hasTime())
            {
                coverage.Attempted++;
                try { if (readValue() is { } value) { node.ValueEvidence = value; coverage.Read++; } else coverage.Unsupported++; }
                catch { coverage.Failed++; }
            }
            else issues.Add(ProtocolHealth.Single(HealthState.Partial, "RECORDER_UIA_COOPERATIVE_DEADLINE",
                "Value evidence was not requested after the cooperative deadline.", "accessibility", true));
        }
        if (coverage.Failed > 0 || unsupportedMetadata > 0) issues.Add(ProtocolHealth.Single(HealthState.Partial,
            "RECORDER_UIA_PROPERTY_PARTIAL", "Some requested accessibility properties or pattern availability evidence were unavailable.", "accessibility"));
        node.Health = ProtocolHealth.Aggregate(issues.Append(ProtocolHealth.Complete()));
        return node;

        string Text(AutomationProperty property) => ProviderPropertyValue.ToProtocolString(Read(property), AutomationElement.NotSupported);
    }

    private static AccessibilityPacket Unavailable(ProbeRequest request, string reason, string message, bool timeLimited = false)
    {
        var packet = new AccessibilityPacket { TimeLimited = timeLimited, Health = ProtocolHealth.Single(timeLimited ? HealthState.Partial : HealthState.SensorUnavailable,
            timeLimited ? "RECORDER_UIA_COOPERATIVE_DEADLINE" : "RECORDER_UIA_UNAVAILABLE", message, "accessibility", true) };
        packet.TraversalFrontier.Add(UiaCaptureTraversal.Frontier(request.WindowEvidenceId, "", reason));
        return packet;
    }
}
