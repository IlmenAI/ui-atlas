namespace UiAtlas.Catalog;

// This versioned JSON boundary intentionally has no MapLock/Protobuf dependencies.
public sealed record CatalogRequest(string Operation, string Root, string? RecordingId = null,
    string? MapId = null, string? DraftRef = null, string? Entry = null, string? SourcePath = null,
    string? Generation = null, int Version = 1, string? KnownCatalogVersion = null);
public sealed record CatalogRecording(string Id, DateTimeOffset StartedUtc, string Outcome,
    string ProcessName, int FrameCount, string? MapId);
public sealed record CatalogMap(string Id, string Path, string ApplicationGroup, string Generation,
    string Entry, string? Session, string? Draft, string? Revision, bool SummaryOnly = false);
public sealed record CatalogImportCandidate(string Path, string Kind, long Bytes);
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
