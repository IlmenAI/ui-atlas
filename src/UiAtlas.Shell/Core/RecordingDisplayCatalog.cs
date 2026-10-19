using System.Drawing;
using FormsScreen = System.Windows.Forms.Screen;

namespace UIMaps.Application.Core;

internal sealed record RecordingDisplaySnapshot(
    string DeviceName,
    Rectangle Bounds,
    bool IsPrimary);

public sealed record RecordingDisplayScope(
    string ScopeId,
    string Label,
    string DeviceCode,
    Rectangle MonitoredBounds,
    Rectangle HudBounds,
    IReadOnlyList<Rectangle> MonitorBounds,
    IReadOnlyList<string> DeviceNames,
    bool IncludesAllDisplays)
{
    public bool IsCurrentDisplay { get; init; }
    public string HudMenuLabel => IsCurrentDisplay ? "This display" : Label;

    public bool Includes(Rectangle bounds) => MonitorBounds.Any(display =>
    {
        var intersection = Rectangle.Intersect(display, bounds);
        return intersection.Width >= 16 && intersection.Height >= 16;
    });

    public string PickerDescription => IncludesAllDisplays
        ? "Showing visible apps on all displays"
        : $"Showing visible apps on {Label.Replace(" (Recommended)", string.Empty, StringComparison.OrdinalIgnoreCase)}";
}

public interface IRecordingDisplayCatalog
{
    IReadOnlyList<RecordingDisplayScope> List(nint anchorWindowHandle);
}

public sealed class RecordingDisplayCatalog : IRecordingDisplayCatalog
{
    public IReadOnlyList<RecordingDisplayScope> List(nint anchorWindowHandle)
    {
        var screens = FormsScreen.AllScreens
            .Select(screen => new RecordingDisplaySnapshot(screen.DeviceName, screen.Bounds, screen.Primary))
            .ToArray();
        var anchor = anchorWindowHandle == 0 ? FormsScreen.PrimaryScreen : FormsScreen.FromHandle(anchorWindowHandle);
        return Project(screens, anchor?.DeviceName);
    }

    internal static IReadOnlyList<RecordingDisplayScope> Project(
        IEnumerable<RecordingDisplaySnapshot> values,
        string? anchorDeviceName)
    {
        var displays = values
            .Where(value => value.Bounds.Width > 0 && value.Bounds.Height > 0)
            .GroupBy(value => value.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (displays.Length == 0)
            return [];

        var primary = displays.FirstOrDefault(value => value.IsPrimary) ?? displays[0];
        var anchor = displays.FirstOrDefault(value => string.Equals(value.DeviceName, anchorDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? primary;
        var ordered = displays
            .OrderBy(value => value.IsPrimary ? 0 : PositionRank(value, primary))
            .ThenBy(value => value.Bounds.Left)
            .ThenBy(value => value.Bounds.Top)
            .ToArray();
        var options = ordered.Select((display, index) => new RecordingDisplayScope(
            display.DeviceName,
            $"{PositionLabel(display, primary)}{(display.IsPrimary ? " (Recommended)" : string.Empty)} · {ResolutionLabel(display.Bounds)}",
            DeviceCode(display.DeviceName, index + 1),
            display.Bounds,
            display.Bounds,
            [display.Bounds],
            [display.DeviceName],
            false) { IsCurrentDisplay = string.Equals(display.DeviceName, anchor.DeviceName, StringComparison.OrdinalIgnoreCase) }).ToList();

        if (displays.Length > 1)
        {
            var virtualBounds = displays.Select(value => value.Bounds).Aggregate(Rectangle.Union);
            options.Insert(0, new RecordingDisplayScope(
                "all",
                $"All displays [{virtualBounds.Left},{virtualBounds.Top},{virtualBounds.Width}×{virtualBounds.Height}]",
                "All",
                virtualBounds,
                anchor.Bounds,
                displays.Select(value => value.Bounds).ToArray(),
                displays.Select(value => value.DeviceName).ToArray(),
                true));
        }

        return options;
    }

    private static string PositionLabel(RecordingDisplaySnapshot display, RecordingDisplaySnapshot primary)
    {
        if (display.IsPrimary || string.Equals(display.DeviceName, primary.DeviceName, StringComparison.OrdinalIgnoreCase))
            return "Primary";
        var horizontal = CenterX(display.Bounds) - CenterX(primary.Bounds);
        var vertical = CenterY(display.Bounds) - CenterY(primary.Bounds);
        if (Math.Abs(horizontal) >= Math.Abs(vertical))
            return horizontal < 0 ? "Left" : "Right";
        return vertical < 0 ? "Upper" : "Lower";
    }

    private static int PositionRank(RecordingDisplaySnapshot display, RecordingDisplaySnapshot primary) =>
        PositionLabel(display, primary) switch
        {
            "Left" => 1,
            "Right" => 2,
            "Upper" => 3,
            "Lower" => 4,
            _ => 5,
        };

    private static string ResolutionLabel(Rectangle bounds) => bounds.Width >= bounds.Height
        ? $"{bounds.Height}p"
        : $"{bounds.Width} × {bounds.Height}";

    private static string DeviceCode(string deviceName, int fallbackIndex)
    {
        var marker = deviceName.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            var number = new string(deviceName[(marker + "DISPLAY".Length)..].TakeWhile(char.IsDigit).ToArray());
            if (!string.IsNullOrWhiteSpace(number))
                return $"D{number}";
        }
        return $"D{fallbackIndex}";
    }

    private static double CenterX(Rectangle bounds) => bounds.Left + (bounds.Width / 2d);
    private static double CenterY(Rectangle bounds) => bounds.Top + (bounds.Height / 2d);
}
