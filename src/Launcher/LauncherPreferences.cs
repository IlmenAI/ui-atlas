using System.IO;
using System.Text.Json;
using UIMaps.Application.Core;

namespace UiAtlas.Launcher;

public sealed class LauncherPreferences(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UiAtlas", "launcher.json");
    private sealed record Value(string Workspace);
    public ExperienceProfileId Load()
    {
        try { return Parse(JsonSerializer.Deserialize<Value>(File.ReadAllText(_path))?.Workspace ?? "maps"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return ExperienceProfileId.Maps; }
    }
    public void Save(ExperienceProfileId workspace)
    {
        var name = workspace switch { ExperienceProfileId.Maps => "maps", ExperienceProfileId.Diagnostics => "diagnostics", _ => throw new ArgumentException("Select UI Maps or Diagnostics.") };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Value(name))); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static ExperienceProfileId? ParseWorkspace(string[] args) => args switch
    {
        [] => null,
        ["--workspace", var value] => Parse(value),
        _ => throw new ArgumentException("Usage: UiAtlas.exe [--workspace maps|diagnostics]")
    };
    private static ExperienceProfileId Parse(string value) => value.ToLowerInvariant() switch
    {
        "maps" => ExperienceProfileId.Maps,
        "diagnostics" => ExperienceProfileId.Diagnostics,
        _ => throw new ArgumentException("Workspace must be maps or diagnostics.")
    };
}
