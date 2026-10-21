using System.Text.Json;

namespace UiAtlas.Catalog;

public sealed record MapReadinessSettings(bool RequireReview = true);

/// <summary>One readiness preference for the library shared by both launchers.</summary>
public sealed class MapReadinessSettingsStore(string? root = null)
{
    private readonly string _path = Path.Combine(AtlasCatalogLocation.Resolve(root), "map-readiness-settings.json");

    public MapReadinessSettings Load() => File.Exists(_path)
        ? JsonSerializer.Deserialize<MapReadinessSettings>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("Map readiness settings are empty.")
        : new();

    public void Save(MapReadinessSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
