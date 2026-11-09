using System.Windows;
using System.Windows.Automation;
using UIMaps.Protocol.V1;
using UIMaps.Recorder.ProviderProbe;

namespace UiAtlas.Core.Windows.Tests;

public sealed class UiaCachedCaptureTests
{
    [Fact]
    public void PeriodicCensusRetainsCachedCapabilitiesWithoutLiveLegacyOrValueQueries()
    {
        var values = Metadata();
        values[AutomationElement.IsInvokePatternAvailableProperty] = true;
        values[AutomationElement.IsValuePatternAvailableProperty] = true;
        var liveCalls = 0;
        var node = UiaCachedCapture.ReadNode(property => values[property],
            () => { liveCalls++; return "must not read"; }, _ => { liveCalls++; return true; },
            () => true, "node", "parent", "window", readValues: false, probeLegacyPattern: false);
        Assert.Equal(0, liveCalls);
        Assert.Equal("parent", node.ParentEvidenceId);
        Assert.Equal("123", node.NativeWindowHandleEvidence);
        Assert.Equal(100, node.BoundsPx.Width);
        Assert.Contains(InvokePattern.Pattern.ProgrammaticName, node.SupportedPatterns);
        Assert.Contains(ValuePattern.Pattern.ProgrammaticName, node.SupportedPatterns);
        Assert.Empty(node.ValueEvidence);
        Assert.Contains(node.Health.Issues, i => i.Code == "RECORDER_UIA_LEGACY_PATTERN_NOT_QUERIED");
        Assert.Equal(0, node.PropertyCoverage.Failed);
    }

    [Fact]
    public void NonContentStatePreservesPresenceAndNeverReadsFieldValues()
    {
        var values = Metadata();
        values[AutomationElement.HasKeyboardFocusProperty] = true;
        values[AutomationElement.IsTogglePatternAvailableProperty] = true;
        values[TogglePattern.ToggleStateProperty] = ToggleState.On;
        values[AutomationElement.IsGridItemPatternAvailableProperty] = true;
        values[GridItemPattern.RowProperty] = 0;
        values[GridItemPattern.ColumnProperty] = 1;
        var node = UiaCachedCapture.ReadNode(property => values[property], () => throw new Exception("No values"),
            _ => false, () => true, "node", "parent", "window", readValues: false);
        Assert.True(node.HasHasKeyboardFocus); Assert.True(node.HasKeyboardFocus);
        Assert.True(node.HasToggleState); Assert.Equal(1, node.ToggleState);
        Assert.True(node.HasGridRow); Assert.Equal(0, node.GridRow); Assert.Equal(1, node.GridColumn);
        Assert.False(node.HasIsSelected); Assert.Empty(node.ValueEvidence);
        var absent = Read(Metadata(), () => null);
        Assert.False(absent.HasToggleState); Assert.False(absent.HasGridRow);
    }
    [Fact]
    public void BulkMetadataRetainsOriginalFieldsAndPatternNamesWithoutReadingNonValuePatterns()
    {
        var values = Metadata();
        values[AutomationElement.IsInvokePatternAvailableProperty] = true;
        values[AutomationElement.IsValuePatternAvailableProperty] = true;
        values[AutomationElement.IsTextPatternAvailableProperty] = true;
        var valueReads = 0;

        var node = Read(values, () => { valueReads++; return "observed value"; });

        Assert.Equal("fixture-id", node.AutomationId);
        Assert.Equal("Edit", node.ControlType);
        Assert.Equal("edit", node.LocalizedControlType);
        Assert.Equal("Fixture field", node.Name);
        Assert.Equal("FixtureEdit", node.ClassName);
        Assert.Equal("123", node.NativeWindowHandleEvidence);
        Assert.Equal(new RectPx { X = 10, Y = 20, Width = 100, Height = 30 }, node.BoundsPx);
        Assert.True(node.IsEnabled);
        Assert.False(node.IsOffscreen);
        Assert.Equal(new[] { 42, 123 }, node.RuntimeIdEvidence);
        Assert.Equal("observed value", node.ValueEvidence);
        Assert.Equal(1, valueReads);
        Assert.Contains(InvokePattern.Pattern.ProgrammaticName, node.SupportedPatterns);
        Assert.Contains(ValuePattern.Pattern.ProgrammaticName, node.SupportedPatterns);
        Assert.Contains(TextPattern.Pattern.ProgrammaticName, node.SupportedPatterns);
        Assert.DoesNotContain(GridPattern.Pattern.ProgrammaticName, node.SupportedPatterns);
        Assert.Equal(HealthState.Complete, node.Health.State);
        Assert.Equal(0, node.PropertyCoverage.Failed);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("unsupported")]
    [InlineData("wrong-type")]
    [InlineData("exception")]
    public void PasswordValueReadsFailClosedWhenProtectedOrStatusIsUnknown(string status)
    {
        var values = Metadata();
        values[AutomationElement.IsValuePatternAvailableProperty] = true;
        values[AutomationElement.IsPasswordProperty] = status switch
        {
            "password" => true,
            "wrong-type" => "false",
            _ => AutomationElement.NotSupported,
        };
        var valueReads = 0;
        var node = UiaCachedCapture.ReadNode(property => status == "exception" && property == AutomationElement.IsPasswordProperty
                ? throw new InvalidOperationException("Injected property failure.") : values[property],
            () => { valueReads++; return "must not be read"; }, _ => false, () => true, "node", "parent", "window");

        Assert.Equal(0, valueReads);
        Assert.Empty(node.ValueEvidence);
        Assert.Equal(1, node.PropertyCoverage.ProtectedOrRedacted);
        if (status != "password")
        {
            Assert.Equal(HealthState.Partial, node.Health.State);
            Assert.Contains(node.Health.Issues, issue => issue.Code == "RECORDER_UIA_PASSWORD_STATUS_UNKNOWN");
        }
    }

