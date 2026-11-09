using UiAtlas.Catalog;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Cli;

internal static class AtlasCatalogPresentation
{
    internal static CatalogApplicationInfo Read(AtlasMapWorkspace workspace)
    {
        var manifests = new List<(RecordingManifest Manifest, string Title)>(); var complete = true;
        foreach (var file in workspace.Index.BundleFiles)
        {
            try
            {
                using var bundle = RecordingBundle.Open(workspace.Resolve(file));
                var manifest = bundle.ReadJson<RecordingManifest>("manifest.json"); var title = "";
                var observations = bundle.Entries.Where(e => e.StartsWith("raw/observations/frame-", StringComparison.Ordinal)
                    && e.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
                foreach (var entry in observations.Take(5).Concat(observations.TakeLast(5)).Distinct())
                {
                    var frame = bundle.ReadJson<FrameObservation>(entry);
                    var candidates = (frame.ScopedWindows ?? []).Where(w => w.Hwnd == manifest.Target.SelectedHwnd || w.Hwnd == manifest.Target.RootOwnerHwnd
                        || (w.IsVisible && w.ProcessId == manifest.Target.ProcessId && manifest.Target.RootOwnerHwnd != 0 && w.RootOwnerHwnd == manifest.Target.RootOwnerHwnd))
                        .OrderBy(w => w.Hwnd == manifest.Target.SelectedHwnd ? 0 : 1).Select(w => w.Title);
                    if (frame.Window.Hwnd == manifest.Target.SelectedHwnd || frame.Window.Hwnd == manifest.Target.RootOwnerHwnd)
                        candidates = candidates.Append(frame.Window.Title);
                    title = candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ?? "";
                    if (title.Length > 0) break;
                }
                manifests.Add((manifest, title));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { complete = false; }
        }
        var latest = manifests.OrderByDescending(v => v.Manifest.EndedUtc).FirstOrDefault();
        return new(latest.Manifest?.Target.ProductName ?? "", latest.Manifest?.Target.ProcessName ?? workspace.Index.ApplicationName,
            latest.Manifest?.Target.ProductVersion ?? "", latest.Title ?? "",
            workspace.Index.Sources.Select(s => s.BundleId).Distinct(StringComparer.Ordinal).ToArray(),
            complete && latest.Manifest is not null ? latest.Manifest.EndedUtc : null);
    }
    internal static byte[]? Preview(AtlasMapWorkspace workspace)
    {
        var source = workspace.Index.Sources.LastOrDefault(s => !string.IsNullOrWhiteSpace(s.ScreenshotEntry));
        if (source is null) return null;
        var file = "recordings/" + source.BundleHash + ".mlrec";
        if (!workspace.Index.BundleFiles.Contains(file, StringComparer.Ordinal)) return null;
        using var bundle = RecordingBundle.Open(workspace.Resolve(file));
        return bundle.ReadBytes(source.ScreenshotEntry, 4 * 1024 * 1024);
    }
}
