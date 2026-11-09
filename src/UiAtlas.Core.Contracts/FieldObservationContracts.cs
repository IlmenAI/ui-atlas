using System.Text.Json.Serialization;

namespace UiAtlas.Core.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<FieldKind>))]
public enum FieldKind { Text, Number, Date, Selection, Toggle, Other }

[JsonConverter(typeof(JsonStringEnumConverter<FieldObservationState>))]
public enum FieldObservationState { Suggested, ChangeObserved }

[JsonConverter(typeof(JsonStringEnumConverter<FieldReviewDecision>))]
public enum FieldReviewDecision { Undecided, Keep, Later }

/// <summary>A difference between two actual observations, not a rule to ignore this property.</summary>
public sealed record FieldPropertyChange(
    string PropertyName,
    string Before,
    string After,
    long BeforeFrameSequence,
    long AfterFrameSequence,
    DateTimeOffset BeforeObservedUtc,
    DateTimeOffset AfterObservedUtc);

/// <summary>
/// Field evidence collected within one recording. Null values mean unavailable; an empty string
/// is an observed empty value. Keep/Later is a review choice, never a matching or execution rule.
/// </summary>
public sealed record FieldObservation(
    string Id,
    string Label,
    FieldKind Kind,
    RectI Bounds,
    FieldObservationState State,
    FieldReviewDecision Decision,
    bool IsManual,
    bool ValueAvailable,
    string? BeforeValue,
    string? AfterValue,
    IReadOnlyList<FieldPropertyChange> PropertyChanges,
    IReadOnlyList<string> UnchangedProperties,
    string IdentityBasis,
    long FirstFrameSequence,
    long LastFrameSequence,
    long WindowHwnd,
    string ControlRuntimeId,
    bool IsCurrent,
    DateTimeOffset FirstObservedUtc = default,
    DateTimeOffset LastObservedUtc = default,
    string ProviderId = "");

public sealed record FieldLearningSnapshot(
    string Format,
    IReadOnlyList<FieldObservation> Fields,
    bool Truncated = false)
{
    public const string CurrentFormat = "ui-atlas.field-learning/1";
    public static FieldLearningSnapshot Empty { get; } = new(CurrentFormat, []);
}