    [Fact]
    public void NoValuePatternAvoidsAnUnnecessaryProviderValueQuery()
    {
        var node = Read(Metadata(), () => throw new InvalidOperationException("No ValuePattern was available."));
        Assert.Empty(node.ValueEvidence);
        Assert.Equal(HealthState.Complete, node.Health.State);
    }

    [Fact]
    public void FocusedMetadataModeRetainsValueCapabilityWithoutRequestingItsContents()
    {
        var values = Metadata(); values[AutomationElement.IsValuePatternAvailableProperty] = true;
        var node = UiaCachedCapture.ReadNode(property => values[property],
            () => throw new InvalidOperationException("Focused policy must never request field values."), _ => false,
            () => true, "node", "parent", "window", readValues: false);
        Assert.Empty(node.ValueEvidence); Assert.Contains(ValuePattern.Pattern.ProgrammaticName, node.SupportedPatterns);
        Assert.Equal(HealthState.Complete, node.Health.State);
    }

    [Fact]
    public void UnsupportedAvailabilityUsesDocumentedFalseWithoutASecondProviderQueryOrDegradedHealth()
    {
        var values = Metadata();
        values[AutomationElement.IsValuePatternAvailableProperty] = AutomationElement.NotSupported;
        values[AutomationElement.IsInvokePatternAvailableProperty] = AutomationElement.NotSupported;
        var node = Read(values, () => throw new InvalidOperationException("Unsupported availability defaults to false."));

        Assert.Empty(node.ValueEvidence);
        Assert.DoesNotContain(ValuePattern.Pattern.ProgrammaticName, node.SupportedPatterns);
        Assert.DoesNotContain(InvokePattern.Pattern.ProgrammaticName, node.SupportedPatterns);
        Assert.Equal(2, node.PropertyCoverage.Unsupported);
        Assert.Equal(0, node.PropertyCoverage.Failed);
        Assert.Equal(HealthState.Complete, node.Health.State);
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("wrong-type")]
    [InlineData("null")]
    public void FailedAvailabilityRemainsUnknownAndPreservesValueFallback(string failure)
    {
        var values = Metadata();
        values[AutomationElement.IsValuePatternAvailableProperty] = failure == "wrong-type" ? "false" : null;
        var valueReads = 0;
        var node = UiaCachedCapture.ReadNode(property => property == AutomationElement.IsValuePatternAvailableProperty && failure == "exception"
                ? throw new InvalidOperationException("Injected availability failure.") : values[property],
            () => { valueReads++; return "preserved value"; }, _ => false, () => true, "node", "parent", "window");

        Assert.Equal(1, valueReads);
        Assert.Equal("preserved value", node.ValueEvidence);
        Assert.Equal(1, node.PropertyCoverage.Failed);
        Assert.Equal(HealthState.Partial, node.Health.State);
    }

