using System.Security.Cryptography;
using Google.Protobuf;
using UIMaps.Protocol.V2_1;

namespace UIMaps.Map.Core.Grid;

public sealed class DataGridMapDraft
{
    public required string DraftRef { get; init; }
    public ulong Version { get; internal set; } = 1;
    public required DataGridMapPackage Package { get; init; }
}
public sealed class DataGridMapException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class StableGridRefAllocator
{
    public string Allocate(string kind) => $"uimaps:{kind}:{Guid.NewGuid():N}";
}

public sealed class DataGridMapStore(string directory, StableGridRefAllocator? allocator = null)
{
    private readonly StableGridRefAllocator _allocator = allocator ?? new();
    private readonly Dictionary<string, DataGridMapDraft> _drafts = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public DataGridMapDraft CreateDraft(string applicationMatcher, string surfaceMatcher, DataGridCandidate source, IReadOnlyDictionary<string, string>? reviewedHeaders = null)
    {
        if (string.IsNullOrWhiteSpace(applicationMatcher) || string.IsNullOrWhiteSpace(surfaceMatcher)) throw new DataGridMapException("GRID_MAP_SCOPE_REQUIRED", "Application and surface matchers are required.");
        var appRef = _allocator.Allocate("app"); var surfaceRef = _allocator.Allocate("surface"); var variantRef = _allocator.Allocate("surface-variant"); var gridRef = _allocator.Allocate("data-grid");
        var package = new DataGridMapPackage { SchemaVersion = "2.1", AppRef = appRef, CreatedAt = DateTimeOffset.UtcNow.ToString("O") };
        package.ApplicationMatchers.Add(applicationMatcher);
        package.Surfaces.Add(new MappedSurface { SurfaceRef = surfaceRef, ApplicationRef = appRef, SurfaceVariantRefs = { variantRef }, Matchers = { surfaceMatcher } });
        var grid = new MappedDataGrid { DataGridRef = gridRef, SurfaceRef = surfaceRef, SurfaceVariantRefs = { variantRef }, DisplayName = "Reviewed data grid", ExpectedRelativeBounds = source.BoundsPx.Clone(), MatcherPolicyRef = "grid-matcher:v1" };
        grid.SourceEvidenceRefs.Add(source.CandidateRef);
        foreach (var column in source.ColumnCandidates.OrderBy(x => x.Ordinal))
        {
            var name = reviewedHeaders?.GetValueOrDefault(column.CandidateRef) ?? column.Aliases.FirstOrDefault() ?? $"Column {column.Ordinal + 1}";
            var mapped = new MappedGridColumn { GridColumnRef = _allocator.Allocate("grid-column"), DataGridRef = gridRef, Ordinal = column.Ordinal, CanonicalDisplayName = name, ExpectedWidthRatio = column.WidthRatio };
            mapped.HeaderMatchers.Add(name);
            mapped.SourceEvidenceRefs.Add(column.CandidateRef);
            grid.Columns.Add(mapped);
            grid.ExpectedHeaderSet.Add(name);
        }
        grid.ReviewerAssertions.Add("review-required:bounds,headers,columns,order");
        package.DataGrids.Add(grid);
        package.EvidenceManifest.Add(source.CandidateRef);
        var draft = new DataGridMapDraft { DraftRef = _allocator.Allocate("grid-draft"), Package = package };
        lock (_gate) _drafts.Add(draft.DraftRef, Clone(draft));
        return Clone(draft);
    }

    public DataGridMapDraft UpdateDraft(string draftRef, ulong expectedVersion, Action<DataGridMapPackage> update)
    {
        lock (_gate)
        {
            if (!_drafts.TryGetValue(draftRef, out var current)) throw new DataGridMapException("GRID_MAP_DRAFT_NOT_FOUND", "Grid Map draft was not found.");
            if (current.Version != expectedVersion) throw new DataGridMapException("GRID_MAP_DRAFT_CONFLICT", "Grid Map draft changed; reload before applying corrections.");
            var next = Clone(current); update(next.Package); next.Version++;
            _drafts[draftRef] = Clone(next); return Clone(next);
        }
    }

