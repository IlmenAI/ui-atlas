using Google.Protobuf;
using UIMaps.Protocol.V2_1;
using UIMaps.Protocol.V3;

namespace UIMaps.Map.Core;

/// <summary>Versioned schema evidence on one stable grid. Selection materializes
/// the descriptor for existing consumers without changing any published revision.</summary>
public static class GridSchemaHistory
{
    public static void EnsureBaseline(UiMapDraft draft, MapEntity grid)
    {
        var descriptor = grid.HostedDataGrid;
        if (descriptor is null || descriptor.SchemaVersions.Count > 0) return;
        var version = new HostedGridSchemaVersion
        {
            SchemaVersionRef = "schema:" + grid.MapEntityRef + ":1",
            Snapshot = Snapshot(descriptor),
            SelectionReason = "Original retained schema",
        };
        version.Columns.AddRange(draft.Entities.Where(e => e.EntityKind == MapEntityKind.MapGridColumn
            && e.ParentEntityRef == grid.MapEntityRef && e.Applicable).OrderBy(e => e.ReviewedOrder).Select(e => e.Clone()));
        AddUnique(version.SourceGridRefs, descriptor.SourceGridRef);
        AddUnique(version.SourceObservationSetRefs, descriptor.SourceObservationSetRef);
        descriptor.SchemaVersions.Add(version);
        descriptor.DefaultSchemaVersionRef = version.SchemaVersionRef;
    }

    public static void Observe(UiMapDraft draft, MapEntity grid, MappedDataGrid incoming,
        string gridRevision, SourceObservationSet source, StableMapRefAllocator allocator)
    {
        EnsureBaseline(draft, grid);
        var descriptor = grid.HostedDataGrid;
        var widths = incoming.Columns.OrderBy(c => c.Ordinal).Select(c => c.ExpectedWidthRatio).ToArray();
        var headers = incoming.ExpectedHeaderSet.ToArray();
        var existing = descriptor.SchemaVersions.FirstOrDefault(v => v.SourceGridRefs.Contains(incoming.DataGridRef)
            && v.Snapshot.GridMapRevisionRef == gridRevision)
            ?? descriptor.SchemaVersions.FirstOrDefault(v => v.Snapshot.ExpectedHeaders.SequenceEqual(headers)
                && (v.ColumnWidthRatios.Count == 0 || SameWidths(v.ColumnWidthRatios, widths)));
        if (existing is not null)
        {
            if (existing.ColumnWidthRatios.Count == 0) existing.ColumnWidthRatios.AddRange(widths);
            AddUnique(existing.SourceGridRefs, incoming.DataGridRef);
            AddUnique(existing.SourceObservationSetRefs, source.SourceObservationSetRef);
            if (existing.ObservedAt.Length == 0) existing.ObservedAt = source.CapturedAt;
            if (!descriptor.SchemaDefaultPinned) SelectAutomatic(draft, grid);
            return;
        }
        var current = descriptor.SchemaVersions.Single(v => v.SchemaVersionRef == descriptor.DefaultSchemaVersionRef);
        var sameColumns = SameWidths(current.ColumnWidthRatios, widths) && current.Columns.Count == headers.Length;
        var version = new HostedGridSchemaVersion
        {
            SchemaVersionRef = "schema:" + grid.MapEntityRef + ":" + (descriptor.SchemaVersions.Count + 1),
            Snapshot = Snapshot(descriptor), ObservedAt = source.CapturedAt,
            SelectionReason = "Alternative schema retained; current default kept",
        };
        version.Snapshot.GridMapRevisionRef = gridRevision;
        version.Snapshot.SourceGridRef = incoming.DataGridRef;
        version.Snapshot.SourceObservationSetRef = source.SourceObservationSetRef;
        version.Snapshot.ExpectedHeaders.Clear(); version.Snapshot.ExpectedHeaders.AddRange(headers);
        version.Snapshot.Limitations.Clear(); version.Snapshot.Limitations.AddRange(incoming.KnownLimitations);
        version.Snapshot.SchemaStatus = incoming.SchemaPartial ? GridSchemaStatus.GridSchemaPartial : GridSchemaStatus.GridSchemaReviewed;
        // Legacy reviewed-header observations do not contain a reviewed image recipe.
        version.Snapshot.ReviewedImageSchema = null;
        version.Snapshot.ExplorationReceipt = null;
        version.Snapshot.SourceAnalysisRef = ""; version.Snapshot.SourceCandidateRef = "";
        version.ColumnWidthRatios.AddRange(widths);
        version.SourceObservationSetRefs.Add(source.SourceObservationSetRef);
        version.SourceGridRefs.Add(incoming.DataGridRef);
        foreach (var column in incoming.Columns.OrderBy(c => c.Ordinal))
        {
            var entity = sameColumns ? current.Columns[column.Ordinal].Clone() : new MapEntity
            {
                MapEntityRef = allocator.Allocate("grid-column"), EntityKind = MapEntityKind.MapGridColumn,
                ParentEntityRef = grid.MapEntityRef, ApplicationRef = grid.ApplicationRef, Role = "grid-column",
            };
            entity.DisplayName = column.CanonicalDisplayName; entity.ReviewedOrder = column.Ordinal;
            entity.Applicable = true; entity.ReviewState = ReviewState.ReviewAccepted;
            entity.Matchers.Clear();
            foreach (var label in column.HeaderMatchers) entity.Matchers.Add(new MapMatcher
            { MatcherRef = allocator.Allocate("map-matcher"), Family = "gridColumn", Key = "header", ExpectedValue = label, Weight = 1, ReviewState = ReviewState.ReviewAccepted });
            version.Columns.Add(entity);
        }
        descriptor.SchemaVersions.Add(version);
        if (!descriptor.SchemaDefaultPinned) SelectAutomatic(draft, grid);
    }

