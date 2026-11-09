using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Tests;

public sealed class FieldObservationTrackerTests
{
    [Fact]
    public void EmptyIsObservedButMissingValueDoesNotCreateAChange()
    {
        var tracker = new FieldObservationTracker();
        var field = Field("field", value: null, available: false);
        Assert.False(Assert.Single(tracker.Observe(Frame(1, field)).Fields).ValueAvailable);

        var empty = Assert.Single(tracker.Observe(Frame(2, field with { Value = "", ValueAvailable = true })).Fields);
        Assert.True(empty.ValueAvailable);
        Assert.Equal("", empty.BeforeValue);
        Assert.Equal(FieldObservationState.Suggested, empty.State);
        Assert.DoesNotContain(empty.PropertyChanges, change => change.PropertyName == "Value");

        var missing = Assert.Single(tracker.Observe(Frame(3, field)).Fields);
        Assert.False(missing.ValueAvailable);
        Assert.Null(missing.AfterValue);
        Assert.Equal(FieldObservationState.Suggested, missing.State);

        var changed = Assert.Single(tracker.Observe(Frame(4, field with { Value = "Alex", ValueAvailable = true })).Fields);
        var change = Assert.Single(changed.PropertyChanges, change => change.PropertyName == "Value");
        Assert.Equal("", change.Before);
        Assert.Equal("Alex", change.After);
        Assert.Equal(2, change.BeforeFrameSequence);
        Assert.Equal(4, change.AfterFrameSequence);
        Assert.Equal(FieldObservationState.ChangeObserved, changed.State);
    }

    [Fact]
    public void PassiveSequenceZeroUsesAcquisitionTimeAndSnapshotsAreImmutable()
    {
        var tracker = new FieldObservationTracker();
        var field = Field("field", value: "before");
        var before = tracker.Observe(Frame(0, field));
        var laterFrame = Frame(0, field with { Value = "after", RuntimeId = "new-evidence-reference" }) with
        { TimestampUtc = Epoch.AddSeconds(1) };
        var after = Assert.Single(tracker.Observe(laterFrame).Fields);
        Assert.Equal(Assert.Single(before.Fields).Id, after.Id);
        Assert.Equal("before", Assert.Single(before.Fields).AfterValue);
        Assert.Empty(Assert.Single(before.Fields).PropertyChanges);
        Assert.Equal("new-evidence-reference", after.ControlRuntimeId);
        Assert.Equal(Epoch.AddSeconds(1), after.LastObservedUtc);
        Assert.Equal(1, after.PropertyChanges.Count(change => change.PropertyName == "Value"));
        Assert.Equal(after.PropertyChanges.Count, Assert.Single(tracker.Observe(laterFrame).Fields).PropertyChanges.Count);
    }

    [Fact]
    public void PasswordsSecretsAndReadOnlyControlsAreExcludedAndUnknownPrivacyCannotExposeValues()
    {
        var tracker = new FieldObservationTracker();
        var snapshot = tracker.Observe(Frame(1,
            Field("password", value: "secret") with { IsPassword = true },
            Field("api-key", value: "credential") with { Name = "API key" },
            Field("readonly", value: "fixed") with { IsReadOnly = true },
            Field("unknown", value: "unverified secret") with { IsPassword = null, Name = "unverified secret" }));
        var field = Assert.Single(snapshot.Fields);
        Assert.False(field.ValueAvailable);
        Assert.Null(field.BeforeValue);
        Assert.Null(field.AfterValue);
        Assert.Equal("Text field", field.Label);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
        Assert.Null(tracker.MarkAtPoint(Frame(2, Field("password", value: "secret") with { IsPassword = true }), 20, 20));
    }

    [Fact]
    public void LaterPasswordClassificationRemovesEarlierFieldEvidence()
    {
        var tracker = new FieldObservationTracker();
        var field = Field("entry", value: "before");
        tracker.Observe(Frame(1, field));
        Assert.Empty(tracker.Observe(Frame(2, field with { IsPassword = true, Value = "secret" })).Fields);
    }

    [Fact]
    public void RepeatedAutomationIdsAreQualifiedByTheirParents()
    {
        var tracker = new FieldObservationTracker();
        var firstParent = Parent("left", "left-section");
        var secondParent = Parent("right", "right-section");
        var left = Field("left-value", value: "L") with { AutomationId = "value", ProviderRuntimeId = null, ParentRuntimeId = "left" };
        var right = Field("right-value", value: "R") with { AutomationId = "value", ProviderRuntimeId = null, ParentRuntimeId = "right" };
        var initial = tracker.Observe(Frame(1, firstParent, secondParent, left, right));
        Assert.Equal(2, initial.Fields.Count);
        Assert.NotEqual(initial.Fields[0].Id, initial.Fields[1].Id);

        var next = tracker.Observe(Frame(2, firstParent, secondParent, left,
            right with { Value = "changed", RuntimeId = "right-new-evidence" }));
        Assert.Equal(2, next.Fields.Count);
        Assert.Single(next.Fields, field => field.State == FieldObservationState.ChangeObserved);
        Assert.Equal("changed", next.Fields.Single(field => field.State == FieldObservationState.ChangeObserved).AfterValue);
    }

