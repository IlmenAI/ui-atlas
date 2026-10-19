using System.Text.Json;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Build;

public sealed record MapBuildSettings(int MaxNodes = 100_000, int MaxEdges = 100_000)
{
    public void Validate()
    {
        if (MaxNodes is < 1 or > GraphContractLimits.MaxNodes || MaxEdges is < 1 or > GraphContractLimits.MaxEdges)
            throw new InvalidDataException($"Node and edge limits must be between 1 and {GraphContractLimits.MaxNodes:N0}.");
    }

    public void CheckCounts(int nodes, int edges)
    {
        Validate();
        if (nodes > MaxNodes || edges > MaxEdges)
            throw new InvalidDataException($"UI Map needs {nodes:N0} nodes and {edges:N0} edges; build limits are {MaxNodes:N0} nodes and {MaxEdges:N0} edges. Increase them in Settings > UI Map building and build again. The previous map has been kept.");
    }
}

public sealed class MapBuildSettingsStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UiAtlas", "map-build-settings.json");
    public MapBuildSettings Load()
    {
        var value = File.Exists(_path) ? JsonSerializer.Deserialize<MapBuildSettings>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("Map build settings are empty.") : new();
        value.Validate();
        return value;
    }
    public void Save(MapBuildSettings value)
    {
        value.Validate();
        var destination = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
