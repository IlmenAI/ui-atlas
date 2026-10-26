using System.IO;
using UiAtlas.Catalog;
namespace UIMaps.Application.Core;

public static class ApplicationMapIdentity
{
    public static string Name(string executable, string savedName, CatalogApplicationInfo? info)
    {
        var key = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        var known = key switch { "winword" => "Microsoft Word", "excel" => "Microsoft Excel", "powerpnt" => "Microsoft PowerPoint", "outlook" => "Microsoft Outlook", "msedge" => "Microsoft Edge", "chrome" => "Google Chrome", _ => null };
        if (known is not null) return known;
        var saved = !string.IsNullOrWhiteSpace(savedName) && !savedName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !savedName.StartsWith("atlas:", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(savedName, executable, StringComparison.OrdinalIgnoreCase) && !string.Equals(savedName, key, StringComparison.OrdinalIgnoreCase);
        if (saved) return savedName;
        if (key is "prowin32" or "prowin")
        {
            if (!string.IsNullOrWhiteSpace(info?.ProductName) && !info.ProductName.Contains("OpenEdge", StringComparison.OrdinalIgnoreCase)
                && !info.ProductName.Contains("Progress", StringComparison.OrdinalIgnoreCase)) return info.ProductName;
            return !string.IsNullOrWhiteSpace(info?.WindowTitle) && !info.WindowTitle.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? info.WindowTitle : "Progress OpenEdge application";
        }
        if (!string.IsNullOrWhiteSpace(info?.ProductName)) return info.ProductName;
        return !string.IsNullOrWhiteSpace(info?.WindowTitle) ? info.WindowTitle : "Unidentified application";
    }
    public static string Color(string executable) => Path.GetFileNameWithoutExtension(executable).ToLowerInvariant() switch
    { "excel" => "#107C41", "powerpnt" => "#C43E1C", "prowin32" or "prowin" => "#007E87", _ => "#126CC4" };
    public static string Date(DateTimeOffset? value) => value is null || value == DateTimeOffset.MinValue ? "Unavailable" : value.Value.ToLocalTime().ToString("MMM d, yyyy · h:mm tt");
}
