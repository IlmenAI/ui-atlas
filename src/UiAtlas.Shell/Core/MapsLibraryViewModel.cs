using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using UIMaps.Protocol.V3;

namespace UIMaps.Application.Core;

public enum MapLibraryFilter
{
    All,
    NeedsReview,
    Drafts,
    Ready,
    Warnings,
}

public enum MapLibrarySort
{
    RecentlyUpdated,
    ApplicationName,
}

public sealed record MapLibraryFilterOption(MapLibraryFilter Value, string Label);
public sealed record MapLibrarySortOption(MapLibrarySort Value, string Label);
public sealed record MappedSurfacePresentation(string Name, string Detail, string Status);

public sealed class MapRowPresentation
{
    public required MapsLibraryRecord Record { get; init; }
    public required string ApplicationVersion { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required string Reference { get; init; }
    public required string Status { get; init; }
    public required string StatusTone { get; init; }
    public required string Updated { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string Counts { get; init; }
    public required string Detail { get; init; }
    public required string Glyph { get; init; }
    public int BlockingReviewCount { get; init; }
    public int WarningCount { get; init; }
    public bool IsDraft => Kind == "Draft";
    public bool IsReady => Kind == "Ready";
    public bool IsArchived => Record.Entry.Archived;
    public bool CanMarkReady => !IsReady;
    public bool CanMarkDraft => IsReady;
    public bool NeedsReview => BlockingReviewCount > 0;
}

public sealed class MapVersionPresentation
{
    public required string ApplicationVersion { get; init; }
    public required string Heading { get; init; }
    public required ObservableCollection<MapRowPresentation> Maps { get; init; }
    public bool HasReadyMap => Maps.Any(value => value.IsReady);
    public bool IsArchived => Maps.Count > 0 && Maps.All(value => value.IsArchived);
    public string Status => IsArchived ? "Archived" : HasReadyMap ? "Ready" : "No Ready map";
    public string StatusTone => IsArchived ? "Archived" : HasReadyMap ? "Ready" : "Draft";
    public string RecordingSummary => $"{Maps.Count} {(Maps.Count == 1 ? "recording" : "recordings")}";
    public bool IsExpanded { get; set; } = true;
}

public sealed class MapApplicationPresentation
{
    public required string ApplicationRef { get; init; }
    public required string DisplayName { get; init; }
    public required string ExecutableName { get; init; }
    public required string Initials { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required ObservableCollection<MapRowPresentation> Maps { get; init; }
    public required ObservableCollection<MapVersionPresentation> Versions { get; init; }
    public required string Counts { get; init; }
    public required string MapSummary { get; init; }
    public required string AttentionStatus { get; init; }
    public required string Status { get; init; }
    public required string StatusTone { get; init; }
    public required string VersionSummary { get; init; }
    public required string RecordingSummary { get; init; }
    public required string LatestActivity { get; init; }
    public bool HasReadyMap { get; init; }
    public bool NeedsReview { get; init; }
    public bool HasWarnings { get; init; }
    public int SurfaceCount { get; init; }
    public int ControlCount { get; init; }
    public int DataGridCount { get; init; }
    public int ObservationCount { get; init; }
    public int BlockingReviewCount { get; init; }
    public int DraftCount => Maps.Count(value => value.IsDraft);
    public bool HasDrafts => DraftCount > 0;
    public bool IsArchived { get; init; }
    public required IReadOnlyList<MappedSurfacePresentation> Surfaces { get; init; }
    public bool IsExpanded { get; set; } = true;
}

public sealed class MapsLibraryViewModel : INotifyPropertyChanged
{
    private readonly IMapsLibrarySource _source;
    private IReadOnlyList<MapsLibraryRecord> _allRecords = [];
    private IReadOnlyList<MapApplicationPresentation> _allGroups = [];
    private string _searchText = "";
    private MapLibraryFilterOption _selectedFilter;
    private MapLibrarySortOption _selectedSort;
    private bool _isBusy;
    private bool _showArchived;
    private bool _lastRefreshSucceeded;
    private string _status = "Loading recorded maps…";

    public MapsLibraryViewModel(IMapsLibrarySource source)
    {
        _source = source;
        Filters =
        [
            new(MapLibraryFilter.All, "All statuses"),
            new(MapLibraryFilter.Ready, "Ready"),
            new(MapLibraryFilter.Drafts, "Draft only"),
        ];
        SortOptions =
        [
            new(MapLibrarySort.RecentlyUpdated, "Recently updated"),
            new(MapLibrarySort.ApplicationName, "Application name"),
        ];
        _selectedFilter = Filters[0];
        _selectedSort = SortOptions[0];
    }

    public ObservableCollection<MapApplicationPresentation> Groups { get; } = [];
    public IReadOnlyList<MapLibraryFilterOption> Filters { get; }
    public IReadOnlyList<MapLibrarySortOption> SortOptions { get; }
    public int ApplicationCount => Groups.Count;
    public int MapCount => Groups.Sum(value => value.Maps.Count);
    public int AttentionCount => _allGroups.Count(value => value.NeedsReview || value.HasWarnings);
    public bool HasAttention => AttentionCount > 0;
    public bool HasGroups => Groups.Count > 0;
    public bool ShowArchived { get => _showArchived; set { if (Set(ref _showArchived, value)) ApplyView(); } }
    public bool LastRefreshSucceeded { get => _lastRefreshSucceeded; private set => Set(ref _lastRefreshSucceeded, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value ?? "")) ApplyView(); }
    }

    public MapLibraryFilterOption SelectedFilter
    {
        get => _selectedFilter;
        set { if (value is not null && Set(ref _selectedFilter, value)) ApplyView(); }
    }

    public MapLibrarySortOption SelectedSort
    {
        get => _selectedSort;
        set { if (value is not null && Set(ref _selectedSort, value)) ApplyView(); }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        IsBusy = true;
        LastRefreshSucceeded = false;
        Status = "Loading recorded maps…";
        try
        {
            var records = await _source.LoadAsync(cancellationToken);
            if (!ReferenceEquals(_allRecords, records))
            {
                var groups = await Task.Run(() => BuildGroups(records), cancellationToken);
                _allRecords = records;
                _allGroups = groups;
                ApplyView();
            }
            LastRefreshSucceeded = true;
            Status = _allGroups.Count == 0
                ? "No recorded UI Maps yet. Create one to begin building application knowledge."
                : $"Loaded {_allGroups.Count} applications and {_allGroups.Sum(value => value.Maps.Count)} maps.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Status = $"Recorded maps could not be loaded. {ex.GetBaseException().Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ShowNeedsReview()
    {
        SelectedFilter = Filters[0];
    }

    public MapApplicationPresentation? FindLatestApplication(string executableName)
    {
        var executableIdentity = NormalizeExecutableIdentity(executableName);
        return _allRecords
            .Where(record => !record.Entry.Archived)
            .Where(record => string.Equals(
                NormalizeExecutableIdentity(
                    record.Session?.Target.ExecutableName
                    ?? record.Revision?.Target.ExecutableName
                    ?? string.Empty),
                executableIdentity,
                StringComparison.OrdinalIgnoreCase))
            .Select(record => BuildGroup(record.ApplicationGroupRef, [record]))
            .OrderByDescending(value => value.UpdatedAt)
            .FirstOrDefault();
    }

    public MapApplicationPresentation? FindTransferredApplication(IReadOnlyList<string> draftRefs)
    {
        var records = _allRecords.Where(record => draftRefs.Contains(record.Entry.MapDraftRef, StringComparer.Ordinal)).ToArray();
        return records.Length == 0 ? null : BuildGroup(records[0].ApplicationGroupRef, records);
    }

    public MapApplicationPresentation? FindApplication(string applicationRef, bool archived) =>
        _allGroups.FirstOrDefault(value => value.IsArchived == archived
            && string.Equals(value.ApplicationRef, applicationRef, StringComparison.Ordinal));

    public async Task PublishCompletedRecordingAsync(MapApplicationPresentation application, CancellationToken cancellationToken)
    {
        var record = ResolveLatestRecord(application, requireDraft: true);
        await ExecuteLibraryActionAsync(
            "Publishing and marking the completed UI Map Ready…",
            token => _source.MarkReadyAsync(record, token),
            cancellationToken);
    }

    public async Task ArchiveCompletedRecordingAsync(MapApplicationPresentation application, CancellationToken cancellationToken)
    {
        var record = ResolveLatestRecord(application, requireDraft: false);
        await ExecuteLibraryActionAsync(
            "Archiving the completed UI Map…",
            token => _source.ArchiveAsync(record, token),
            cancellationToken);
    }

    public Task MarkReadyAsync(MapRowPresentation map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        return ExecuteLibraryActionAsync("Marking the UI Map Ready…", token => _source.MarkReadyAsync(map.Record, token), cancellationToken);
    }

    public Task MarkDraftAsync(MapRowPresentation map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        return ExecuteLibraryActionAsync("Moving the UI Map back to Draft…", token => _source.MarkDraftAsync(map.Record, token), cancellationToken);
    }

    public Task ArchiveAsync(MapRowPresentation map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        return ExecuteLibraryActionAsync("Archiving the UI Map…", token => _source.ArchiveAsync(map.Record, token), cancellationToken);
    }

    public Task RestoreAsync(MapRowPresentation map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        return ExecuteLibraryActionAsync("Restoring the UI Map…", token => _source.RestoreAsync(map.Record, token), cancellationToken);
    }

    public Task ArchiveAllDraftsAsync(MapApplicationPresentation application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        var drafts = RecordsFor(application).Where(value => !value.Entry.IsReady).ToArray();
        if (drafts.Length == 0) return Task.CompletedTask;
        return ExecuteLibraryActionAsync(
            $"Archiving {drafts.Length} draft UI {(drafts.Length == 1 ? "Map" : "Maps")}…",
            token => _source.ArchiveManyAsync(drafts, token),
            cancellationToken);
    }

    public Task ArchiveApplicationAsync(MapApplicationPresentation application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        var records = RecordsFor(application).ToArray();
        if (records.Length == 0) return Task.CompletedTask;
        return ExecuteLibraryActionAsync(
            $"Archiving {application.DisplayName}…",
            token => _source.ArchiveManyAsync(records, token),
            cancellationToken);
    }

    public Task RestoreApplicationAsync(MapApplicationPresentation application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        var records = RecordsFor(application).ToArray();
        if (records.Length == 0) return Task.CompletedTask;
        return ExecuteLibraryActionAsync(
            $"Restoring {application.DisplayName}…",
            token => _source.RestoreManyAsync(records, token),
            cancellationToken);
    }

    private IEnumerable<MapsLibraryRecord> RecordsFor(MapApplicationPresentation application) =>
        _allRecords.Where(value => value.Entry.Archived == application.IsArchived
            && string.Equals(value.ApplicationGroupRef, application.ApplicationRef, StringComparison.Ordinal));

    private MapsLibraryRecord ResolveLatestRecord(MapApplicationPresentation application, bool requireDraft)
    {
        ArgumentNullException.ThrowIfNull(application);
        return _allRecords
            .Where(value => string.Equals(value.ApplicationGroupRef, application.ApplicationRef, StringComparison.Ordinal))
            .Where(value => !requireDraft || value.Draft is not null)
            .OrderByDescending(value => ParseDate(value.Draft?.UpdatedAt ?? value.Session?.UpdatedAt ?? value.Entry.UpdatedAt))
            .FirstOrDefault()
            ?? throw new InvalidOperationException(requireDraft
                ? "The completed UI Map draft is no longer available. Refresh Maps and try again."
                : "The completed UI Map is no longer available. Refresh Maps and try again.");
    }

    private async Task ExecuteLibraryActionAsync(
        string progress,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        if (IsBusy) throw new InvalidOperationException("The Map Library is already busy.");
        IsBusy = true;
        Status = progress;
        try
        {
            await action(cancellationToken);
            var records = await _source.LoadAsync(cancellationToken);
            var groups = await Task.Run(() => BuildGroups(records), cancellationToken);
            _allRecords = records;
            _allGroups = groups;
            ApplyView();
            Status = $"Loaded {_allGroups.Count} applications and {_allGroups.Sum(value => value.Maps.Count)} maps.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string NormalizeExecutableIdentity(string value)
    {
        var fileName = Path.GetFileName((value ?? "").Trim());
        return string.Equals(Path.GetExtension(fileName), ".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;
    }

    internal static IReadOnlyList<MapApplicationPresentation> BuildGroups(IReadOnlyList<MapsLibraryRecord> records)
    {
        return records
            .GroupBy(value => (value.ApplicationGroupRef, value.Entry.Archived))
            .Select(group => BuildGroup(group.Key.ApplicationGroupRef, group.ToArray()))
            .OrderByDescending(value => value.UpdatedAt)
            .ToArray();
    }

    private static MapApplicationPresentation BuildGroup(string applicationRef, IReadOnlyList<MapsLibraryRecord> records)
    {
        var maps = new List<MapRowPresentation>();
        var entityRefs = new Dictionary<MapEntityKind, HashSet<string>>();
        var sourceRefs = new HashSet<string>(StringComparer.Ordinal);
        var displayName = records.Select(value => value.Session?.Target.DisplayName ?? value.Revision?.Target.DisplayName ?? value.Entry.ApplicationDisplayName)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? applicationRef;
        var executable = records.Select(value => value.Session?.Target.ExecutableName ?? value.Revision?.Target.ExecutableName)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? applicationRef;

        foreach (var record in records)
        {
            var warnings = record.Entry.Warnings.Concat(record.Session?.Warnings ?? [])
                .Where(value => !value.StartsWith("Imported immutable revision;", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (record.Revision is not null)
            {
                AddKnowledge(record.Revision.Entities, record.Revision.Sources, entityRefs, sourceRefs);
                var revisionWarnings = warnings.Length + record.Revision.Limitations.Count;
                maps.Add(new MapRowPresentation
                {
                    Record = record,
                    ApplicationVersion = ApplicationVersion(record),
                    Kind = record.Entry.Archived ? "Archived" : record.Entry.IsReady ? "Ready" : "Draft",
                    Title = "Map recording",
                    Reference = record.Revision.MapRevisionRef,
                    Status = record.Entry.Archived ? "Archived" : record.Entry.IsReady ? "Ready" : "Draft",
                    StatusTone = record.Entry.Archived ? "Archived" : record.Entry.IsReady ? "Ready" : "Draft",
                    UpdatedAt = ParseDate(record.Revision.PublishedAt),
                    Updated = FormatDate(record.Revision.PublishedAt),
                    Counts = Counts(record.Revision.Entities, record.Revision.Sources),
                    Detail = revisionWarnings > 0
                        ? $"Immutable  ·  {revisionWarnings} warnings  ·  {SourceCount(record.Revision.Sources.Count)}"
                        : $"Immutable  ·  {SourceCount(record.Revision.Sources.Count)}",
                    Glyph = "\uE73E",
                    WarningCount = revisionWarnings,
                });
            }
            else
            {
                if (record.Draft is not null)
                    AddKnowledge(record.Draft.Entities, record.Draft.Sources, entityRefs, sourceRefs);
                var draftBlockers = record.Draft?.ReviewItems.Count(value => value.Blocking && value.ReviewState == ReviewState.ReviewUnreviewed)
                    ?? record.Entry.BlockingReviewCount;
                var draftWarnings = warnings.Length + (record.Draft?.Limitations.Count ?? 0);
                var draftSources = record.Draft?.Sources.Count ?? record.Session?.AttachedSourceCount ?? 0;
                maps.Add(new MapRowPresentation
                {
                    Record = record,
                    ApplicationVersion = ApplicationVersion(record),
                    Kind = record.Entry.Archived ? "Archived" : "Draft",
                    Title = "Map recording",
                    Reference = record.Draft?.MapDraftRef ?? record.Session?.MappingSessionRef ?? record.Entry.MapDraftRef,
                    Status = record.Entry.Archived ? "Archived" : "Draft",
                    StatusTone = record.Entry.Archived ? "Archived" : "Draft",
                    UpdatedAt = ParseDate(record.Draft?.UpdatedAt ?? record.Session?.UpdatedAt ?? record.Entry.UpdatedAt),
                    Updated = FormatDate(record.Draft?.UpdatedAt ?? record.Session?.UpdatedAt ?? record.Entry.UpdatedAt),
                    Counts = Counts(record.Draft?.Entities, record.Draft?.Sources),
                    Detail = Detail(draftBlockers, draftWarnings, draftSources),
                    Glyph = "\uE8A5",
                    BlockingReviewCount = draftBlockers,
                    WarningCount = draftWarnings,
                });
            }
        }

        var orderedMaps = maps.OrderByDescending(value => value.UpdatedAt).ToArray();
        var versions = orderedMaps
            .GroupBy(value => value.ApplicationVersion, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MapVersionPresentation
            {
                ApplicationVersion = group.Key,
                Heading = string.IsNullOrWhiteSpace(group.Key) ? "Unknown version" : $"Version {group.Key}",
                Maps = new ObservableCollection<MapRowPresentation>(group.OrderByDescending(value => value.UpdatedAt)),
            })
            .OrderBy(value => string.IsNullOrWhiteSpace(value.ApplicationVersion) ? 1 : 0)
            .ThenByDescending(value => value.Maps.Max(map => map.UpdatedAt))
            .ToArray();
        var hasReady = orderedMaps.Any(value => value.IsReady);
        var blockers = records.Where(record => record.Revision is null).Sum(record => record.Draft is not null
            ? record.Draft.ReviewItems.Count(value => value.Blocking && value.ReviewState == ReviewState.ReviewUnreviewed)
            : record.Entry.BlockingReviewCount);
        var warningCount = orderedMaps.Sum(value => value.WarningCount);
        var currentEntities = records.SelectMany(record => record.Revision?.Entities ?? record.Draft?.Entities ?? [])
            .Where(value => value.ReviewState != ReviewState.ReviewRejected)
            .GroupBy(value => value.MapEntityRef, StringComparer.Ordinal)
            .Select(value => value.First().Clone())
            .ToArray();
        var surfaces = BuildSurfacePresentations(currentEntities);
        var surfaceCount = Count(entityRefs, MapEntityKind.MapSurface);
        var controlCount = Count(entityRefs, MapEntityKind.MapControl);
        var dataGridCount = Count(entityRefs, MapEntityKind.MapDataGrid);
        var counts = JoinCounts(
            surfaceCount,
            controlCount,
            dataGridCount,
            sourceRefs.Count);
        return new MapApplicationPresentation
        {
            ApplicationRef = applicationRef,
            DisplayName = displayName,
            ExecutableName = executable,
            Initials = Initials(displayName),
            UpdatedAt = orderedMaps.Select(value => value.UpdatedAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max(),
            Maps = new ObservableCollection<MapRowPresentation>(orderedMaps),
            Versions = new ObservableCollection<MapVersionPresentation>(versions),
            Counts = counts,
            MapSummary = $"{orderedMaps.Length} {(orderedMaps.Length == 1 ? "map" : "maps")}",
            AttentionStatus = blockers > 0 ? $"{blockers} blocking" : warningCount > 0 ? $"{warningCount} warnings" : "Up to date",
            Status = records.All(value => value.Entry.Archived) ? "Archived" : hasReady ? "Ready" : "Draft only",
            StatusTone = records.All(value => value.Entry.Archived) ? "Archived" : hasReady ? "Ready" : "Draft",
            VersionSummary = $"{versions.Length} {(versions.Length == 1 ? "version" : "versions")}",
            RecordingSummary = $"{orderedMaps.Length} {(orderedMaps.Length == 1 ? "recording" : "recordings")}",
            LatestActivity = orderedMaps.Length == 0 ? "Update time unavailable" : orderedMaps[0].Updated,
            IsArchived = records.All(value => value.Entry.Archived),
            HasReadyMap = hasReady,
            NeedsReview = blockers > 0,
            HasWarnings = warningCount > 0,
            SurfaceCount = surfaceCount,
            ControlCount = controlCount,
            DataGridCount = dataGridCount,
            ObservationCount = sourceRefs.Count,
            BlockingReviewCount = blockers,
            Surfaces = surfaces,
        };
    }

    private static string ApplicationVersion(MapsLibraryRecord record) =>
        (record.Entry.ApplicationVersion
            ?? record.Session?.Target?.ApplicationVersion
            ?? record.Revision?.Target?.ApplicationVersion
            ?? string.Empty).Trim();

    private static IReadOnlyList<MappedSurfacePresentation> BuildSurfacePresentations(IReadOnlyList<MapEntity> entities)
    {
        var byRef = entities.Where(value => !string.IsNullOrWhiteSpace(value.MapEntityRef))
            .ToDictionary(value => value.MapEntityRef, StringComparer.Ordinal);
        return entities.Where(value => value.EntityKind == MapEntityKind.MapSurface && value.Applicable)
            .OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(surface =>
            {
                var grids = entities.Where(value => value.EntityKind == MapEntityKind.MapDataGrid
                        && value.Applicable && HasAncestor(value, surface.MapEntityRef, byRef))
                    .Select(value => value.DisplayName)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new MappedSurfacePresentation(
                    string.IsNullOrWhiteSpace(surface.DisplayName) ? "Unnamed surface" : surface.DisplayName,
                    grids.Length == 0 ? "No Data Grid" : string.Join(", ", grids),
                    "Mapped");
            })
            .ToArray();
    }

    private static bool HasAncestor(MapEntity entity, string ancestorRef, IReadOnlyDictionary<string, MapEntity> byRef)
    {
        var parent = entity.ParentEntityRef;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(parent) && visited.Add(parent))
        {
            if (string.Equals(parent, ancestorRef, StringComparison.Ordinal)) return true;
            if (!byRef.TryGetValue(parent, out var next)) break;
            parent = next.ParentEntityRef;
        }
        return false;
    }

    private void ApplyView()
    {
        IEnumerable<MapApplicationPresentation> values = _allGroups.Where(value => value.IsArchived == ShowArchived);
        var query = SearchText.Trim();
        if (query.Length > 0)
        {
            values = values.Where(group => group.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || group.ApplicationRef.Contains(query, StringComparison.OrdinalIgnoreCase)
                || group.ExecutableName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || group.Maps.Any(map => map.Reference.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }
        values = SelectedFilter.Value switch
        {
            MapLibraryFilter.NeedsReview => values.Where(value => value.NeedsReview),
            MapLibraryFilter.Drafts => values.Where(value => !value.HasReadyMap),
            MapLibraryFilter.Ready => values.Where(value => value.HasReadyMap),
            MapLibraryFilter.Warnings => values.Where(value => value.HasWarnings),
            _ => values,
        };
        values = SelectedSort.Value == MapLibrarySort.ApplicationName
            ? values.OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            : values.OrderByDescending(value => value.UpdatedAt);
        Groups.Clear();
        foreach (var value in values) Groups.Add(value);
        RaiseCounts();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(ApplicationCount));
        OnPropertyChanged(nameof(MapCount));
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(HasAttention));
        OnPropertyChanged(nameof(HasGroups));
    }

    private static void AddKnowledge(IEnumerable<MapEntity> entities, IEnumerable<SourceObservationSet> sources, Dictionary<MapEntityKind, HashSet<string>> entityRefs, HashSet<string> sourceRefs)
    {
        foreach (var entity in entities)
        {
            if (!entityRefs.TryGetValue(entity.EntityKind, out var values)) entityRefs[entity.EntityKind] = values = new HashSet<string>(StringComparer.Ordinal);
            values.Add(entity.MapEntityRef);
        }
        foreach (var source in sources) sourceRefs.Add(source.SourceObservationSetRef);
    }

    private static int Count(Dictionary<MapEntityKind, HashSet<string>> values, MapEntityKind kind) => values.TryGetValue(kind, out var refs) ? refs.Count : 0;
    private static string Counts(IEnumerable<MapEntity>? firstEntities, IEnumerable<SourceObservationSet>? firstSources, IEnumerable<MapEntity>? secondEntities = null, IEnumerable<SourceObservationSet>? secondSources = null)
    {
        var entities = (firstEntities ?? []).Concat(secondEntities ?? []).GroupBy(value => value.MapEntityRef, StringComparer.Ordinal).Select(value => value.First()).ToArray();
        var sources = (firstSources ?? []).Concat(secondSources ?? []).Select(value => value.SourceObservationSetRef).Distinct(StringComparer.Ordinal).Count();
        return JoinCounts(
            entities.Count(value => value.EntityKind == MapEntityKind.MapSurface),
            entities.Count(value => value.EntityKind == MapEntityKind.MapControl),
            entities.Count(value => value.EntityKind == MapEntityKind.MapDataGrid),
            sources);
    }

    private static string JoinCounts(int surfaces, int controls, int grids, int sources) =>
        $"{surfaces} {(surfaces == 1 ? "surface" : "surfaces")}  ·  "
        + $"{controls} {(controls == 1 ? "control" : "controls")}  ·  "
        + $"{grids} Data {(grids == 1 ? "Grid" : "Grids")}  ·  "
        + $"{sources} {(sources == 1 ? "source" : "sources")}";

    private static string Detail(int blockers, int warnings, int sources)
    {
        var values = new List<string>();
        if (blockers > 0) values.Add($"{blockers} blocking items");
        if (warnings > 0) values.Add($"{warnings} warnings");
        values.Add(SourceCount(sources));
        return string.Join("  ·  ", values);
    }

    private static string SourceCount(int sources) => $"{sources} {(sources == 1 ? "source" : "sources")}";

    private static DateTimeOffset ParseDate(string? value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;
    private static string FormatDate(string? value)
    {
        var date = ParseDate(value);
        if (date == DateTimeOffset.MinValue) return "Update time unavailable";
        var local = date.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (local.Date == now.Date) return $"Today, {local:h:mm tt}";
        if (local.Date == now.Date.AddDays(-1)) return $"Yesterday, {local:h:mm tt}";
        return local.ToString(local.Year == now.Year ? "MMM d, h:mm tt" : "MMM d, yyyy");
    }

    private static string Initials(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