    [Fact]
    public void DeadlineStillRetainsCachedMetadataButDoesNotStartAValueRead()
    {
        var values = Metadata(); values[AutomationElement.IsValuePatternAvailableProperty] = true;
        var node = UiaCachedCapture.ReadNode(property => values[property],
            () => throw new InvalidOperationException("Deadline must prevent a live provider value read."), _ => false,
            () => false, "node", "parent", "window");

        Assert.Equal("Fixture field", node.Name);
        Assert.Empty(node.ValueEvidence);
        Assert.Contains(node.Health.Issues, issue => issue.Code == "RECORDER_UIA_COOPERATIVE_DEADLINE");
    }

    [Fact]
    public void MissingGeometryAndFailedPropertyAreExplicitlyPartialRatherThanInventedOriginBounds()
    {
        var values = Metadata(); values[AutomationElement.BoundingRectangleProperty] = Rect.Empty;
        var node = UiaCachedCapture.ReadNode(property => property == AutomationElement.NameProperty
                ? throw new InvalidOperationException("Injected failure.") : values[property],
            () => null, _ => false, () => true, "node", "parent", "window");

        Assert.Null(node.BoundsPx);
        Assert.Empty(node.Name);
        Assert.Equal(2, node.PropertyCoverage.Failed);
        Assert.Equal(HealthState.Partial, node.Health.State);
        Assert.Contains(node.Health.Issues, issue => issue.Code == "RECORDER_UIA_PROPERTY_PARTIAL");
        Assert.Contains(node.Health.Issues, issue => issue.Code == "RECORDER_UIA_GEOMETRY_UNAVAILABLE"
            && issue.State == HealthState.Partial && issue.Source == "accessibility");
    }

    [Fact]
    public void CacheUsesRawElementMetadataAndNeverBulkRequestsValueContents()
    {
        var cache = UiaCachedCapture.CreateCache();
        Assert.Equal(TreeScope.Element, cache.TreeScope);
        Assert.Same(Automation.RawViewCondition, cache.TreeFilter);
        Assert.Equal(AutomationElementMode.Full, cache.AutomationElementMode);
        // The metadata seam above independently proves that protected or unknown
        // fields never invoke the separate provider value delegate.
    }

