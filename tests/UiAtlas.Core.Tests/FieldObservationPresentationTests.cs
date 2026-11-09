using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Tests;

public sealed class FieldObservationPresentationTests
{
    [Fact]
    public void PassiveFieldHighlightsUseTheirOwnSnapshotWhenProviderIdsDifferFromTheFullScan()
    {
        var bounds = new RectI(0, 0, 800, 600);
        var window = new WindowObservation(10, 10, 1, "fixture", "Fixture", bounds, true, true, false, false, 96);
        var fullControl = Control("full-observation-42", 10, new(20, 20, 150, 30));
        var sampledControl = fullControl with { RuntimeId = "passive-observation-7", Bounds = new(25, 20, 150, 30) };
        var fullFrame = new FrameObservation(1, DateTimeOffset.UtcNow, "", window, [fullControl], false, "ok", "full");
        var passiveFrame = fullFrame with { Sequence = 0, Automation = [sampledControl], Trigger = "field-observation" };
        var field = Field(sampledControl);

        Assert.Empty(MapperHighlightModel.BuildFieldsFromFrame(fullFrame, 10, [field]));
        var result = Assert.Single(MapperHighlightModel.BuildFieldsFromFrame(passiveFrame, 10, [field]));
        Assert.Equal(sampledControl.Bounds, result.Bounds);
        Assert.Equal(sampledControl.RuntimeId, result.Control.RuntimeId);
        Assert.Empty(MapperHighlightModel.BuildFieldsFromFrame(passiveFrame, 20, [field]));
    }

    [Fact]
    public void FieldHighlightsRequireCurrentUnambiguousEvidenceFromTheSameSurface()
    {
        var surface = new RectI(0, 0, 800, 600);
        var first = Control("one", 10, new(20, 20, 150, 30));
        var other = first with { RuntimeId = "other", WindowHwnd = 20 };
        var stale = Field(first) with { Id = "stale", IsCurrent = false };
        var foreign = Field(first) with { Id = "foreign", WindowHwnd = 30 };
        var moved = first with { Bounds = first.Bounds with { X = 200 } };

        var highlight = Assert.Single(MapperHighlightModel.BuildFields(surface, 10, [moved, other],
            [Field(first), stale, foreign]));
        Assert.Equal(moved.Bounds, highlight.Bounds); // Current provider bounds, not the historical field rectangle.
        Assert.Equal(MapperHighlightKind.Fields, highlight.Kind);
        Assert.False(highlight.IsGrid); // Fields never create input-intercepting grid windows.
        Assert.Empty(MapperHighlightModel.BuildFields(surface, 10, [first, first], [Field(first)]));
        Assert.Empty(MapperHighlightModel.BuildFields(surface, 10, [first with { RuntimeId = "replacement" }], [Field(first)]));
    }

    [Fact]
    public void FieldsRemainDistinctInsideGridsAndRespectProviderVisibility()
    {
        var surface = new RectI(0, 0, 800, 600);
        var grid = Control("grid", 10, new(10, 10, 700, 500)) with { ControlType = "ControlType.DataGrid", SupportedPatterns = ["Grid"] };
        var editableCell = Control("edit", 10, new(25, 40, 100, 25));
        Assert.Single(MapperHighlightModel.Build(surface, [grid, editableCell]));
        Assert.Single(MapperHighlightModel.BuildFields(surface, 10, [grid, editableCell], [Field(editableCell)]));
        Assert.Empty(MapperHighlightModel.BuildFields(surface, 10, [editableCell with { IsOffscreen = true }], [Field(editableCell)]));
    }

    [Fact]
    public void FieldHighlightsUseQueryScopeAndPermitZeroHwndProviderObservations()
    {
        var surface = new RectI(0, 0, 800, 600);
        var legacy = Control("legacy", 99, new(20, 20, 100, 25)) with { QueryWindowHwnd = 10 };
        var noHandle = legacy with { RuntimeId = "no-handle", QueryWindowHwnd = null, WindowHwnd = 0 };
        var fields = new[] { Field(legacy) with { WindowHwnd = 10 }, Field(noHandle) with { WindowHwnd = 10 } };
        Assert.Equal(2, MapperHighlightModel.BuildFields(surface, 10, [legacy, noHandle], fields).Count);
    }

    [Fact]
    public void ReviewDistinguishesMissingValuesFromObservedEmptyValues()
    {
        var field = Field(Control("name", 10, new(20, 20, 100, 25))) with
        {
            State = FieldObservationState.ChangeObserved,
            BeforeValue = "",
            AfterValue = "Alex",
            ValueAvailable = true
        };
        Assert.Equal("Empty → Alex", FieldObservationPresentation.ChangeText(field));
        Assert.Equal("Change observed", FieldObservationPresentation.ChangeText(field with { BeforeValue = null }));
        Assert.Equal("Fields · 1 change", FieldObservationPresentation.Summary([field]));
        Assert.Equal("Change observed", FieldObservationPresentation.Status(field));
        Assert.Equal("Suggested", FieldObservationPresentation.Status(field with { State = FieldObservationState.Suggested }));
    }

    [Fact]
    public void ReviewKeepsExplicitChoicesAndDoesNotSelectEverySuggestion()
    {
        var field = Field(Control("date", 10, new(20, 20, 100, 25)));
        Assert.False(FieldObservationPresentation.InitiallySelected(field));
        Assert.True(FieldObservationPresentation.InitiallySelected(field with { IsManual = true }));
        Assert.True(FieldObservationPresentation.InitiallySelected(field with { State = FieldObservationState.ChangeObserved }));
        Assert.False(FieldObservationPresentation.InitiallySelected(field with
            { State = FieldObservationState.ChangeObserved, Decision = FieldReviewDecision.Later }));
        Assert.True(FieldObservationPresentation.InitiallySelected(field with { Decision = FieldReviewDecision.Keep }));
    }

    [Theory]
    [InlineData(FieldKind.Toggle, "0", "1", "Off → On")]
    [InlineData(FieldKind.Toggle, "1", "2", "On → Mixed")]
    [InlineData(FieldKind.Selection, "[]", "[\"Reservation\"]", "No selection → Reservation")]
    [InlineData(FieldKind.Selection, "[\"Red\"]", "[\"Blue\",\"Green\"]", "Red → Blue, Green")]
    [InlineData(FieldKind.Selection, "Pending", "Approved", "Pending → Approved")]
    public void ReviewFormatsTogglesAndSelectionsWithoutChangingTheEvidence(FieldKind kind, string before, string after, string expected)
    {
        var field = Field(Control("choice", 10, new(20, 20, 100, 25))) with
        {
            Kind = kind, State = FieldObservationState.ChangeObserved,
            ValueAvailable = true, BeforeValue = before, AfterValue = after
        };
        Assert.Equal(expected, FieldObservationPresentation.ChangeText(field));
    }

    internal static FieldObservation Field(AutomationObservation control) => new(
        "field-" + control.RuntimeId, control.Name, FieldKind.Text, control.Bounds,
        FieldObservationState.Suggested, FieldReviewDecision.Undecided, false, false, null, null,
        [], ["AutomationId"], "provider-session", 1, 1, control.WindowHwnd, control.RuntimeId, true);

    private static AutomationObservation Control(string id, long hwnd, RectI bounds) =>
        new(id, "", id, id, "ControlType.Edit", "Edit", bounds, true, false, "WPF", hwnd);
}
