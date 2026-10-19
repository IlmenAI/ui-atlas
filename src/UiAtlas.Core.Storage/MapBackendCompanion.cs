using System.Text.Json;
using System.Text.Json.Nodes;

namespace UiAtlas.Core.Storage;

/// <summary>Filesystem lifecycle of an optional UI Maps authority next to a legacy-compatible projection.</summary>
public static class MapBackendCompanion
{
    public static string PathFor(string mapPath) => Path.GetFullPath(mapPath) + ".uimaps";
    public static void Copy(string sourceMap, string targetMap, string? logicalMapId = null)
    {
        var source = PathFor(sourceMap); var target = PathFor(targetMap);
        if (!Directory.Exists(source)) return;
        if (Directory.Exists(target)) throw new IOException("The destination already has a mapping authority.");
        RejectLinks(source);
        using var lease = new FileStream(Path.Combine(source, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Directory.CreateDirectory(target);
        try
        {
            var generation = JsonSerializer.Deserialize<string>(File.ReadAllText(Path.Combine(source, "active.json")));
            if (!Guid.TryParseExact(generation, "N", out _)) throw new InvalidDataException("Invalid map generation.");
            var sourceGeneration = Path.Combine(source, "generations", generation);
            var targetGeneration = Path.Combine(target, "generations", generation);
            CopyTree(sourceGeneration, targetGeneration);
            if (logicalMapId is not null)
            {
                var indexPath = Path.Combine(targetGeneration, "atlas-map.json");
                var index = JsonNode.Parse(File.ReadAllText(indexPath)) ?? throw new InvalidDataException("Invalid map index.");
                index["LogicalMapId"] = logicalMapId;
                File.WriteAllText(indexPath, index.ToJsonString());
            }
            File.Copy(Path.Combine(source, "active.json"), Path.Combine(target, "active.json"));
        }
        catch { Directory.Delete(target, true); throw; }
    }
    public static void Move(string sourceMap, string targetMap)
    {
        var source = PathFor(sourceMap);
        if (!Directory.Exists(source)) return;
        RejectLinks(source);
        using (new FileStream(Path.Combine(source, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
        Directory.Move(source, PathFor(targetMap));
    }
    public static bool Delete(string mapPath, string allowedDirectory)
    {
        var path = PathFor(mapPath);
        if (!Directory.Exists(path)) return false;
        var allowed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedDirectory)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Map authority escaped the catalog.");
        RejectLinks(path);
        using (new FileStream(Path.Combine(path, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
        Directory.Delete(path, true);
        return true;
    }
    public static void RejectLinks(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Map authorities cannot contain links.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Map authorities cannot contain links.");
            if (Directory.Exists(entry)) RejectLinks(entry);
        }
    }
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyTree(entry, destination); else File.Copy(entry, destination);
        }
    }
}
