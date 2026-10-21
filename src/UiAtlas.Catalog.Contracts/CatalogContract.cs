namespace UiAtlas.Catalog;

// This versioned JSON boundary intentionally has no MapLock/Protobuf dependencies.
public sealed record CatalogRequest(string Operation, string Root, string? RecordingId = null,
    string? MapId = null, string? DraftRef = null, string? Entry = null, string? SourcePath = null,
    string? Generation = null, int Version = 1, string? KnownCatalogVersion = null,
    string? RevisionRef = null, bool RequireReady = false, string? ProfileId = null, bool IncludeRawWorld = false,
    long? ExpectedReviewRevision = null, string? ExpectedReviewSha256 = null,
    CatalogParameterConfirmation[]? ParameterConfirmations = null, string? ExpectedDraftEtag = null, string[]? IdentityConfirmations = null);
public sealed record CatalogRecording(string Id, DateTimeOffset StartedUtc, string Outcome,
    string ProcessName, int FrameCount, string? MapId);
public sealed record CatalogMap(string Id, string Path, string ApplicationGroup, string Generation,
    string Entry, string? Session, string? Draft, string? Revision, bool SummaryOnly = false);
public sealed record CatalogImportCandidate(string Path, string Kind, long Bytes);
// Full immutable bytes with eligibility observed at this read, never a latest alias.
public sealed record CatalogRevision(string RevisionRef, string CanonicalPayloadHash, string Payload,
    string MapId, string Generation, string Entry, bool Ready, bool Archived, string ReadAtUtc, int Version = 1);
// Original canonical source payloads whose hashes are embedded in the revision.
// PayloadKind is "snapshot" (WorldStateSnapshot) or "survey" (SurveyScenePublication).
public sealed record CatalogRawWorldSource(string SourceObservationSetRef, string PayloadKind, string PayloadSha256, string Payload);
// Accepted scalar decisions, compiled against this exact revision and its original
// source objects. A rule varies one property (or one bounded part of a name).
public sealed record CatalogParameterRule(string ProposalId, string SourceFingerprint, string ParameterName,
    string TargetEntityRef, string SourceObservationSetRef, string SourcePayloadSha256,
    string OriginWorldObjectRef, string WorldObjectRef, string PropertyName, string ExpectedValue,
    string Prefix = "", string Suffix = "");
public sealed record CatalogParameterPolicy(int Version, string MapRevisionRef, string MapPayloadSha256,
    long ReviewRevision, string ReviewSha256, CatalogParameterRule[] Rules);
public sealed record CatalogParameterCandidate(string ProposalId, string SourceFingerprint, string ParameterName,
    string DisplayName, string PropertyName, string Scope, string Evidence, int EquivalentControlCount = 1);
public sealed record CatalogParameterConfirmation(string ProposalId, string SourceFingerprint, string Note);
// Current authoring suggestions for comparison only. Preview must never be admitted
// as an accepted matching policy or substituted into a saved analysis.
public sealed record CatalogParameterReview(string MapId, string Generation, CatalogParameterPolicy Accepted,
    CatalogParameterPolicy Preview, CatalogParameterCandidate[] Candidates, int ReusedParameterCount = 0);
public sealed record CatalogIdentityCandidate(string Fingerprint, string CanonicalRef, string[] Members, string Label, string Evidence);
public sealed record CatalogIdentityReview(string MapId, string Generation, string RevisionRef, string DraftRef, string DraftEtag, CatalogIdentityCandidate[] Candidates);
public sealed record CatalogReply
{
    public int Version { get; init; } = 1;
    public bool Success { get; init; } = true;
    public string Message { get; init; } = "";
    public string Root { get; init; } = "";
    public CatalogRecording[] Recordings { get; init; } = [];
    public CatalogMap[] Maps { get; init; } = [];
    public string? CatalogVersion { get; init; }
    public bool NotModified { get; init; }
    public CatalogImportCandidate[] Imports { get; init; } = [];
    public string? Revision { get; init; }
    public CatalogRevision? ExactRevision { get; init; }
    public CatalogRawWorldSource[] RawWorldSources { get; init; } = [];
    public int RawWorldContractVersion { get; init; }
    public CatalogParameterPolicy? Parameterization { get; init; }
    public CatalogParameterReview? ParameterReview { get; init; }
    public CatalogIdentityReview? IdentityReview { get; init; }
    public string? IdentityRevisionRef { get; init; }
    public string? CaptureRequest { get; init; }
    public string? MapId { get; init; }
    public int Nodes { get; init; }
    public int Edges { get; init; }
}
public static class AtlasCatalogLocation
{
    public static string Resolve(string? root = null) => Path.GetFullPath(root
        ?? Environment.GetEnvironmentVariable("UI-ATLAS_DATA_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UiAtlas", "Core"));
}
