using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UiAtlas.Catalog;

namespace UiAtlas.Core.Cli;

// Presentation metadata belongs to the local Atlas catalog, outside immutable map revisions.
internal sealed class AtlasApplicationNames
{
    private readonly Dictionary<string, string> names;

    private AtlasApplicationNames(Dictionary<string, string> names) => this.names = names;

    public string Version => Hash(JsonSerializer.Serialize(names));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string FilePath(string root) => Path.Combine(root, "application-names.json");

    public static AtlasApplicationNames Read(string root)
    {
        try
        {
            using var stream = new FileStream(FilePath(root), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var names = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidDataException("The application names file is empty.");
            return new(names);
        }
        catch (FileNotFoundException) { return new(new()); }
        catch (DirectoryNotFoundException) { return new(new()); }
    }

    public CatalogMap[] Apply(IEnumerable<CatalogMap> maps) =>
        maps.Select(map => map with { FriendlyName = names.GetValueOrDefault(map.ApplicationGroup) }).ToArray();

    public static void Save(string root, string group, string? requested, string? expected)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var name = requested.Trim();
        if (name.Length > 100 || name.Any(char.IsControl))
            throw new ArgumentException("Use a single line of up to 100 characters for the application name.");
        var path = FilePath(root);
        using var mutex = new Mutex(false, "UiAtlas.AppNames." + Hash(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()));
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException("Another application name is being saved. Try again.");
        }
        catch (AbandonedMutexException) { /* The previous writer released ownership on exit. */ }
        try
        {
            var names = Read(root).names;
            var current = names.GetValueOrDefault(group);
            var next = name.Length == 0 ? null : name;
            if (current == next) return;
            if (current != expected) throw new IOException("The application name changed. Refresh the library before editing it again.");
            if (next == null) names.Remove(group);
            else names[group] = next;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(names));
                File.Move(temporary, path, true);
            }
            finally { File.Delete(temporary); }
        }
        finally { mutex.ReleaseMutex(); }
    }
}
