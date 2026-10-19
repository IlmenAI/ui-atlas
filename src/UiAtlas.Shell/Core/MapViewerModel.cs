using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V3;

namespace UIMaps.Application.Core;

public enum MapViewerWorld { Observed, Structured, Interpreted, Atlas }
public enum MapViewerPerspective { SurfaceAnalysis, InteractionGraph }

public static class MapSurfaceViewModes
{
    public const string Screenshot = "Screenshot";
    public const string Controls = "Controls";
    public const string Overlay = "Overlay";
    public const string Structure = "Structure";
    public const string StructureOverlay = "Structure Overlay";
    public static IReadOnlyList<string> All { get; } = [Screenshot, Controls, Overlay, Structure, StructureOverlay];
}

public sealed class MapViewerHierarchyNode(MapEntity entity)
{
    public MapEntity Entity { get; } = entity.Clone();
    public string EntityRef => Entity.MapEntityRef;
    public string Name => Entity.DisplayName;
    public string Detail => $"{MapViewerModel.KindLabel(Entity.EntityKind)} · {Entity.EvidenceLinks.Count} evidence";
    public bool IsSurface => Entity.EntityKind == MapEntityKind.MapSurface;
    public bool IsVariant => Entity.EntityKind == MapEntityKind.MapSurfaceVariant;
    public ObservableCollection<MapViewerHierarchyNode> Children { get; } = [];
    public bool IsExpanded { get; set; } = entity.EntityKind is MapEntityKind.MapApplication or MapEntityKind.MapApplicationState;
}

public sealed record MapViewerControl(
    string EntityRef,
    string Name,
    string Role,
    RectPx? Bounds,
    int EvidenceCount);

public sealed class MapViewerVariant
{
    public required MapEntity Entity { get; init; }
    public required string SurfaceRef { get; init; }
    public required IReadOnlyList<MapViewerControl> Controls { get; init; }
    public required IReadOnlyList<string> SourceObservationRefs { get; init; }
    public MapVisualEvidence? VisualEvidence { get; init; }
    public string EntityRef => Entity.MapEntityRef;
    public string Name => Entity.DisplayName;
    public string Detail => $"{Controls.Count} controls · {SourceObservationRefs.Count} observation(s)";
    public bool HasInspectableEvidence => Controls.Count > 0 || (VisualEvidence is not null && Bounds is not null);
    public RectPx? Bounds => Entity.EvidenceLinks.FirstOrDefault(value =>
            Positive(value.BoundsPx)
            && (VisualEvidence is null || value.SourceObservationSetRef == VisualEvidence.SourceObservationSetRef))?.BoundsPx?.Clone()
        ?? Entity.EvidenceLinks.FirstOrDefault(value => Positive(value.BoundsPx))?.BoundsPx?.Clone();
    private static bool Positive(RectPx? bounds) => bounds is { Width: > 0, Height: > 0 };
}

public sealed class MapViewerSurface
{
    public required MapEntity Entity { get; init; }
    public required ObservableCollection<MapViewerVariant> Variants { get; init; }
    public required IReadOnlyList<MapViewerControl> Controls { get; init; }
    public string EntityRef => Entity.MapEntityRef;
    public string Name => Entity.DisplayName;
    public string Role => string.IsNullOrWhiteSpace(Entity.Role) ? "surface" : Entity.Role;
    public string Detail => $"{Variants.Count} variant(s) · {Controls.Count} controls";
    public bool HasInspectableEvidence => Controls.Count > 0 || Variants.Any(value => value.HasInspectableEvidence);
    public RectPx? Bounds => Entity.EvidenceLinks.FirstOrDefault(value => Positive(value.BoundsPx))?.BoundsPx?.Clone()
        ?? Variants.Select(value => value.Bounds).FirstOrDefault(Positive)?.Clone();
    private static bool Positive(RectPx? bounds) => bounds is { Width: > 0, Height: > 0 };
}

public sealed record MapViewerStage(string Name, string Subtitle, IReadOnlyList<MapViewerStageCard> Cards);

public sealed record MapViewerStageCard(
    string EntityRef,
    string SurfaceRef,
    string VariantRef,
    string Name,
    string Detail,
    string Stage,
    MapViewerSurface? Surface,
    MapViewerVariant? Variant);

public sealed record MapViewerTransition(
    string InteractionRef,
    ulong Sequence,
    string SourceSurfaceRef,
    string DestinationSurfaceRef,
    string Label,
    string Outcome);

