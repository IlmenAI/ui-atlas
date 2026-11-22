using System.IO;
using System.Text.Json;

namespace UiAtlas.Presentation;

/// <summary>One per-user capture budget shared by the standalone and embedded Atlas tools.</summary>
public sealed record TableCaptureDuration(int Minutes = 10)
{
    public const int MaximumMinutes = 1440;
    public int ToMilliseconds() => Minutes is >= 1 and <= MaximumMinutes ? Minutes * 60_000
        : throw new ArgumentOutOfRangeException(nameof(Minutes), $"Enter a whole number from 1 to {MaximumMinutes} minutes.");
}

public sealed class TableCaptureDurationStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UiAtlas", "table-capture-duration.json");

    public TableCaptureDuration Load()
    {
        if (!File.Exists(_path)) return new();
        var value = JsonSerializer.Deserialize<TableCaptureDuration>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("The table capture duration could not be read.");
        value.ToMilliseconds();
        return value;
    }

    public void Save(TableCaptureDuration value)
    {
        value.ToMilliseconds();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
