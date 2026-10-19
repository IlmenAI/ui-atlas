using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Google.Protobuf;
using UiAtlas.Core.Mapping.Atlas;
using UIMaps.Protocol.V1;

namespace UiAtlas.Core.Desktop;

/// <summary>Inspect retained pixels and source packets alongside Perception's
/// publication. Large textual previews are capped; stored evidence is unchanged.</summary>
public sealed class MappingEvidenceWindow : Window
{
    private sealed record Source(string Label, string Episode, string Publication);
    public MappingEvidenceWindow(string mapPath)
    {
        Title = "UI Atlas — Mapping source evidence"; Width = 1120; Height = 760;
        MinWidth = 700; MinHeight = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Source[] sources;
        if (Directory.Exists(mapPath)) sources = [new("Native sensor episode", Path.Combine(mapPath, "episode.pb"), Path.Combine(mapPath, "perception.pb"))];
        else
        {
            var workspace = AtlasMapWorkspace.Open(mapPath);
            sources = workspace.Index.Sources.Select(source => new Source($"{source.BundleId} · frame {source.Frame}",
                workspace.Resolve(source.EpisodeFile), workspace.Resolve(source.PublicationFile))).ToArray();
        }
        var layout = new DockPanel();
        var selector = new ComboBox { ItemsSource = sources, DisplayMemberPath = "Label", Margin = new Thickness(10) };
        DockPanel.SetDock(selector, Dock.Top); layout.Children.Add(selector);
        var notice = new TextBlock { Text = "Raw Data Streams retain recorder packets and provider provenance. Raw World is the Perception publication. Semantic World contains Map candidates and their review state. The original evidence remains in the workspace.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10) };
        DockPanel.SetDock(notice, Dock.Bottom); layout.Children.Add(notice);
        var tabs = new TabControl();
        var pixels = new Image { Stretch = Stretch.Uniform };
        var raw = PreviewBox(); var world = PreviewBox();
        tabs.Items.Add(new TabItem { Header = "Retained pixels", Content = new ScrollViewer { Content = pixels } });
        tabs.Items.Add(new TabItem { Header = "Sensor packets", Content = raw });
        tabs.Items.Add(new TabItem { Header = "Perception publication", Content = world });
        if (!Directory.Exists(mapPath))
        {
            var workspace = AtlasMapWorkspace.Open(mapPath);
            var grids = new ComboBox { ItemsSource = workspace.Index.GridReviews ?? [], DisplayMemberPath = "Name", Margin = new Thickness(8) };
            var gridPixels = new Image { Stretch = Stretch.Uniform };
            var gridDetails = PreviewBox();
            var gridLayout = new DockPanel();
            DockPanel.SetDock(grids, Dock.Top); gridLayout.Children.Add(grids);
            gridDetails.Height = 170; DockPanel.SetDock(gridDetails, Dock.Bottom); gridLayout.Children.Add(gridDetails);
            gridLayout.Children.Add(new ScrollViewer { Content = gridPixels });
            tabs.Items.Add(new TabItem { Header = "Reviewed grids", Content = gridLayout });
            grids.SelectionChanged += (_, _) =>
            {
                if (grids.SelectedItem is not AtlasGridReview grid) return;
                try
                {
                    using var stream = File.OpenRead(workspace.Resolve(grid.ImageFile));
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); gridPixels.Source = bitmap;
                    var path = workspace.Resolve(grid.ReviewFile);
                    if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Grid review exceeds the preview limit.");
                    var json = File.ReadAllText(path);
                    gridDetails.Text = grid.Status + ": " + grid.Detail + "\n\n" +
                        (json.Length <= 256_000 ? json : json[..256_000] + "\n[Preview truncated; complete source is retained on disk.]");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
                { gridDetails.Text = ex.Message; gridPixels.Source = null; }
            };
            grids.SelectedIndex = 0;
        }
        layout.Children.Add(tabs); Content = layout;
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is not Source source) return;
            try
            {
                if (new FileInfo(source.Episode).Length > CaptureSizeLimits.MaximumEpisodeBytes || new FileInfo(source.Publication).Length > CaptureSizeLimits.MaximumEpisodeBytes)
                    throw new InvalidDataException("Evidence exceeds the preview size limit.");
                var episode = SensorEpisodeBundle.Parser.ParseFrom(File.ReadAllBytes(source.Episode));
                var screenshot = episode.SensorPackets.FirstOrDefault(packet => packet.Screenshot is not null)?.Screenshot;
                pixels.Source = null;
                if (screenshot is not null)
                {
                    using var stream = new MemoryStream(screenshot.ImageBlob.ToByteArray());
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); pixels.Source = bitmap;
                }
                // Avoid base64 pixel dumps in the text preview.
                foreach (var packet in episode.SensorPackets.Where(packet => packet.Screenshot is not null)) packet.Screenshot.ImageBlob = ByteString.Empty;
                raw.Text = Preview(episode);
                world.Text = Preview(GetPublicationResponse.Parser.ParseFrom(File.ReadAllBytes(source.Publication)));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
            { raw.Text = ex.Message; world.Text = "Evidence could not be rendered."; }
        };
        selector.SelectedIndex = 0;
    }
    private static TextBox PreviewBox() => new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(8) };
    private static string Preview(IMessage message)
    {
        var value = JsonFormatter.Default.Format(message);
        return value.Length <= 256_000 ? value : value[..256_000] + "\n[Preview truncated; complete source is retained on disk.]";
    }
}
