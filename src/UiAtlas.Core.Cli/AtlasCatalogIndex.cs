using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UiAtlas.Catalog;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Cli;

/// <summary>A disposable display index shared by all Atlas catalog clients. Map workspaces
/// remain authoritative; their generation and database/WAL stamps invalidate cached rows.</summary>
internal static class AtlasCatalogIndex
{
    internal const string CacheFileName = "map-list-cache-v1.json";
    internal sealed record CachedMap(string Id, string Stamp, CatalogMap[] Maps);
    internal sealed record Index(int Version, CachedMap[] Entries);

    internal static CatalogReply Read(LocalArtifactCatalog catalog, string? knownVersion, CancellationToken token)
    {
        var paths = catalog.ListMapPaths().Order(StringComparer.Ordinal).ToArray();
        var stamps = paths.Select(path => (Path: path, Stamp: Stamp(path))).ToArray();
        var version = Version(catalog.Root, stamps);
        token.ThrowIfCancellationRequested();
        if (knownVersion == version)
            return new() { Root = catalog.Root, CatalogVersion = version, NotModified = true };

        var cached = ReadCache(catalog);
        var entries = new List<CachedMap>();
        var changed = cached.Length != paths.Length;
        foreach (var item in stamps)
        {
            token.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(item.Path);
            var prior = cached.FirstOrDefault(entry => entry.Id == id && entry.Stamp == item.Stamp
                && entry.Maps.All(map => map.Id == id && map.Path == item.Path && map.SummaryOnly));
            if (prior is not null) { entries.Add(prior); continue; }
            changed = true;
            var before = item.Stamp;
            for (var attempt = 0; ; attempt++)
            {
                var maps = AtlasCatalogCommand.LoadMap(catalog, id, true, token);
                var after = Stamp(item.Path);
                if (before == after) { entries.Add(new(id, after, maps)); break; }
                if (attempt >= 2) throw new IOException("The map changed while loading. Refresh Maps and retry.");
                before = after;
            }
        }
        if (changed) WriteCache(catalog, new(1, entries.ToArray()));
        // Use the stamps associated with the actual rows, not newer state observed after reading them.
        version = Version(catalog.Root, entries.Select(entry => (Path: catalog.MapPath(entry.Id), entry.Stamp)));
        return new() { Root = catalog.Root, CatalogVersion = version, Maps = entries.SelectMany(entry => entry.Maps).ToArray() };
    }

    private static string Version(string root, IEnumerable<(string Path, string Stamp)> values) =>
        Hash("map-list-v1\n" + root + "\n" + string.Join("\n", values.Select(value => value.Path + "|" + value.Stamp)));

    private static string Stamp(string path)
    {
        var value = new StringBuilder();
        AddFile(value, path);
        AddFile(value, path + "-wal");
        AddFile(value, path + "-shm");
        var root = AtlasMapWorkspace.RootFor(path);
        if (Directory.Exists(root)) MapBackendCompanion.RejectLinks(root);
        var pointer = Path.Combine(root, "active.json");
        if (File.Exists(pointer))
        {
            if (new FileInfo(pointer).Length > 1024) throw new InvalidDataException("Map generation pointer exceeds its size limit.");
            var generation = JsonSerializer.Deserialize<string>(File.ReadAllText(pointer));
            if (!Guid.TryParseExact(generation, "N", out _)) throw new InvalidDataException("Invalid Map generation.");
            value.Append('|').Append(generation);
            var directory = Path.Combine(root, "generations", generation);
            AddFile(value, Path.Combine(directory, "atlas-map.json"));
            var database = Path.Combine(directory, "map", "map.db");
            AddFile(value, database);
            AddFile(value, database + "-wal");
            // SQLite shared-memory is a transient reader coordination file, not map content.
        }
        return Hash(value.ToString());
    }

    private static void AddFile(StringBuilder value, string path)
    {
        var file = new FileInfo(path);
        value.Append('|').Append(path);
        if (!file.Exists) { value.Append(":missing"); return; }
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Catalog files cannot be links.");
        value.Append(':').Append(file.Length).Append(':').Append(file.LastWriteTimeUtc.Ticks).Append(':').Append(file.CreationTimeUtc.Ticks);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static CachedMap[] ReadCache(LocalArtifactCatalog catalog)
    {
        var path = Path.Combine(catalog.Root, CacheFileName);
        if (!File.Exists(path)) return [];
        RejectCacheLink(path);
        if (new FileInfo(path).Length > 64 * 1024 * 1024) return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var cache = JsonSerializer.Deserialize<Index>(stream);
            return cache is { Version: 1, Entries: not null } && cache.Entries.All(entry => entry is not null && entry.Maps is not null
                && entry.Maps.All(map => map is not null)) ? cache.Entries : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static void WriteCache(LocalArtifactCatalog catalog, Index index)
    {
        var path = Path.Combine(catalog.Root, CacheFileName);
        RejectCacheLink(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, index);
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A read-only or temporarily locked cache must not prevent listing the authoritative maps.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectCacheLink(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The catalog display cache cannot be a link.");
    }
}