    [Fact]
    public void EmptyValueIsAnObservationWhileUnavailableValueHasNoPresence()
    {
        var values = Metadata();
        values[AutomationElement.IsValuePatternAvailableProperty] = true;

        var empty = Read(values, () => "");
        var missing = Read(values, () => null);

        Assert.True(empty.HasValueEvidence);
        Assert.Equal("", empty.ValueEvidence);
        Assert.True(empty.HasIsPassword);
        Assert.False(empty.IsPassword);
        Assert.False(missing.HasValueEvidence);
        Assert.Equal(0, missing.PropertyCoverage.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitReadOnlyStatePreservesFalseAndTrue(bool readOnly)
    {
        var values = Metadata();
        values[AutomationElement.IsValuePatternAvailableProperty] = true;
        values[ValuePattern.IsReadOnlyProperty] = readOnly;

        var node = Read(values, () => "value");

        Assert.True(node.HasIsReadOnly);
        Assert.Equal(readOnly, node.IsReadOnly);
    }

    [Fact]
    public void UnsupportedReadOnlyStateRemainsUnknown()
    {
        var values = Metadata();
        values[AutomationElement.IsValuePatternAvailableProperty] = true;
        values[ValuePattern.IsReadOnlyProperty] = AutomationElement.NotSupported;

        var node = Read(values, () => "value");

        Assert.False(node.HasIsReadOnly);
        Assert.Equal(0, node.PropertyCoverage.Failed);
    }

    [Theory]
    [InlineData("range")]
    [InlineData("document")]
    public void RangeAndDocumentCapabilitiesAllowTheirValueRead(string kind)
    {
        var values = Metadata();
        values[kind == "range" ? AutomationElement.IsRangeValuePatternAvailableProperty :
            AutomationElement.IsTextPatternAvailableProperty] = true;
        values[AutomationElement.ControlTypeProperty] = kind == "range" ? ControlType.Slider : ControlType.Document;
        if (kind == "range") values[RangeValuePattern.IsReadOnlyProperty] = false;
        var reads = 0;

        var node = Read(values, () => { reads++; return "observed"; });

        Assert.Equal(1, reads);
        Assert.True(node.HasValueEvidence);
        Assert.Equal("observed", node.ValueEvidence);
        Assert.DoesNotContain(ValuePattern.Pattern.ProgrammaticName, node.SupportedPatterns);
        if (kind == "range") { Assert.True(node.HasIsReadOnly); Assert.False(node.IsReadOnly); }
    }

    [Theory]
    [InlineData("known", true)]
    [InlineData("password", false)]
    [InlineData("unknown", false)]
    public void SelectionReadsRequirePositivelyKnownNonPasswordState(string privacy, bool expectedRead)
    {
        var values = Metadata();
        values[AutomationElement.IsSelectionPatternAvailableProperty] = true;
        values[AutomationElement.IsPasswordProperty] = privacy switch
        {
            "known" => false,
            "password" => true,
            _ => AutomationElement.NotSupported
        };
        var reads = 0;

        var node = UiaCachedCapture.ReadNode(property => values[property], () => null, _ => false,
            () => true, "node", "parent", "window", readSelection: () => { reads++; return ""; });

        Assert.Equal(expectedRead ? 1 : 0, reads);
        Assert.Equal(expectedRead, node.HasSelectionEvidence);
        if (expectedRead) Assert.Equal("", node.SelectionEvidence);
        if (privacy == "unknown") Assert.False(node.HasIsPassword);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void MetadataOnlyOrExpiredBudgetNeverStartsSelectionRead(bool readValues, bool hasTime)
    {
        var values = Metadata();
        values[AutomationElement.IsSelectionPatternAvailableProperty] = true;
        var reads = 0;

        var node = UiaCachedCapture.ReadNode(property => values[property], () => null, _ => false,
            () => hasTime, "node", "parent", "window", readValues: readValues,
            readSelection: () => { reads++; return "must not read"; });

        Assert.Equal(0, reads);
        Assert.False(node.HasSelectionEvidence);
    }

    private static AccessibilityNodeRecord Read(Dictionary<AutomationProperty, object?> values, Func<string?> value) =>
        UiaCachedCapture.ReadNode(property => values[property], value, _ => false, () => true, "node", "parent", "window");

    private static Dictionary<AutomationProperty, object?> Metadata()
    {
        var values = new Dictionary<AutomationProperty, object?>
        {
            [AutomationElement.IsPasswordProperty] = false,
            [ValuePattern.IsReadOnlyProperty] = AutomationElement.NotSupported,
            [RangeValuePattern.IsReadOnlyProperty] = AutomationElement.NotSupported,
            [AutomationElement.HasKeyboardFocusProperty] = false,
            [AutomationElement.AutomationIdProperty] = "fixture-id",
            [AutomationElement.ControlTypeProperty] = ControlType.Edit,
            [AutomationElement.LocalizedControlTypeProperty] = "edit",
            [AutomationElement.NameProperty] = "Fixture field",
            [AutomationElement.ClassNameProperty] = "FixtureEdit",
            [AutomationElement.NativeWindowHandleProperty] = 123,
            [AutomationElement.BoundingRectangleProperty] = new Rect(10, 20, 100, 30),
            [AutomationElement.IsEnabledProperty] = true,
            [AutomationElement.IsOffscreenProperty] = false,
            [AutomationElement.RuntimeIdProperty] = new[] { 42, 123 },
        };
        foreach (var (_, property) in UiaCachedCapture.Patterns) values[property] = false;
        return values;
    }
}
