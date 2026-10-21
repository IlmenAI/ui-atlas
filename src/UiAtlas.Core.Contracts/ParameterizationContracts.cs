using System.Text.Json.Serialization;

namespace UiAtlas.Core.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ParameterReviewState>))]
public enum ParameterReviewState { Suggested, Accepted, Denied, NeedsEvidence }

[JsonConverter(typeof(JsonStringEnumConverter<GridVariationMode>))]
public enum GridVariationMode { FullyVariable, AccordingToSchema }

public sealed record ParameterEvidence(string Kind, string Summary, string Source, string? Applicability = null);

public sealed record ParameterDocumentation(string Title, string Url, string Excerpt, string Applicability);

public sealed record GridVariationRule(
    GridVariationMode Mode = GridVariationMode.FullyVariable,
    bool AllowRowCountChanges = true,
    bool AllowRowOrderChanges = true,
    bool AllowColumnChanges = false);

public sealed record ParameterProposal(
    string Id, string TargetId, string SurfaceId, string DisplayName, string ParameterName,
    string ValueType, string Scope, string PropertyName, bool IsGrid, RectI Bounds,
    IReadOnlyList<ParameterEvidence> Evidence, string Fingerprint,
    GridSchema? Schema = null, RectI? HeaderBounds = null, string? ImagePath = null,
    string? ImageSha256 = null, bool HostBound = true, string? ParentProposalId = null,
    bool IsManual = false, string Coverage = "Coverage unknown", string ReuseKey = "");

public sealed record ParameterDecision(
    string ProposalId, string SourceFingerprint, ParameterReviewState State,
    string ParameterName, string ValueType, string Scope, GridVariationRule? GridRule,
    IReadOnlyList<ParameterDocumentation> Documentation, string Reviewer, DateTimeOffset ReviewedUtc,
    string Note = "");

public sealed record MapParameterizationDocument(
    string Format, string LogicalMapId, long Revision,
    IReadOnlyList<ParameterProposal> Proposals, IReadOnlyList<ParameterDecision> Decisions)
{
    public const string CurrentFormat = "ui-atlas.parameter-review/1";
    public static MapParameterizationDocument Empty(string logicalMapId) =>
        new(CurrentFormat, logicalMapId, 0, [], []);
}

/// <summary>A retained mapping schema; a missing host is explicitly inspectable, not guessed.</summary>
public sealed record ParameterGridSource(
    string Id, string TargetId, string SurfaceId, string DisplayName,
    SavedDataGridReview Review, string ImagePath, bool HostBound);