    public static void Select(UiMapDraft draft, MapEntity grid, string versionRef)
    {
        EnsureBaseline(draft, grid);
        if (versionRef == "automatic")
        {
            grid.HostedDataGrid.SchemaDefaultPinned = false;
            SelectAutomatic(draft, grid);
        }
        else
        {
            var version = grid.HostedDataGrid.SchemaVersions.SingleOrDefault(v => v.SchemaVersionRef == versionRef)
                ?? throw new UiMapException("MAP_GRID_SCHEMA_UNKNOWN", "Select a retained schema version of this grid.");
            Activate(draft, grid, version, true);
        }
    }

    private static void SelectAutomatic(UiMapDraft draft, MapEntity grid)
    {
        var descriptor = grid.HostedDataGrid;
        // Start from the earliest schema so unpinning is independent of which
        // version the user previously pinned. Conflicting layouts never win by age.
        var best = descriptor.SchemaVersions[0];
        foreach (var candidate in descriptor.SchemaVersions.Skip(1))
        {
            if (best.Columns.Count == 0 && best.Snapshot.ExpectedHeaders.Count == 0
                && candidate.Snapshot.SchemaStatus == GridSchemaStatus.GridSchemaReviewed
                && candidate.Snapshot.ExpectedHeaders.Any(h => !string.IsNullOrWhiteSpace(h)))
            {
                candidate.SelectionReason = "Automatically selected: first reviewed column schema";
                best = candidate;
                continue;
            }
            if (!SameWidths(best.ColumnWidthRatios, candidate.ColumnWidthRatios)
                || best.Snapshot.ExpectedHeaders.Count != candidate.Snapshot.ExpectedHeaders.Count) continue;
            if (best.Snapshot.SchemaStatus == GridSchemaStatus.GridSchemaReviewed
                && candidate.Snapshot.SchemaStatus != GridSchemaStatus.GridSchemaReviewed) continue;
            var old = best.Snapshot.ExpectedHeaders; var next = candidate.Snapshot.ExpectedHeaders;
            if (old.Where((label, i) => !string.IsNullOrWhiteSpace(label)
                    && !string.Equals(label.Trim(), next[i].Trim(), StringComparison.Ordinal)).Any()) continue;
            if (next.Count(h => !string.IsNullOrWhiteSpace(h)) <= old.Count(h => !string.IsNullOrWhiteSpace(h))) continue;
            candidate.SelectionReason = "Automatically selected: matching physical columns and recovered names";
            best = candidate;
        }
        Activate(draft, grid, best, false);
    }

    private static void Activate(UiMapDraft draft, MapEntity grid, HostedGridSchemaVersion version, bool pinned)
    {
        var history = grid.HostedDataGrid.SchemaVersions.Select(v => v.Clone()).ToArray();
        var descriptor = version.Snapshot.Clone();
        descriptor.SchemaVersions.AddRange(history);
        descriptor.DefaultSchemaVersionRef = version.SchemaVersionRef; descriptor.SchemaDefaultPinned = pinned;
        grid.HostedDataGrid = descriptor;
        foreach (var column in draft.Entities.Where(e => e.EntityKind == MapEntityKind.MapGridColumn && e.ParentEntityRef == grid.MapEntityRef))
            column.Applicable = false;
        foreach (var snapshot in version.Columns)
        {
            var column = draft.Entities.FirstOrDefault(e => e.MapEntityRef == snapshot.MapEntityRef);
            if (column is null) draft.Entities.Add(snapshot.Clone());
            else { column.DisplayName = snapshot.DisplayName; column.ReviewedOrder = snapshot.ReviewedOrder;
                column.Applicable = true; column.ReviewState = snapshot.ReviewState;
                column.Matchers.Clear(); column.Matchers.AddRange(snapshot.Matchers.Select(m => m.Clone())); }
            if (!draft.Relationships.Any(r => r.RelationshipKind == MapRelationshipKind.MapHasColumn && r.FromEntityRef == grid.MapEntityRef && r.ToEntityRef == snapshot.MapEntityRef))
                draft.Relationships.Add(new MapRelationship { MapRelationshipRef = new StableMapRefAllocator().Allocate("map-relationship"),
                    RelationshipKind = MapRelationshipKind.MapHasColumn, FromEntityRef = grid.MapEntityRef,
                    ToEntityRef = snapshot.MapEntityRef, ReviewState = ReviewState.ReviewAccepted });
        }
        foreach (var matcher in grid.Matchers.Where(m => m.Family == "grid" && m.Key == "headers"))
            matcher.ExpectedValue = string.Join('\u001f', descriptor.ExpectedHeaders.Order(StringComparer.OrdinalIgnoreCase));
    }

    private static HostedDataGridDescriptor Snapshot(HostedDataGridDescriptor value)
    {
        var copy = value.Clone(); copy.SchemaVersions.Clear(); copy.DefaultSchemaVersionRef = ""; copy.SchemaDefaultPinned = false; return copy;
    }
    private static bool SameWidths(IEnumerable<double> first, IEnumerable<double> second)
    {
        var a = first.ToArray(); var b = second.ToArray();
        return a.Length > 0 && a.Length == b.Length && a.Zip(b).All(p => p.First > 0 && p.Second > 0 && Math.Abs(p.First - p.Second) < .003);
    }
    private static void AddUnique(Google.Protobuf.Collections.RepeatedField<string> values, string value)
    { if (value.Length > 0 && !values.Contains(value)) values.Add(value); }
}
