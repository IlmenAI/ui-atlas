using System.Text.Json;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Cli;

/// <summary>Presentation of observations; keeping a field never certifies its locator.</summary>
internal static class FieldObservationPresentation
{
    internal static string Summary(IReadOnlyList<FieldObservation> fields)
    {
        var changed = fields.Count(field => field.State == FieldObservationState.ChangeObserved);
        if (changed != 0) return $"Fields · {changed} {(changed == 1 ? "change" : "changes")}";
        var current = fields.Count(field => field.IsCurrent);
        return current == 0 ? "Fields" : $"Fields · {current} suggested";
    }

    internal static string Kind(FieldKind kind) => kind switch
    {
        FieldKind.Text => "Text",
        FieldKind.Number => "Number",
        FieldKind.Date => "Date",
        FieldKind.Selection => "Selection",
        FieldKind.Toggle => "Checkbox / toggle",
        _ => "Field"
    };

    internal static string Symbol(FieldKind kind) => kind switch
    {
        FieldKind.Text => "T",
        FieldKind.Number => "#",
        FieldKind.Date => "▦",
        FieldKind.Selection => "⌄",
        FieldKind.Toggle => "☑",
        _ => "▤"
    };

    internal static string Status(FieldObservation field) =>
        field.State == FieldObservationState.ChangeObserved ? "Change observed" : "Suggested";

    internal static string ChangeText(FieldObservation field)
    {
        if (field.BeforeValue is not null && field.AfterValue is not null && field.BeforeValue != field.AfterValue)
            return $"{DisplayValue(field.BeforeValue, field.Kind)} → {DisplayValue(field.AfterValue, field.Kind)}";
        if (field.State == FieldObservationState.ChangeObserved)
        {
            var change = field.PropertyChanges.LastOrDefault();
            return change is null ? "Change observed" : ChangeDetail(field, change);
        }
        return field.IsManual ? "Marked by you · no change observed" :
            field.ValueAvailable ? "No change observed" : "Value is not exposed by the app";
    }

    internal static bool InitiallySelected(FieldObservation field) => field.Decision == FieldReviewDecision.Keep ||
        field.Decision != FieldReviewDecision.Later &&
        (field.State == FieldObservationState.ChangeObserved || field.IsManual);

    internal static string PropertyName(string value) => value switch
    {
        "AutomationId" => "Automation ID",
        "ControlType" => "Control type",
        "ClassName" => "Class",
        "ToggleState" => "Checked state",
        "IsEnabled" => "Enabled state",
        _ => value
    };

    internal static string ChangeDetail(FieldObservation field, FieldPropertyChange change)
    {
        var kind = change.PropertyName switch
        {
            "ToggleState" => FieldKind.Toggle,
            "Selection" => FieldKind.Selection,
            "Value" => field.Kind,
            _ => FieldKind.Other
        };
        return $"{PropertyName(change.PropertyName)}: {DisplayValue(change.Before, kind)} → {DisplayValue(change.After, kind)}";
    }

    private static string DisplayValue(string value, FieldKind kind)
    {
        if (kind == FieldKind.Toggle)
            value = value.Trim() switch
            {
                "0" or "Off" or "False" => "Off",
                "1" or "On" or "True" => "On",
                "2" or "Indeterminate" or "Mixed" => "Mixed",
                _ => value
            };
        if (kind == FieldKind.Selection)
        {
            if (value.Length == 0) return "No selection";
            try
            {
                using var document = JsonDocument.Parse(value);
                if (document.RootElement.ValueKind == JsonValueKind.Array &&
                    document.RootElement.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
                {
                    var names = document.RootElement.EnumerateArray().Select(item => item.GetString()!).ToArray();
                    value = names.Length == 0 ? "No selection" : string.Join(", ", names);
                }
            }
            catch (JsonException) { } // A provider may supply a plain selected name.
        }
        if (value.Length == 0) return "Empty";
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 72 ? oneLine : oneLine[..69] + "…";
    }
}

