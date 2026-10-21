using System.Text.Json;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Storage;

/// <summary>Authoring knowledge only. This store never changes raw evidence or execution permissions.</summary>
public static class MapParameterizationStore
{
    private const int MaxBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string LegacyPath(string mapPath) => Path.GetFullPath(mapPath) + ".parameters.json";

    public static string PathFor(string mapPath)
    {
        var root = MapBackendCompanion.PathFor(mapPath);
        var pointer = Path.Combine(root, "active.json");
        if (!File.Exists(pointer)) return LegacyPath(mapPath);
        MapBackendCompanion.RejectLinks(root);
        var generation = JsonSerializer.Deserialize<string>(File.ReadAllText(pointer));
        if (!Guid.TryParseExact(generation, "N", out _)) throw new InvalidDataException("Invalid map generation.");
        return Path.Combine(root, "generations", generation, "parameter-review.json");
    }

    public static MapParameterizationDocument Load(string mapPath, string logicalMapId) =>
        LoadFile(PathFor(mapPath), logicalMapId);

    public static MapParameterizationDocument LoadFile(string path, string logicalMapId)
    {
        if (!File.Exists(path)) return MapParameterizationDocument.Empty(logicalMapId);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("Parameter review file is unsafe or too large.");
        var value = JsonSerializer.Deserialize<MapParameterizationDocument>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException("Parameter review file is empty.");
        Validate(value, logicalMapId);
        return value;
    }

    public static MapParameterizationDocument Save(string mapPath, MapParameterizationDocument value, long expectedRevision,
        string? expectedStoragePath = null)
    {
        Validate(value, value.LogicalMapId);
        var originalPath = PathFor(mapPath);
        if (expectedStoragePath is not null && !originalPath.Equals(expectedStoragePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Map generation changed. Reload before saving.");
        var root = MapBackendCompanion.PathFor(mapPath);
        var lockPath = Directory.Exists(root) ? Path.Combine(root, "write.lock") : LegacyPath(mapPath) + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (PathFor(mapPath) != originalPath) throw new IOException("Map generation changed. Reload before saving.");
        var current = LoadFile(originalPath, value.LogicalMapId);
        if (current.Revision != expectedRevision)
            throw new IOException("Parameter review changed in another editor. Reload before saving.");
        var updated = value with { Revision = checked(expectedRevision + 1) };
        WriteAtomic(originalPath, updated);
        return updated;
    }

    public static void RebindFile(string path, string logicalMapId)
    {
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > MaxBytes || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Parameter review file is unsafe or too large.");
        var value = JsonSerializer.Deserialize<MapParameterizationDocument>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException("Parameter review file is empty.");
        Validate(value, value.LogicalMapId);
        WriteAtomic(path, value with { LogicalMapId = logicalMapId });
    }

    private static void WriteAtomic(string path, MapParameterizationDocument value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Parameter review exceeds its size limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Validate(MapParameterizationDocument value, string logicalMapId)
    {
        if (value.Format != MapParameterizationDocument.CurrentFormat || value.LogicalMapId != logicalMapId ||
            value.Revision < 0 || value.Proposals is null || value.Decisions is null ||
            value.Proposals.Count > 10_000 || value.Decisions.Count > 20_000)
            throw new InvalidDataException("Unsupported or foreign parameter review document.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in value.Proposals)
        {
            if (p is null || string.IsNullOrWhiteSpace(p.Id) || !ids.Add(p.Id) ||
                string.IsNullOrWhiteSpace(p.TargetId) || string.IsNullOrWhiteSpace(p.Fingerprint) ||
                p.ReuseKey is null || p.ReuseKey.Length is not (0 or 64) ||
                p.Bounds is null || p.Evidence is null || p.Evidence.Count > 100 ||
                p.Schema is { Columns: null } || p.Schema?.Columns.Count > 2000)
                throw new InvalidDataException("Invalid parameter proposal.");
            if (p.Schema is { } schema && (string.IsNullOrWhiteSpace(schema.Revision) ||
                schema.Columns.Any(c => c is null || string.IsNullOrWhiteSpace(c.ColumnKey) ||
                    c.Ordinal < 0 || !double.IsFinite(c.StartX) || !double.IsFinite(c.EndX) || c.EndX <= c.StartX) ||
                schema.Columns.Select(c => c.ColumnKey).Distinct(StringComparer.Ordinal).Count() != schema.Columns.Count ||
                schema.Columns.Select(c => c.Ordinal).Distinct().Count() != schema.Columns.Count))
                throw new InvalidDataException("Invalid physical column identities.");
            foreach (var e in p.Evidence)
                if (e is null || e.Summary?.Length > 16_000 || e.Source?.Length > 16_000)
                    throw new InvalidDataException("Invalid parameter evidence.");
            if (p.ParentProposalId is not null)
            {
                var parent = value.Proposals.FirstOrDefault(v => v.Id == p.ParentProposalId);
                if (parent is null || !parent.IsGrid || !p.IsGrid || p.ParentProposalId == p.Id ||
                    !Contains(parent.Bounds, p.Bounds))
                    throw new InvalidDataException("Grid subregion must be inside its parent grid.");
                var visited = new HashSet<string>(StringComparer.Ordinal) { p.Id };
                while (parent is not null)
                {
                    if (!visited.Add(parent.Id)) throw new InvalidDataException("Grid subregions contain a cycle.");
                    parent = value.Proposals.FirstOrDefault(v => v.Id == parent.ParentProposalId);
                }
            }
        }
        var decisions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in value.Decisions)
        {
            if (d is null || !decisions.Add(d.ProposalId) || !Enum.IsDefined(d.State) ||
                string.IsNullOrWhiteSpace(d.SourceFingerprint) || string.IsNullOrWhiteSpace(d.Reviewer) ||
                string.IsNullOrWhiteSpace(d.ParameterName) || d.ParameterName.Length > 128 ||
                string.IsNullOrWhiteSpace(d.ValueType) || d.ValueType.Length > 2048 ||
                string.IsNullOrWhiteSpace(d.Scope) || d.Scope.Length > 2048 || d.Note?.Length > 16_000 ||
                d.Documentation is null || d.Documentation.Count > 32 ||
                d.GridRule is not null && !Enum.IsDefined(d.GridRule.Mode))
                throw new InvalidDataException("Invalid parameter decision.");
            foreach (var source in d.Documentation)
                if (source is null || !IsDocumentationUrl(source.Url) || string.IsNullOrWhiteSpace(source.Title) ||
                    source.Title.Length > 512 || source.Excerpt?.Length > 16_000 || source.Applicability?.Length > 2048)
                    throw new InvalidDataException("Documentation requires a title and an HTTP(S) source.");
            var proposal = value.Proposals.FirstOrDefault(p => p.Id == d.ProposalId);
            if (proposal is not null && proposal.IsGrid != (d.GridRule is not null))
                throw new InvalidDataException("Only grid decisions require a grid variation rule.");
            if (d.State == ParameterReviewState.Accepted && proposal is not null &&
                proposal.Fingerprint == d.SourceFingerprint &&
                d.GridRule?.Mode == GridVariationMode.AccordingToSchema && proposal.Schema?.Columns.Count is not > 0)
                throw new InvalidDataException("Schema mode requires an attached mapping schema.");
        }
    }

    public static bool IsDocumentationUrl(string? url) => url?.Length <= 4096 &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static bool Contains(RectI outer, RectI inner) => inner is { Width: > 0, Height: > 0 } &&
        inner.X >= outer.X && inner.Y >= outer.Y &&
        (long)inner.X + inner.Width <= (long)outer.X + outer.Width &&
        (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height;
}
