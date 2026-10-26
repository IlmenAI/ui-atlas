using UiAtlas.Catalog;

namespace UiAtlas.Shell.Catalog;

public sealed record RecordingLibraryItem(CatalogRecording Recording)
{
    public string Id => Recording.Id;
    public string Title => string.IsNullOrWhiteSpace(Recording.ProcessName) ? "Unknown application" : Recording.ProcessName;
    public string Initials => Title[..1].ToUpperInvariant();
    public string Metadata => $"{Recording.FrameCount:N0} frames · {Recording.Outcome}";
    public bool IsInvalid => string.Equals(Recording.Outcome, "invalid", StringComparison.OrdinalIgnoreCase);
    public bool HasMap => !string.IsNullOrWhiteSpace(Recording.MapId);
    public string Status => IsInvalid ? "Invalid" : HasMap ? "Map built" : "Not built";
    public string StatusTone => IsInvalid || !HasMap ? "Draft" : "Ready";
    public string Description => IsInvalid ? "Recording could not be read" : HasMap ? "UI Map available" : "Ready to build";
    public string RecordedAt => Recording.StartedUtc.ToLocalTime().ToString("MMM d, h:mm tt");
    public string Details => $"Recording ID: {Id}\nRecorded: {Recording.StartedUtc.ToLocalTime():f}\nOutcome: {Recording.Outcome}";

    public static RecordingLibraryItem[] Filter(IEnumerable<CatalogRecording> recordings, bool archived,
        string search, string? application, int status, int sort)
    {
        var query = recordings.Where(item => item.Archived == archived).Select(item => new RecordingLibraryItem(item));
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(item => item.Title.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                || item.Id.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                || item.Recording.Outcome.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
        if (application is not null) query = query.Where(item => item.Recording.ProcessName == application);
        query = status switch { 1 => query.Where(item => item.HasMap && !item.IsInvalid),
            2 => query.Where(item => !item.HasMap && !item.IsInvalid), 3 => query.Where(item => item.IsInvalid), _ => query };
        return (sort switch
        {
            1 => query.OrderBy(item => item.Recording.StartedUtc).ThenBy(item => item.Id, StringComparer.Ordinal),
            2 => query.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ThenByDescending(item => item.Recording.StartedUtc),
            _ => query.OrderByDescending(item => item.Recording.StartedUtc).ThenBy(item => item.Id, StringComparer.Ordinal),
        }).ToArray();
    }
}
