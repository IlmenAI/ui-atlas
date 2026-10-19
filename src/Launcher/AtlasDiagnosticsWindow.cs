using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Google.Protobuf;
using UiAtlas.Core.Recording.Windows;
using UIMaps.Application.Core;

namespace UiAtlas.Launcher;

/// <summary>Read-only native capture diagnostics, using the same isolated provider as Atlas recording.</summary>
public sealed class AtlasDiagnosticsWindow : Window
{
    private readonly RecordableApplicationTarget _target;
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12 };
    private readonly Image _image = new() { Stretch = System.Windows.Media.Stretch.Uniform };
    private readonly TextBlock _status = new() { Text = "Capture a read-only snapshot to inspect provider health, window topology, and accessibility evidence.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) };
    private readonly Button _capture = new() { Content = "Capture snapshot", Padding = new Thickness(16, 8, 16, 8) };
    private CancellationTokenSource? _captureJob;

    public AtlasDiagnosticsWindow(RecordableApplicationTarget target)
    {
        _target = target; Title = "UI Atlas Diagnostics - " + target.DisplayName; Width = 1160; Height = 780; MinWidth = 700; MinHeight = 450;
        var root = new DockPanel { Margin = new Thickness(24) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = target.WindowTitle, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock { Text = $"{target.ExecutableName} · Process {target.ProcessId} · Read-only diagnostics", Margin = new Thickness(0, 7, 0, 12) });
        var actions = new WrapPanel(); actions.Children.Add(_capture);
        var cancel = new Button { Content = "Cancel capture", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 8, 16, 8) };
        cancel.Click += (_, _) => _captureJob?.Cancel(); actions.Children.Add(cancel); header.Children.Add(actions); header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "Provider evidence", Content = _details }); tabs.Items.Add(new TabItem { Header = "Screenshot", Content = _image }); root.Children.Add(tabs); Content = root;
        _capture.Click += async (_, _) => await CaptureAsync();
        Closed += (_, _) => _captureJob?.Cancel();
    }
    private async Task CaptureAsync()
    {
        if (_captureJob is not null) return;
        _captureJob = new(); _capture.IsEnabled = false;
        try
        {
            var target = WindowCatalog.Resolve((nint)_target.WindowHandle);
            if (target.ProcessId != _target.ProcessId || target.ProcessStartedUtc != _target.ProcessStartedUtc)
                throw new InvalidOperationException("The application instance changed. Reopen Diagnostics from the application list.");
            var recorder = new NativeRecorderCapture(Path.Combine(AtlasCapabilityLauncher.ToolsDirectory, "native-recorder", "UIMaps.Recorder.ProviderProbe.dll"));
            var episode = await recorder.CaptureAsync(target, _captureJob.Token, message => Dispatcher.InvokeAsync(() => _status.Text = message));
            var screenshot = episode.SensorPackets.FirstOrDefault(packet => packet.Screenshot?.ImageBlob.Length > 0)?.Screenshot;
            if (screenshot is not null)
            {
                using var stream = new MemoryStream(screenshot.ImageBlob.ToByteArray());
                _image.Source = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            var evidence = episode.Clone();
            foreach (var packet in evidence.SensorPackets) if (packet.Screenshot is not null) packet.Screenshot.ImageBlob = ByteString.Empty;
            foreach (var window in evidence.WindowImages) window.Png = ByteString.Empty;
            using var json = System.Text.Json.JsonDocument.Parse(JsonFormatter.Default.Format(evidence));
            _details.Text = System.Text.Json.JsonSerializer.Serialize(json.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            _status.Text = $"Snapshot captured: {episode.Health?.State} · {episode.SensorPackets.Count} provider packets. Image bytes are omitted from this text preview. No map was changed.";
        }
        catch (OperationCanceledException) { _status.Text = "Capture cancelled."; }
        catch (Exception ex) { _status.Text = "Capture failed: " + ex.Message; }
        finally { _captureJob.Dispose(); _captureJob = null; _capture.IsEnabled = true; }
    }
}