    public DataGridMapValidationReport Validate(DataGridMapPackage package)
    {
        var report = new DataGridMapValidationReport();
        void Issue(string severity, string code, string message) => report.Issues.Add(new DataGridMapValidationIssue { Severity = severity, Code = code, Message = message });
        if (package.SchemaVersion != "2.1") Issue("error", "GRID_MAP_SCHEMA_INVALID", "Schema version must be 2.1.");
        if (string.IsNullOrWhiteSpace(package.AppRef) || package.ApplicationMatchers.Count == 0) Issue("error", "GRID_MAP_APPLICATION_INVALID", "Application identity and matcher are required.");
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var surface in package.Surfaces)
        {
            if (!refs.Add(surface.SurfaceRef)) Issue("error", "GRID_MAP_REF_DUPLICATE", "Surface refs must be unique.");
            if (surface.ApplicationRef != package.AppRef || surface.Matchers.Count == 0) Issue("error", "GRID_MAP_SURFACE_INVALID", "Surface ownership and matcher are required.");
        }
        foreach (var grid in package.DataGrids)
        {
            if (!refs.Add(grid.DataGridRef) || package.Surfaces.All(x => x.SurfaceRef != grid.SurfaceRef)) Issue("error", "GRID_MAP_GRID_INVALID", "Grid ref and surface closure are required.");
            if (grid.SourceEvidenceRefs.Count == 0 || grid.ReviewerAssertions.Count == 0) Issue("error", "GRID_MAP_REVIEW_LINEAGE_REQUIRED", "Source evidence and explicit reviewer assertions are required.");
            if (grid.ExpectedRelativeBounds is null || grid.ExpectedRelativeBounds.Width <= 0 || grid.ExpectedRelativeBounds.Height <= 0) Issue("error", "GRID_MAP_BOUNDS_INVALID", "Reviewed grid bounds are required.");
            var ordinals = new HashSet<int>();
            foreach (var column in grid.Columns)
            {
                if (!refs.Add(column.GridColumnRef) || column.DataGridRef != grid.DataGridRef || !ordinals.Add(column.Ordinal)) Issue("error", "GRID_MAP_COLUMN_INVALID", "Column refs, ownership, and ordinals must be unique.");
                if (string.IsNullOrWhiteSpace(column.CanonicalDisplayName) || column.SourceEvidenceRefs.Count == 0) Issue("error", "GRID_MAP_COLUMN_LINEAGE_REQUIRED", "Every reviewed column needs a display name and source evidence.");
            }
        }
        report.Valid = report.Issues.All(x => x.Severity != "error");
        report.CanonicalPayloadHash = HashCanonical(package);
        return report;
    }

    public DataGridMapPackage Publish(string draftRef, ulong expectedVersion, string publisherIdentity, DateTimeOffset publishedAt)
    {
        DataGridMapDraft draft;
        lock (_gate)
        {
            if (!_drafts.TryGetValue(draftRef, out var stored)) throw new DataGridMapException("GRID_MAP_DRAFT_NOT_FOUND", "Grid Map draft was not found.");
            if (stored.Version != expectedVersion) throw new DataGridMapException("GRID_MAP_DRAFT_CONFLICT", "Grid Map draft changed before publication.");
            draft = Clone(stored);
        }
        if (string.IsNullOrWhiteSpace(publisherIdentity)) throw new DataGridMapException("GRID_MAP_REVIEWER_REQUIRED", "Publisher identity is required.");
        var package = draft.Package.Clone();
        foreach (var grid in package.DataGrids)
        {
            grid.ReviewerAssertions.Remove("review-required:bounds,headers,columns,order");
            grid.ReviewerAssertions.Add("reviewed-by:" + publisherIdentity);
        }
        package.PublisherIdentity = publisherIdentity; package.PublishedAt = publishedAt.ToUniversalTime().ToString("O");
        var validation = Validate(package);
        if (!validation.Valid) throw new DataGridMapException("GRID_MAP_VALIDATION_FAILED", string.Join("; ", validation.Issues.Select(x => x.Code)));
        package.CanonicalPayloadHash = validation.CanonicalPayloadHash;
        package.MapRevisionRef = "uimaps:grid-map-revision:sha256:" + validation.CanonicalPayloadHash;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, validation.CanonicalPayloadHash + ".gridmap.pb");
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, package.ToByteArray());
        if (File.Exists(path)) File.Delete(temp); else File.Move(temp, path, false);
        var activePath = Path.Combine(directory, "active"); var activeTemp = activePath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(activeTemp, package.MapRevisionRef); File.Move(activeTemp, activePath, true);
        return package.Clone();
    }

    public DataGridMapPackage Load(string mapRevisionRef)
    {
        var hash = mapRevisionRef.Split(':').Last(); var path = Path.Combine(directory, hash + ".gridmap.pb");
        if (!File.Exists(path)) throw new DataGridMapException("GRID_MAP_REVISION_NOT_FOUND", "Grid Map revision was not found.");
        var package = DataGridMapPackage.Parser.ParseFrom(File.ReadAllBytes(path));
        if (package.MapRevisionRef != mapRevisionRef || package.CanonicalPayloadHash != HashCanonical(package)) throw new DataGridMapException("GRID_MAP_INTEGRITY_FAILED", "Grid Map revision integrity failed.");
        return package;
    }

    public static string HashCanonical(DataGridMapPackage package)
    {
        var canonical = package.Clone(); canonical.MapRevisionRef = ""; canonical.CanonicalPayloadHash = "";
        return Convert.ToHexString(SHA256.HashData(canonical.ToByteArray())).ToLowerInvariant();
    }

    private static DataGridMapDraft Clone(DataGridMapDraft value) => new() { DraftRef = value.DraftRef, Version = value.Version, Package = value.Package.Clone() };
}