public sealed class MapViewerModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, MapEntity> _entities;
    private MapViewerWorld _world = MapViewerWorld.Observed;
    private MapViewerPerspective _perspective = MapViewerPerspective.SurfaceAnalysis;
    private MapViewerSurface? _selectedSurface;
    private MapViewerVariant? _selectedVariant;
    private MapViewerTransition? _selectedTransition;
    private string _surfaceViewMode = MapSurfaceViewModes.Screenshot;

    public MapViewerModel(MapsLibraryRecord record, MapVisualEvidenceStore? visualStore = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
        var source = record.Revision?.Entities ?? record.Draft?.Entities ?? [];
        _entities = source.Where(value => value.ReviewState != ReviewState.ReviewRejected)
            .GroupBy(value => value.MapEntityRef, StringComparer.Ordinal)
            .Select(value => value.First().Clone())
            .ToDictionary(value => value.MapEntityRef, StringComparer.Ordinal);
        Title = record.Session?.Target?.DisplayName
            ?? _entities.Values.FirstOrDefault(value => value.EntityKind == MapEntityKind.MapApplication)?.DisplayName
            ?? "Recorded UI Map";
        Subtitle = record.Revision is not null ? record.Entry.IsReady && !record.Entry.Archived ? "Ready UI Map" : "Published UI Map" : "Draft UI Map";
        Surfaces = new(BuildSurfaces(record, visualStore ?? new MapVisualEvidenceStore()));
        Hierarchy = new(BuildHierarchy());
        Stages = new(BuildStages(record));
        Transitions = new(BuildTransitions(record));
        EntryPoints = new(BuildEntryPoints(record));
        UnreachableSurfaces = new(BuildUnreachableSurfaces());
        SelectedSurface = Surfaces
            .OrderByDescending(value => value.Controls.Count)
            .ThenByDescending(value => value.Variants.Count)
            .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public MapsLibraryRecord Record { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public ObservableCollection<MapViewerSurface> Surfaces { get; }
    public ObservableCollection<MapViewerHierarchyNode> Hierarchy { get; }
    public ObservableCollection<MapViewerStage> Stages { get; }
    public ObservableCollection<MapViewerTransition> Transitions { get; }
    public ObservableCollection<MapViewerSurface> EntryPoints { get; }
    public ObservableCollection<MapViewerSurface> UnreachableSurfaces { get; }
    public IReadOnlyList<string> SurfaceViewModes { get; } = MapSurfaceViewModes.All;
    public MapViewerWorld World { get => _world; set => Set(ref _world, value); }
    public MapViewerPerspective Perspective { get => _perspective; set => Set(ref _perspective, value); }
    public string SurfaceViewMode { get => _surfaceViewMode; set { if (MapSurfaceViewModes.All.Contains(value, StringComparer.Ordinal)) Set(ref _surfaceViewMode, value); } }
    public MapViewerSurface? SelectedSurface
    {
        get => _selectedSurface;
        set
        {
            if (!Set(ref _selectedSurface, value)) return;
            SelectedVariant = value?.Variants.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedSurfaceTitle));
            OnPropertyChanged(nameof(SelectedSurfaceDetail));
        }
    }
    public MapViewerVariant? SelectedVariant
    {
        get => _selectedVariant;
        set
        {
            if (!Set(ref _selectedVariant, value)) return;
            if (value is not null && (SelectedSurface is null || SelectedSurface.EntityRef != value.SurfaceRef))
                _selectedSurface = Surfaces.FirstOrDefault(surface => surface.EntityRef == value.SurfaceRef);
            OnPropertyChanged(nameof(SelectedSurface));
            OnPropertyChanged(nameof(SelectedSurfaceTitle));
            OnPropertyChanged(nameof(SelectedSurfaceDetail));
        }
    }
    public MapViewerTransition? SelectedTransition { get => _selectedTransition; set => Set(ref _selectedTransition, value); }
    public string SelectedSurfaceTitle => SelectedVariant?.Name ?? SelectedSurface?.Name ?? "Select a surface";
    public string SelectedSurfaceDetail => SelectedVariant?.Detail ?? SelectedSurface?.Detail ?? "Choose a surface from the hierarchy or topology.";

    public bool SelectEntity(string entityRef)
    {
        var variant = Surfaces.SelectMany(value => value.Variants).FirstOrDefault(value => value.EntityRef == entityRef);
        if (variant is not null) { SelectedVariant = variant; return true; }
        var surface = Surfaces.FirstOrDefault(value => value.EntityRef == ResolveSurfaceRef(entityRef));
        if (surface is null) return false;
        SelectedSurface = surface;
        return true;
    }

    private IReadOnlyList<MapViewerSurface> BuildSurfaces(MapsLibraryRecord record, MapVisualEvidenceStore visualStore)
    {
        var sources = record.Revision?.Sources ?? record.Draft?.Sources ?? [];
        return _entities.Values.Where(value => value.EntityKind == MapEntityKind.MapSurface)
            .OrderBy(value => value.ReviewedOrder).ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(surface =>
            {
                var controls = ControlsFor(surface.MapEntityRef);
                var variants = _entities.Values.Where(value => value.EntityKind == MapEntityKind.MapSurfaceVariant && ResolveSurfaceRef(value.MapEntityRef) == surface.MapEntityRef)
                    .OrderBy(value => value.ReviewedOrder).ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(variant =>
                    {
                        var sourceRefs = sources.Where(source => source.SurfaceObservations.Any(observation => observation.ResultingMapVariantRef == variant.MapEntityRef))
                            .Select(source => source.SourceObservationSetRef).Distinct(StringComparer.Ordinal).ToArray();
                        return new MapViewerVariant
                        {
                            Entity = variant.Clone(), SurfaceRef = surface.MapEntityRef,
                            Controls = ControlsFor(variant.MapEntityRef, sourceRefs), SourceObservationRefs = sourceRefs,
                            VisualEvidence = sourceRefs.Select(visualStore.TryLoad).FirstOrDefault(value => value is not null),
                        };
                    })
                    .Where(value => value.HasInspectableEvidence)
                    .ToList();
                return new MapViewerSurface { Entity = surface.Clone(), Variants = new(variants), Controls = controls };
            })
            .Where(value => value.HasInspectableEvidence)
            .ToList();
    }

    private IReadOnlyList<MapViewerControl> ControlsFor(string rootRef, IReadOnlyCollection<string>? sourceObservationRefs = null)
    {
        var surfaceRef = ResolveSurfaceRef(rootRef);
        return _entities.Values.Where(value =>
                (value.EntityKind is MapEntityKind.MapControl or MapEntityKind.MapDataGrid)
                && ResolveSurfaceRef(value.MapEntityRef) == surfaceRef
                && (sourceObservationRefs is null
                    || value.EvidenceLinks.Any(link => sourceObservationRefs.Contains(link.SourceObservationSetRef, StringComparer.Ordinal))))
            .OrderBy(value => value.ReviewedOrder).ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(value =>
            {
                var evidence = value.EvidenceLinks.FirstOrDefault(link =>
                    link.BoundsPx is { Width: > 0, Height: > 0 }
                    && (sourceObservationRefs is null || sourceObservationRefs.Contains(link.SourceObservationSetRef, StringComparer.Ordinal)));
                return new MapViewerControl(
                    value.MapEntityRef, value.DisplayName, value.Role,
                    evidence?.BoundsPx?.Clone(), value.EvidenceLinks.Count);
            }).ToArray();
    }

    private IReadOnlyList<MapViewerHierarchyNode> BuildHierarchy()
    {
        var visibleSurfaceRefs = Surfaces.Select(value => value.EntityRef).ToHashSet(StringComparer.Ordinal);
        var visibleVariantRefs = Surfaces.SelectMany(value => value.Variants).Select(value => value.EntityRef).ToHashSet(StringComparer.Ordinal);
        var nodes = _entities.Values.Where(value => value.EntityKind switch
            {
                MapEntityKind.MapApplication or MapEntityKind.MapApplicationState => true,
                MapEntityKind.MapSurface => visibleSurfaceRefs.Contains(value.MapEntityRef),
                MapEntityKind.MapSurfaceVariant => visibleVariantRefs.Contains(value.MapEntityRef),
                _ => visibleSurfaceRefs.Contains(ResolveSurfaceRef(value.MapEntityRef)),
            })
            .ToDictionary(value => value.MapEntityRef, value => new MapViewerHierarchyNode(value), StringComparer.Ordinal);
        var roots = new List<MapViewerHierarchyNode>();
        foreach (var node in nodes.Values.OrderBy(value => value.Entity.ReviewedOrder).ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(node.Entity.ParentEntityRef) && nodes.TryGetValue(node.Entity.ParentEntityRef, out var parent)) parent.Children.Add(node);
            else roots.Add(node);
        }
        return roots;
    }

    private IReadOnlyList<MapViewerStage> BuildStages(MapsLibraryRecord record)
    {
        var apps = _entities.Values.Where(value => value.EntityKind == MapEntityKind.MapApplication).ToList();
        var states = _entities.Values.Where(value => value.EntityKind == MapEntityKind.MapApplicationState).ToList();
        var observed = Surfaces.SelectMany(surface => surface.Variants.Select(variant => Card("Observed", surface, variant))).ToList();
        var structured = Surfaces.Select(surface => Card("Structured", surface, null)).ToList();
        var interpreted = Surfaces.Select(surface => Card("Interpreted", surface, null)).ToList();
        var atlas = record.Revision is { Immutable: true } ? Surfaces.Select(surface => Card("Atlas", surface, null)).ToList() : [];
        return
        [
            new("App identity", "Recorded application", apps.Select(value => ContextCard("App identity", value)).ToList()),
            new("Process", "Application context", states.Select(value => ContextCard("Process", value)).ToList()),
            new("Observed", "Fused evidence", observed),
            new("Structured", "Ownership resolved", structured),
            new("Interpreted", "Semantic meaning", interpreted),
            new("Atlas", "Canonical surfaces", atlas),
        ];
    }

    private static MapViewerStageCard Card(string stage, MapViewerSurface surface, MapViewerVariant? variant) => new(
        variant?.EntityRef ?? surface.EntityRef, surface.EntityRef, variant?.EntityRef ?? "", variant?.Name ?? surface.Name,
        variant?.Detail ?? surface.Detail, stage, surface, variant);

    private static MapViewerStageCard ContextCard(string stage, MapEntity entity) => new(
        entity.MapEntityRef, "", "", entity.DisplayName, KindLabel(entity.EntityKind), stage, null, null);

    private IReadOnlyList<MapViewerTransition> BuildTransitions(MapsLibraryRecord record)
    {
        var interactions = record.Revision?.Interactions ?? record.Draft?.Interactions ?? [];
        var visibleSurfaceRefs = Surfaces.Select(value => value.EntityRef).ToHashSet(StringComparer.Ordinal);
        return interactions.Where(value => value.ReviewState != ReviewState.ReviewRejected)
            .OrderBy(value => value.SequenceNumber)
            .SelectMany(value => value.ResultSurfaceVariantRefs.Select(result => new MapViewerTransition(
                value.InteractionRef, value.SequenceNumber, ResolveSurfaceRef(value.SourceMapEntityRef), ResolveSurfaceRef(result),
                Label(value.Kind.ToString(), "MapInteraction"), Label(value.Outcome.ToString(), "MapInteraction"))))
            .Where(value => visibleSurfaceRefs.Contains(value.SourceSurfaceRef)
                && visibleSurfaceRefs.Contains(value.DestinationSurfaceRef))
            .ToArray();
    }

    private IReadOnlyList<MapViewerSurface> BuildEntryPoints(MapsLibraryRecord record)
    {
        var interactions = record.Revision?.Interactions ?? record.Draft?.Interactions ?? [];
        var refs = interactions.Where(value => value.ReviewState != ReviewState.ReviewRejected)
            .GroupBy(value => value.RecordingSessionRef ?? "", StringComparer.Ordinal)
            .Select(group => group.OrderBy(value => value.SequenceNumber).FirstOrDefault())
            .Where(value => value is not null)
            .Select(value => ResolveSurfaceRef(value!.SourceMapEntityRef))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        return Surfaces.Where(value => refs.Contains(value.EntityRef)).ToArray();
    }

    private IReadOnlyList<MapViewerSurface> BuildUnreachableSurfaces()
    {
        var reachable = EntryPoints.Select(value => value.EntityRef).ToHashSet(StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var edge in Transitions.Where(value => reachable.Contains(value.SourceSurfaceRef)))
                changed |= reachable.Add(edge.DestinationSurfaceRef);
        }
        return Surfaces.Where(value => !reachable.Contains(value.EntityRef)).ToArray();
    }

    private string ResolveSurfaceRef(string entityRef)
    {
        if (!_entities.TryGetValue(entityRef, out var current)) return "";
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(current.MapEntityRef))
        {
            if (current.EntityKind == MapEntityKind.MapSurface) return current.MapEntityRef;
            if (string.IsNullOrWhiteSpace(current.ParentEntityRef) || !_entities.TryGetValue(current.ParentEntityRef, out current!)) return "";
        }
        return "";
    }

    public static string KindLabel(MapEntityKind kind) => Label(kind.ToString(), "Map");
    private static string Label(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(propertyName); return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
}
