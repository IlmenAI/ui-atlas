using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;
using static UiAtlas.Core.Tests.MapperOverlayWindowTests;

namespace UiAtlas.Core.Tests;

[Collection("Mapper desktop")]
public sealed class GridExplorerNativeWindowTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false, true)]
    public void ExploreOpaqueGridTraversesAndRestoresWithoutReadingText(bool stop, bool zeroPage, bool discover = false)
    {
        Sta(() =>
        {
            var window = new Window { Title = "Opaque table exploration fixture", Left = 80, Top = 550,
                // The native child uses physical pixel geometry. It must fit even
                // on a 100% display, rather than relying on the primary display's DPI.
                Width = 640, Height = 360, Background = Brushes.White, Topmost = true };
            NativeDesktopFixture.Place(window);
            RecordingHighlightOverlay? overlay = null;
            try
            {
                window.Show(); window.Activate(); Pump(100);
                var hwnd = new WindowInteropHelper(window).Handle;
                using var grid = new NativeGridFixture(hwnd, zeroPage);
                Pump(100);
                var activation = WindowActivation.ActivateAsync(hwnd, CancellationToken.None);
                Await(() => activation.IsCompleted);
                Assert.True(activation.GetAwaiter().GetResult());
                window.Topmost = false; window.Topmost = true; Pump(100);
                var target = WindowCatalog.Resolve(hwnd.ToInt64());
                var host = WindowCatalog.Resolve(grid.Handle.ToInt64());
                var control = MapperHighlightTests.Control("Opaque table", "Table", host.Bounds, "UiAtlas.Visual.Ocr", ["Grid"])
                    with { WindowHwnd = host.Hwnd };
                var frame = new FrameObservation(1, DateTimeOffset.UtcNow, "", WindowSnapshotCapture.Observe(target), [control], false, "ok", "synthetic-live");
                byte[]? retainedPixels = null;
                if (discover)
                {
                    control = control with { RuntimeId = "opaque-pane", ControlType = "ControlType.Pane", ClassName = host.ClassName,
                        FrameworkId = "Win32", SupportedPatterns = ["ScrollPatternIdentifiers.Pattern"],
                        WindowHwnd = target.Hwnd, NativeWindowHwnd = host.Hwnd };
                    // Use physical pixels with their exact origin, as native retained evidence does.
                    // The legacy composite capture stretches WGC's visible frame to GetWindowRect.
                    var captureBounds = WindowSnapshotCapture.VisibleFrameBounds(target);
                    using var bitmap = new System.Drawing.Bitmap(captureBounds.Width, captureBounds.Height);
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                        graphics.CopyFromScreen(captureBounds.X, captureBounds.Y, 0, 0, bitmap.Size);
                    using var png = new System.IO.MemoryStream();
                    bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                    retainedPixels = png.ToArray();
                    frame = frame with { Automation = [control], ScreenshotBounds = captureBounds,
                        Window = frame.Window with { Bounds = captureBounds }, NativeEpisodeEntry = "raw/native/fixture.pb" };
                    Assert.DoesNotContain(MapperHighlightModel.Build(target.Bounds, frame.Automation), g => g.IsGrid);
                    if (Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR") is { Length: > 0 } evidence)
                    {
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(evidence, "discovery-source.png"), retainedPixels);
                        System.IO.File.WriteAllText(System.IO.Path.Combine(evidence, "discovery-frame.json"),
                            System.Text.Json.JsonSerializer.Serialize(frame));
                    }
                    Assert.Single(MapperGridDiscovery.Discover(frame, frame.Window, frame.Automation, retainedPixels));
                }
                overlay = new RecordingHighlightOverlay(target) { RecordedFramePixels = _ => retainedPixels };
                overlay.Start(); overlay.ReplaceMapperHighlights(frame.Window.Bounds, frame);
                Await(() => overlay.GridHitWindows.Count > 0);
                var dispatcher = overlay.GridHitWindows.First().Dispatcher;
                dispatcher.Invoke(() => overlay.InspectGrid(overlay.MapperHighlights.Single(g => g.IsGrid)));
                if (discover) dispatcher.Invoke(() =>
                {
                    Assert.Equal(MapperHighlightKind.GridCandidate, overlay.GridInspector!.Selection.Kind);
                    Assert.Same(control, overlay.GridInspector.Selection.Control);
                    Assert.Equal("ControlType.Pane", frame.Automation[0].ControlType);
                });
                var inspector = overlay.GridInspector!;
                var handoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var stopped = false;
                if (stop) grid.AfterHorizontalMove = () =>
                {
                    if (stopped) return;
                    stopped = true;
                    dispatcher.Invoke(() => Descendants<Button>(inspector).Single(b => Equals(b.Content, "Stop"))
                        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)));
                };
                if (zeroPage) overlay.ExplorationRunner = async (action, token) =>
                { await handoff.Task.WaitAsync(token); return await action(token); };
                dispatcher.Invoke(() => Descendants<Button>(inspector).Single(b => Equals(b.Content, "Explore"))
                    .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)));
                if (zeroPage)
                {
                    // A passive refresh can complete while the recorder has not yet
                    // handed control to Explore. It must not close the pending inspector.
                    overlay.ReplaceMapperHighlights(target.Bounds, frame with { Automation = [] });
                    var drained = dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Await(() => drained.Task.IsCompleted);
                    dispatcher.Invoke(() => Assert.Same(inspector, overlay.GridInspector));
                    handoff.SetResult();
                }
                var deadline = DateTime.UtcNow.AddSeconds(75);
                while (dispatcher.Invoke(() => inspector.IsExploring) && DateTime.UtcNow < deadline)
                {
                    Pump(20);
                }
                var result = dispatcher.Invoke(() => inspector.Result);
                Assert.True(result is not null, dispatcher.Invoke(() => inspector.Error));
                Assert.Equal(GridCaptureScope.VisibleRowBand, result.Capture.Scope);
                Assert.True(result.Capture.Status == (stop ? GridCaptureStatus.Partial : GridCaptureStatus.Complete), string.Join(';', result.Reasons));
                if (stop) { Assert.True(stopped); Assert.Contains("cancelled", result.Reasons); }
                else
                {
                    Assert.True(result.Capture.Coverage.HorizontalMovementObserved);
                    Assert.True(result.Capture.Coverage.RightBoundary);
                    Assert.False(result.Capture.Coverage.VerticalMovementObserved);
                    Assert.All(result.Capture.Movements, movement => Assert.Equal(GridScrollAxis.Horizontal, movement.Axis));
                    Assert.True(result.Capture.Coverage.ColumnCoverageComplete);
                    Assert.Equal(zeroPage, result.Capture.Coverage.RowCoverageComplete);
                    if (zeroPage)
                    {
                        Assert.True(grid.MaximumXSeen > 800);
                        Assert.True(result.TableBounds.Width > 1300);
                    }
                }
                Assert.True(result.Capture.Restoration.Status == GridRestorationStatus.Succeeded, result.Capture.Restoration.Reason);
                Assert.Equal(0, grid.X); Assert.Equal(0, grid.Y);
                Assert.NotNull(result.ImagePath);
                Assert.False(result.Capture.Schema.IsComplete); // Image success does not publish a data schema.
                var qa = Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR");
                if (!string.IsNullOrWhiteSpace(qa)) dispatcher.Invoke(() => Render((FrameworkElement)inspector.Content,
                    System.IO.Path.Combine(qa, stop ? "stopped-result.png" : "opaque-result.png")));
            }
            finally { overlay?.Dispose(); window.Close(); }
        });
    }

    private sealed class NativeGridFixture : IDisposable
    {
        private readonly HwndSource _source;
        private readonly DrawingVisual _visual = new();
        public nint Handle => _source.Handle;
        public int X { get; private set; }
        public int Y { get; private set; }
        public int MaximumXSeen { get; private set; }
        public Action? AfterHorizontalMove { get; set; }
        private int _width, _height;
        private readonly bool _zeroPage;
        private readonly int _contentWidth;
        public NativeGridFixture(nint parent, bool zeroPage)
        {
            _zeroPage = zeroPage;
            _contentWidth = zeroPage ? 1420 : 620;
            var parameters = new HwndSourceParameters("Opaque grid fixture") { ParentWindow = parent,
                PositionX = 30, PositionY = 40, Width = 530, Height = 190,
                WindowStyle = unchecked((int)(zeroPage ? 0x50100000 : 0x50300000)) }; // visible child with native scrollbars
            _source = new(parameters) { RootVisual = _visual };
            _source.AddHook(Hook);
            GetClientRect(Handle, out var bounds); _width = bounds.Right; _height = bounds.Bottom;
            SetAxis(0, _contentWidth, _width, X);
            SetAxis(1, zeroPage ? _height - 24 : 216, _height - 24, Y);
            Draw();
        }
        private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message is not (0x114 or 0x115)) return 0;
            var horizontal = message == 0x114;
            var command = (int)(wParam.ToInt64() & 0xffff);
            var old = horizontal ? X : Y;
            var step = horizontal && _zeroPage ? 128 : 64;
            var value = command == 4 ? (int)(wParam.ToInt64() >> 16) : old + (command == 0 ? -step : step);
            if (horizontal && _zeroPage && command == 4) value = (int)Math.Round(value * (_contentWidth - _width) / 127d);
            value = Math.Clamp(value, 0, horizontal ? _contentWidth - _width : _zeroPage ? 0 : 216 - (_height - 24));
            if (horizontal) X = value; else Y = value;
            MaximumXSeen = Math.Max(MaximumXSeen, X);
            SetAxis(horizontal ? 0 : 1, horizontal ? _contentWidth : _zeroPage ? _height - 24 : 216, horizontal ? _width : _height - 24, value);
            Draw();
            if (horizontal && value != old) AfterHorizontalMove?.Invoke();
            handled = true; return 0;
        }
        private void SetAxis(int bar, int total, int page, int position)
        {
            var info = new ScrollInfo { Size = (uint)Marshal.SizeOf<ScrollInfo>(), Mask = 0x17,
                Minimum = 0, Maximum = total - 1, Page = (uint)page, Position = position };
            if (bar == 0 && _zeroPage)
            { info.Maximum = 127; info.Page = 0; info.Position = (int)Math.Round(position * 127d / (total - page)); }
            SetScrollInfo(Handle, bar, ref info, true);
        }
        private void Draw()
        {
            using var dc = _visual.RenderOpen();
            var scale = _source.CompositionTarget.TransformToDevice;
            dc.PushTransform(new ScaleTransform(1 / scale.M11, 1 / scale.M22));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, _width, _height)));
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, _width, _height));
            dc.PushClip(new RectangleGeometry(new Rect(0, 24, _width, _height - 24)));
            for (var row = 0; row < 9; row++)
            {
                var y = 24 + row * 24 - Y;
                for (var column = 0; column < (_zeroPage ? 9 : 4); column++)
                {
                    var x = column * 160 - X;
                    dc.DrawRectangle(null, new Pen(Brushes.Gray, 1), new Rect(x + .5, y + .5, 160, 24));
                    Text($"Item {row * 37 + column * 13:D3}", x + 6, y + 4);
                }
            }
            dc.Pop();
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, _width, 24));
            for (var column = 0; column < (_zeroPage ? 9 : 4); column++)
            {
                var x = column * 160 - X;
                dc.DrawRectangle(null, new Pen(Brushes.Gray, 1), new Rect(x + .5, .5, 160, 23));
                // This fixture exercises successful traversal. Distinct field names
                // avoid the near-identical glyphs of Header 1/2/3/4 at 100% scaling;
                // repeated-content rejection has its own registration tests.
                string[] headers = ["Reference", "Product description", "Stock quantity", "Warehouse", "Status", "Supplier", "Unit price", "Location", "Last received"];
                Text(headers[column], x + 6, 4);
            }
            dc.Pop(); dc.Pop();
            void Text(string text, int x, int y) => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.Black, 1), new Point(x, y));
        }
        public void Dispose() => _source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct ScrollInfo
    { public uint Size, Mask; public int Minimum, Maximum; public uint Page; public int Position, TrackPosition; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern int SetScrollInfo(nint hwnd, int bar, ref ScrollInfo info, bool redraw);
}