    [Fact]
    public void AmbiguousSiblingsAndCoordinatesNeverProduceCrossCaptureChangeEvidence()
    {
        var tracker = new FieldObservationTracker();
        var first = Field("first", value: "a") with { AutomationId = "duplicate", ProviderRuntimeId = null };
        var second = Field("second", value: "b") with
        { AutomationId = "duplicate", ProviderRuntimeId = null, Bounds = new(10, 50, 120, 24) };
        tracker.Observe(Frame(1, first, second));
        var snapshot = tracker.Observe(Frame(2,
            first with { RuntimeId = "new-first", Value = "c" }, second with { RuntimeId = "new-second", Value = "d" }));
        Assert.Equal(2, snapshot.Fields.Count);
        Assert.All(snapshot.Fields, field =>
        {
            Assert.Equal(FieldObservationState.Suggested, field.State);
            Assert.Empty(field.PropertyChanges);
            Assert.Contains("Observation only", field.IdentityBasis, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SelectionToggleRangeAndDateCapabilitiesBecomeCandidates()
    {
        var tracker = new FieldObservationTracker();
        var selection = Field("selection", value: null, available: false) with
        { ControlType = "ControlType.ComboBox", SupportedPatterns = ["Selection"], SelectionValue = "A", SelectionAvailable = true };
        var toggle = Field("toggle", value: null, available: false) with
        { ControlType = "ControlType.CheckBox", SupportedPatterns = ["Toggle"], ToggleState = "Off" };
        var range = Field("range", value: "3") with
        { ControlType = "ControlType.Custom", SupportedPatterns = ["RangeValuePatternIdentifiers.Pattern"] };
        var date = Field("date", value: "2026-11-09") with { ClassName = "SysDateTimePick32" };
        var first = tracker.Observe(Frame(1, selection, toggle, range, date));
        Assert.Contains(first.Fields, field => field.Kind == FieldKind.Selection);
        Assert.Contains(first.Fields, field => field.Kind == FieldKind.Toggle);
        Assert.Contains(first.Fields, field => field.Kind == FieldKind.Number);
        Assert.Contains(first.Fields, field => field.Kind == FieldKind.Date);
        var next = tracker.Observe(Frame(2,
            selection with { SelectionValue = "B" }, toggle with { ToggleState = "On" }, range, date));
        Assert.Equal(2, next.Fields.Count(field => field.State == FieldObservationState.ChangeObserved));
        Assert.Contains(next.Fields.SelectMany(field => field.PropertyChanges), change => change.PropertyName == "Selection");
        Assert.Contains(next.Fields.SelectMany(field => field.PropertyChanges), change => change.PropertyName == "ToggleState");
    }

    [Fact]
    public void ManualMarkingCanRetainUnsupportedControlWithoutInventingAValue()
    {
        var tracker = new FieldObservationTracker();
        var custom = Field("custom", value: "not available", available: false) with
        { ControlType = "ControlType.Custom", SupportedPatterns = [] };
        Assert.Empty(tracker.Observe(Frame(1, custom)).Fields);
        var marked = Assert.IsType<FieldObservation>(tracker.MarkAtPoint(Frame(1, custom), 20, 20));
        Assert.True(marked.IsManual);
        Assert.Equal(FieldKind.Other, marked.Kind);
        Assert.False(marked.ValueAvailable);
        Assert.True(tracker.SetDecision(marked.Id, FieldReviewDecision.Keep));
        Assert.True(tracker.Rename(marked.Id, "Custom field"));
        var changedName = Assert.Single(tracker.Observe(Frame(2, custom with { Name = "changed label" })).Fields);
        Assert.Equal("Custom field", changedName.Label);
        Assert.Equal(FieldReviewDecision.Keep, changedName.Decision);
        Assert.Equal(FieldObservationState.Suggested, changedName.State);
        Assert.Null(changedName.AfterValue);
        Assert.Contains(changedName.PropertyChanges, change => change.PropertyName == "Name");
    }

    [Fact]
    public void ChangedMetadataIsEvidenceWithoutBecomingAnIgnoredIdentityRule()
    {
        var tracker = new FieldObservationTracker();
        var field = Field("field", value: "same");
        tracker.Observe(Frame(1, field));
        var next = Assert.Single(tracker.Observe(Frame(2, field with { Name = "New label", AutomationId = "new-id" })).Fields);
        Assert.Equal(FieldObservationState.Suggested, next.State);
        Assert.Contains(next.PropertyChanges, change => change.PropertyName == "Name");
        Assert.Contains(next.PropertyChanges, change => change.PropertyName == "AutomationId");
        Assert.Contains("Value", next.UnchangedProperties);
        Assert.DoesNotContain("AutomationId", next.UnchangedProperties);
        tracker.SetDecision(next.Id, FieldReviewDecision.Later);
        Assert.Equal(2, Assert.Single(tracker.Snapshot().Fields).PropertyChanges.Count);
    }

    [Fact]
    public void FieldsAreScopedToTheObservedWindowAndHiddenControlsAreNotSuggested()
    {
        var tracker = new FieldObservationTracker();
        Assert.Empty(tracker.Observe(Frame(1,
            Field("outside", value: "outside") with { WindowHwnd = 900 },
            Field("unverified", value: "unverified") with { ScopeStatus = "unverified" },
            Field("hidden", value: "hidden") with { IsOffscreen = true })).Fields);
        var initial = Assert.Single(tracker.Observe(Frame(2, Field("visible", value: "A"))).Fields);
        // No highlight-visibility argument exists: observing does not depend on any display layer.
        var changed = Assert.Single(tracker.Observe(Frame(3, Field("visible", value: "B"))).Fields);
        Assert.Equal(initial.Id, changed.Id);
        Assert.Equal(FieldObservationState.ChangeObserved, changed.State);
    }

    [Fact]
    public void CompleteDisappearanceResetsComparisonButPartialAbsenceDoesNot()
    {
        var complete = new FieldObservationTracker();
        var field = Field("field", value: "first instance");
        complete.Observe(Frame(1, field));
        complete.Observe(Frame(2));
        var returned = Assert.Single(complete.Observe(Frame(3, field with { Value = "replacement" })).Fields);
        Assert.Equal(FieldObservationState.Suggested, returned.State);
        Assert.Empty(returned.PropertyChanges);
        Assert.Equal("replacement", returned.BeforeValue);

        var partial = new FieldObservationTracker();
        partial.Observe(Frame(1, field));
        partial.Observe(Frame(2) with { AutomationStatus = "partial" });
        var changed = Assert.Single(partial.Observe(Frame(3, field with { Value = "new value" })).Fields);
        Assert.Equal(FieldObservationState.ChangeObserved, changed.State);
        Assert.Single(changed.PropertyChanges, change => change.PropertyName == "Value");
    }

    [Fact]
    public void EditableDocumentTextPatternIsSuggestedWithoutInventingUnavailableText()
    {
        var tracker = new FieldObservationTracker();
        var document = Field("document", value: null, available: false) with
        { ControlType = "ControlType.Document", SupportedPatterns = ["TextPatternIdentifiers.Pattern"] };
        var suggested = Assert.Single(tracker.Observe(Frame(1, document)).Fields);
        Assert.Equal(FieldKind.Text, suggested.Kind);
        Assert.False(suggested.ValueAvailable);
        Assert.Equal(FieldObservationState.Suggested, suggested.State);
    }

    [Fact]
    public void CompleteObservationOfAnotherProviderDoesNotBreakThisProvidersBaseline()
    {
        var tracker = new FieldObservationTracker();
        var field = Field("field", "before");
        tracker.Observe(Frame(1, field));
        tracker.Observe(Frame(2) with
        {
            RawStreams = [new("msaa-sample", "msaa", Epoch.AddSeconds(2), Epoch.AddSeconds(2), "ok", [], TargetWindowHwnd: 100)]
        });
        var changed = Assert.Single(tracker.Observe(Frame(3, field with { Value = "after" })).Fields);
        Assert.Equal(FieldObservationState.ChangeObserved, changed.State);

        var reset = new FieldObservationTracker();
        reset.Observe(Frame(1, field));
        reset.Observe(Frame(2) with
        {
            RawStreams = [new("uia-sample", "uia-cached", Epoch.AddSeconds(2), Epoch.AddSeconds(2), "ok", [], TargetWindowHwnd: 100)]
        });
        var returned = Assert.Single(reset.Observe(Frame(3, field with { Value = "replacement" })).Fields);
        Assert.Equal(FieldObservationState.Suggested, returned.State);
    }

    [Fact]
    public void MarkingAnUnobservedFieldDoesNotSubstituteItsWholeWindowOrPane()
    {
        var tracker = new FieldObservationTracker();
        var window = Parent("window", "window") with { ControlType = "ControlType.Window" };
        var pane = Parent("pane", "pane") with { ParentRuntimeId = "window" };
        Assert.Null(tracker.MarkAtPoint(Frame(1, window, pane), 20, 20));
        Assert.Empty(tracker.Snapshot().Fields);
    }

    [Fact]
    public void SelectionAndToggleChangesAreObservedEvenWhenAValuePatternStaysConstant()
    {
        var tracker = new FieldObservationTracker();
        var toggle = Field("check", "constant caption") with
        { ControlType = "ControlType.CheckBox", SupportedPatterns = ["Value", "Toggle"], ToggleState = "Off" };
        var selection = Field("choice", "constant caption") with
        { ControlType = "ControlType.ComboBox", SupportedPatterns = ["Value", "Selection"], SelectionValue = "A", SelectionAvailable = true };
        tracker.Observe(Frame(1, toggle, selection));
        var changed = tracker.Observe(Frame(2, toggle with { ToggleState = "On" }, selection with { SelectionValue = "B" }));
        Assert.All(changed.Fields, field => Assert.Equal(FieldObservationState.ChangeObserved, field.State));
        Assert.Equal("On", changed.Fields.Single(field => field.Kind == FieldKind.Toggle).AfterValue);
        Assert.Equal("B", changed.Fields.Single(field => field.Kind == FieldKind.Selection).AfterValue);
        Assert.All(changed.Fields, field => Assert.Contains("Value", field.UnchangedProperties));
    }

    [Fact]
    public void GridRowsDoNotDrownOutInputFields()
    {
        var rows = Enumerable.Range(0, 600).Select(index => Field("row-" + index, null, false) with
        { ControlType = "ControlType.DataItem", SupportedPatterns = ["SelectionItemPatternIdentifiers.Pattern"] });
        var containers = new[] { "Tab", "Tree", "DataGrid", "Table" }.Select(type => Field(type, null, false) with
        { ControlType = "ControlType." + type, SupportedPatterns = ["SelectionPatternIdentifiers.Pattern"] });
        var name = Field("name", "Alex");
        var choice = Field("choice", null, false) with
        { ControlType = "ControlType.ComboBox", SupportedPatterns = ["Selection"], SelectionAvailable = true, SelectionValue = "One" };
        var list = Field("list", null, false) with
        { ControlType = "ControlType.List", SupportedPatterns = ["Selection"], SelectionAvailable = true, SelectionValue = "Selected" };
        var editableRow = Field("editable-row", "Editable cell") with
        { ControlType = "ControlType.DataItem", SupportedPatterns = ["SelectionItem", "Value"] };
        var toggledRow = Field("toggled-row", null, false) with
        { ControlType = "ControlType.DataItem", SupportedPatterns = ["SelectionItem", "Toggle"], ToggleState = "Off" };
        var snapshot = new FieldObservationTracker().Observe(Frame(1,
            rows.Concat(containers).Concat([name, choice, list, editableRow, toggledRow]).ToArray()));
        Assert.Equal(5, snapshot.Fields.Count);
        Assert.False(snapshot.Truncated);
        Assert.Contains(snapshot.Fields, field => field.ControlRuntimeId == "name");
        Assert.Contains(snapshot.Fields, field => field.ControlRuntimeId == "editable-row" && field.Kind == FieldKind.Text);
        Assert.Contains(snapshot.Fields, field => field.ControlRuntimeId == "toggled-row" && field.Kind == FieldKind.Toggle);
        Assert.Equal(2, snapshot.Fields.Count(field => field.Kind == FieldKind.Selection));
    }

    private static readonly DateTimeOffset Epoch = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static FrameObservation Frame(long sequence, params AutomationObservation[] controls) => new(
        sequence, Epoch.AddSeconds(sequence), "",
        new(100, 100, 42, "TestWindow", "Test", new(0, 0, 800, 600), true, true, false, false, 96),
        controls, false, "ok", "manual-field-observation");

    private static AutomationObservation Field(string id, string? value, bool available = true) => new(
        id, "", id, "Name", "ControlType.Edit", "Edit", new(10, 10, 120, 24), true, false, "Win32",
        WindowHwnd: 100, SupportedPatterns: ["Value"], IsPassword: false, IsReadOnly: false,
        Value: value, ValueAvailable: available, ProviderRuntimeId: "runtime:" + id, ProviderId: "uia-cached");

    private static AutomationObservation Parent(string reference, string id) => new(
        reference, "", id, "Section", "ControlType.Pane", "Panel", new(0, 0, 800, 600), true, false, "Win32",
        WindowHwnd: 100, IsPassword: false, ProviderId: "uia-cached");
}
