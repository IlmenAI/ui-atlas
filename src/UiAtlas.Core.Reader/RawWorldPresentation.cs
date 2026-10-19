using System.Globalization;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Reader;

/// <summary>Presentation of published scene facts, with no inference in the viewer.</summary>
public static class RawWorldPresentation
{
    public static long? WindowHandle(GraphNode? source)
    {
        if (source is null) return null;
        var value = UiMappingReadModel.Property(source, "windowHandleEvidence") ?? UiMappingReadModel.Property(source, "hwnd");
        var hex = value?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true;
        return long.TryParse(hex ? value![2..] : value, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var hwnd) && hwnd != 0 ? hwnd : null;
    }

    public static bool IsAvailableOnSurface(UiMapControlView control, string surfaceId) =>
        control.OwnerSurfaceId == surfaceId || control.Level == UiUnderstandingLevel.RawWorld &&
        control.Source.Properties.Any(property => property.Name == "scenePresentedOn" && property.Value == surfaceId);

    public static IReadOnlyList<RectI> VisibleRegions(UiMapControlView control)
    {
        if (control.Level is not (UiUnderstandingLevel.RawWorld or UiUnderstandingLevel.SemanticWorld) ||
            !control.Source.Properties.Any(property => property.Name == "sceneReconstruction")) return [control.Bounds];
        var result = new List<RectI>();
        foreach (var property in control.Source.Properties.Where(property => property.Name == "sceneVisibleRegion"))
        {
            var parts = property.Value.Split(',');
            if (parts.Length != 4) continue;
            var numbers = new int[4];
            if (!parts.Select((part, index) => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[index])).All(value => value)) continue;
            var rect = new RectI(numbers[0], numbers[1], numbers[2], numbers[3]);
            if (ControlGeometry.IsRenderable(rect)) result.Add(rect);
        }
        return result;
    }
}
