using System.IO;
using System.Text.Json;

namespace UiAtlas.Presentation;

/// <summary>Only immutable identity crosses dispatchers or process boundaries.</summary>
public sealed record AppearancePreference(string ThemeId = "maplock", string ColorMode = "light")
{
    public static IReadOnlyList<(string Id, string Label)> Themes { get; } = Array.AsReadOnly(new[]
    {
        ("plex", "Plex"), ("aero", "Longhorn Aero"), ("chatgpt", "ChatGPT-like"),
        ("maplock", "MapLock"), ("ilmen", "Ilmen"), ("fluent", "Fluent")
    });

    public AppearancePreference Validated() => new(
        Themes.Any(t => t.Id == ThemeId) ? ThemeId : "maplock",
        ColorMode is "light" or "dark" ? ColorMode : "light");
}

/// <summary>Shared per-user appearance preferences, independent of service credentials and business settings.</summary>
public sealed class AppearancePreferenceStore(string? path = null)
{
    public string Path { get; } = path ?? Environment.GetEnvironmentVariable("UIMAPS_APPEARANCE_SETTINGS")
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UIMaps", "appearance.json");
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

    public AppearancePreference Load()
    {
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var document = JsonSerializer.Deserialize<SavedPreference>(stream, Json);
            return document is { SchemaVersion: 1 } ? new AppearancePreference(document.ThemeId, document.ColorMode).Validated() : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(AppearancePreference preference)
    {
        var validated = preference.Validated();
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".appearance-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SavedPreference(1, validated.ThemeId, validated.ColorMode), Json));
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record SavedPreference(int SchemaVersion, string ThemeId, string ColorMode);
}
