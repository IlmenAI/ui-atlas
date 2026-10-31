using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UiAtlas.Presentation;

namespace UiAtlas.Core.Cli;

internal sealed partial class GridSchemaReviewWindow
{
    private readonly ScrollViewer _preview;
    private readonly ComboBox _zoom = new() { ItemsSource = new[] { "Fit", "100%", "200%", "Custom" }, SelectedIndex = 0, Width = 85 };
    private readonly TextBlock _zoomPercent = AtlasFluent.Text("100%", 12, AtlasFluent.Muted);
    private readonly TextBlock _columnContext = AtlasFluent.Text("Select a column below to locate it in the image.", 12, AtlasFluent.Muted);
    private readonly CheckBox _autoColumnZoom = new() { Content = "Zoom to selected column", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Rectangle _columnHighlight = new() { IsHitTestVisible = false, Stroke = MapperGridInspector.Brush("#0067C0"),
        Fill = new SolidColorBrush(Color.FromArgb(42, 22, 131, 255)), Visibility = Visibility.Collapsed };
    private bool _updatingPreviewZoom, _navigatingPreview;
    private int _zoomMode;
    private ViewportState? _beforeColumnFocus;
    private Point? _panOrigin;
    private double _panX, _panY;
    private sealed record ViewportState(double Scale, double X, double Y, int Mode);

    internal double PreviewZoom => _previewScale.ScaleX;
    internal Rect? SelectedColumnBounds => _columnHighlight.Visibility == Visibility.Visible
        ? new(Canvas.GetLeft(_columnHighlight), Canvas.GetTop(_columnHighlight), _columnHighlight.Width, _columnHighlight.Height) : null;

    private FrameworkElement BuildPreviewNavigation()
    {
        var controls = new WrapPanel { Margin = new(0, 0, 0, 4) };
        var label = AtlasFluent.Text("Preview zoom", 12); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new(0, 0, 8, 0); controls.Children.Add(label);
        var minus = Button("−", () => ZoomPreview(1 / 1.25, new(_preview.ViewportWidth / 2, _preview.ViewportHeight / 2)), false);
        var plus = Button("+", () => ZoomPreview(1.25, new(_preview.ViewportWidth / 2, _preview.ViewportHeight / 2)), false);
        AutomationProperties.SetName(minus, "Zoom out preview"); AutomationProperties.SetName(plus, "Zoom in preview");
        foreach (var button in new[] { minus, plus }) { button.MinWidth = 32; button.MinHeight = 30; button.Padding = new(8, 2, 8, 2); button.Margin = new(0, 0, 5, 0); }
        controls.Children.Add(minus); controls.Children.Add(plus); controls.Children.Add(_zoom);
        _zoomPercent.Margin = new(8, 0, 12, 0); _zoomPercent.VerticalAlignment = VerticalAlignment.Center; controls.Children.Add(_zoomPercent);
        AutomationProperties.SetName(_zoom, "Header preview zoom"); AutomationProperties.SetName(_zoomPercent, "Current preview zoom");
        _autoColumnZoom.Margin = new(0, 0, 12, 0); controls.Children.Add(_autoColumnZoom);
        var reset = Button("Clear column focus", ClearColumnFocus, false); reset.MinHeight = 30; reset.Padding = new(9, 3, 9, 3);
        reset.ToolTip = "Deselect the column and restore the previous zoom and scroll position."; controls.Children.Add(reset);
        var panel = new StackPanel(); panel.Children.Add(controls);
        panel.Children.Add(_columnContext);
        panel.Children.Add(AtlasFluent.Text("Ctrl + wheel or pinch to zoom · middle mouse or Space + drag to pan · Esc clears column focus", 11, AtlasFluent.Muted));
        Panel.SetZIndex(_columnHighlight, 1); _canvas.Children.Add(_columnHighlight);
        _canvas.HorizontalAlignment = HorizontalAlignment.Left; _canvas.VerticalAlignment = VerticalAlignment.Top;
        return panel;
    }

    private void ConfigurePreviewNavigation()
    {
        _zoom.SelectionChanged += (_, _) =>
        {
            if (_updatingPreviewZoom || _zoom.SelectedIndex < 0) return;
            if (_zoom.SelectedIndex == 3) { _zoomMode = -1; return; }
            _zoomMode = _zoom.SelectedIndex;
            if (_zoomMode == 0) FitPreview(); else SetPreviewScale(_zoomMode, new(_preview.ViewportWidth / 2, _preview.ViewportHeight / 2));
        };
        _preview.SizeChanged += (_, _) => { if (_zoomMode == 0 && !_updatingPreviewZoom) FitPreview(); };
        Loaded += (_, _) => FitPreview();
        _autoColumnZoom.Checked += (_, _) => FocusSelectedColumn();
        _autoColumnZoom.Unchecked += (_, _) => { RestoreBeforeColumnFocus(); RevealSelectedColumn(); };
        _preview.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            ZoomPreview(Math.Pow(1.25, e.Delta / 120d), e.GetPosition(_preview)); e.Handled = true;
        };
        _preview.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle && !(e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space))) return;
            EndPreviewInteraction(); _navigatingPreview = true; _panOrigin = e.GetPosition(_preview);
            _panX = _preview.HorizontalOffset; _panY = _preview.VerticalOffset; _preview.CaptureMouse(); e.Handled = true;
        };
        _preview.PreviewMouseMove += (_, e) =>
        {
            if (_panOrigin is not { } start) return;
            var point = e.GetPosition(_preview); _preview.ScrollToHorizontalOffset(_panX - point.X + start.X);
            _preview.ScrollToVerticalOffset(_panY - point.Y + start.Y); e.Handled = true;
        };
        _preview.PreviewMouseUp += (_, e) =>
        {
            if (_panOrigin is null) return;
            _panOrigin = null; _navigatingPreview = false; _preview.ReleaseMouseCapture(); e.Handled = true;
        };
        _preview.LostMouseCapture += (_, _) => { _panOrigin = null; _navigatingPreview = false; };
        _preview.IsManipulationEnabled = true;
        _preview.ManipulationStarting += (_, e) =>
        {
            EndPreviewInteraction(); _navigatingPreview = true; e.ManipulationContainer = _preview;
            e.Mode = ManipulationModes.Scale | ManipulationModes.Translate; e.Handled = true;
        };
        _preview.ManipulationDelta += (_, e) =>
        {
            NavigatePreviewGesture(e.DeltaManipulation.Scale.X, e.DeltaManipulation.Translation, e.ManipulationOrigin); e.Handled = true;
        };
        _preview.ManipulationCompleted += (_, e) => { _navigatingPreview = false; e.Handled = true; };
        _preview.PreviewTouchDown += (_, _) => EndPreviewInteraction();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && SelectedColumnBounds is not null && !IsEditingName() && _editDividers?.IsChecked != true)
            { ClearColumnFocus(); e.Handled = true; return; }
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || IsEditingName()) return;
            if (e.Key is Key.Add or Key.OemPlus) ZoomPreview(1.25, new(_preview.ViewportWidth / 2, _preview.ViewportHeight / 2));
            else if (e.Key is Key.Subtract or Key.OemMinus) ZoomPreview(1 / 1.25, new(_preview.ViewportWidth / 2, _preview.ViewportHeight / 2));
            else if (e.Key is Key.D0 or Key.NumPad0) { _zoomMode = 0; FitPreview(); }
            else return;
            e.Handled = true;
        };
    }

    private void FitPreview()
    {
        if (_updatingPreviewZoom || _preview.ViewportWidth <= 0 || _preview.ViewportHeight <= 0) return;
        var fit = Math.Min(1, Math.Min(_preview.ViewportWidth / _imageWidth, _preview.ViewportHeight / _imageHeight));
        ApplyPreviewView(fit, 0, 0, 0);
    }

    internal void ZoomPreview(double factor, Point anchor)
    {
        if (!double.IsFinite(factor) || factor <= 0 || !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y)) return;
        EndPreviewInteraction(); _zoomMode = -1;
        SetPreviewScale(_previewScale.ScaleX * factor, anchor);
    }

    internal void NavigatePreviewGesture(double factor, Vector translation, Point anchor)
    {
        if (!double.IsFinite(translation.X) || !double.IsFinite(translation.Y)) return;
        EndPreviewInteraction();
        ZoomPreview(factor, anchor);
        _preview.ScrollToHorizontalOffset(_preview.HorizontalOffset - translation.X);
        _preview.ScrollToVerticalOffset(_preview.VerticalOffset - translation.Y);
    }

    private void SetPreviewScale(double scale, Point anchor)
    {
        var old = Math.Max(.0001, _previewScale.ScaleX); scale = Math.Clamp(scale, .05, 8);
        var x = (_preview.HorizontalOffset + anchor.X) / old;
        var y = (_preview.VerticalOffset + anchor.Y) / old;
        ApplyPreviewView(scale, x * scale - anchor.X, y * scale - anchor.Y, _zoomMode);
    }

    private void ApplyPreviewView(double scale, double x, double y, int mode)
    {
        if (_closed) return;
        _updatingPreviewZoom = true;
        try
        {
            _zoomMode = mode; _previewScale.ScaleX = _previewScale.ScaleY = scale;
            _zoom.SelectedIndex = mode >= 0 ? mode : 3; _zoomPercent.Text = $"{scale:P0}";
            _preview.UpdateLayout(); _preview.ScrollToHorizontalOffset(Math.Max(0, x)); _preview.ScrollToVerticalOffset(Math.Max(0, y));
            DrawSelection(); UpdateColumnOutline();
        }
        finally { _updatingPreviewZoom = false; }
    }

    private bool IsEditingName() => _columns?.CurrentItem is not null && Keyboard.FocusedElement is TextBox;

    private void UpdateSelectedColumn()
    {
        if (Draft is null || _columns.SelectedItem is not ColumnRow row || _columns.SelectedIndex >= Draft.Schema.Columns.Count)
        {
            _columnHighlight.Visibility = Visibility.Collapsed; _columnContext.Text = "Select a column below to locate it in the image.";
            RestoreBeforeColumnFocus(); return;
        }
        _beforeColumnFocus ??= new(_previewScale.ScaleX, _preview.HorizontalOffset, _preview.VerticalOffset, _zoomMode);
        var bounds = ColumnImageBounds(_columns.SelectedIndex);
        if (bounds is null) { _columnHighlight.Visibility = Visibility.Collapsed; return; }
        Canvas.SetLeft(_columnHighlight, bounds.Value.X); Canvas.SetTop(_columnHighlight, bounds.Value.Y);
        _columnHighlight.Width = bounds.Value.Width; _columnHighlight.Height = _imageHeight - bounds.Value.Y;
        _columnHighlight.Visibility = Visibility.Visible;
        _columnContext.Text = $"Column {row.Number} of {Draft.Schema.Columns.Count}: {(string.IsNullOrWhiteSpace(row.Label) ? "(blank header)" : row.Label)}";
        _columns.ScrollIntoView(row);
        UpdateColumnOutline();
        if (_autoColumnZoom.IsChecked == true) FocusSelectedColumn(); else RevealSelectedColumn();
    }

    private void UpdateColumnOutline() => _columnHighlight.StrokeThickness = 2 / Math.Max(.0001, _previewScale.ScaleX);

    private Rect? ColumnImageBounds(int index)
    {
        if (Draft is null || index < 0 || index >= Draft.Schema.Columns.Count) return null;
        var reading = Draft.HeaderReadings.FirstOrDefault(r => r.ColumnKey == Draft.Schema.Columns[index].ColumnKey);
        return reading is null ? null : new Rect(reading.Bounds.X, reading.Bounds.Y, reading.Bounds.Width, reading.Bounds.Height);
    }

    internal void SelectColumnAt(Point point)
    {
        if (IsBusy || Draft is null || point.Y < HeaderSelection.Y || point.Y > _imageHeight) return;
        var index = Enumerable.Range(0, Draft.Schema.Columns.Count).FirstOrDefault(i =>
            ColumnImageBounds(i) is { } b && point.X >= b.Left && point.X < b.Right, -1);
        if (index < 0 || index == _columns.SelectedIndex) { ClearColumnFocus(); return; }
        _columns.SelectedIndex = index; _columns.ScrollIntoView(_columns.SelectedItem);
    }

    private void FocusSelectedColumn()
    {
        if (SelectedColumnBounds is not { } bounds || _preview.ViewportWidth <= 0 || _preview.ViewportHeight <= 0) return;
        var sampleHeight = Math.Min(bounds.Height, Math.Max(HeaderSelection.Height + 40, 72));
        var scale = Math.Clamp(Math.Min(_preview.ViewportWidth / (bounds.Width + 40), _preview.ViewportHeight / (sampleHeight + 24)), .25, 4);
        ApplyPreviewView(scale, (bounds.X + bounds.Width / 2) * scale - _preview.ViewportWidth / 2, bounds.Y * scale - 12, -1);
    }

    private void RevealSelectedColumn()
    {
        if (SelectedColumnBounds is not { } bounds) return;
        _preview.ScrollToHorizontalOffset((bounds.X + bounds.Width / 2) * _previewScale.ScaleX - _preview.ViewportWidth / 2);
        _preview.ScrollToVerticalOffset(bounds.Y * _previewScale.ScaleY);
    }

    private void RestoreBeforeColumnFocus()
    {
        if (_beforeColumnFocus is not { } original) return;
        _beforeColumnFocus = null; ApplyPreviewView(original.Scale, original.X, original.Y, original.Mode);
    }

    internal void ClearColumnFocus()
    {
        if (_columns is not null) _columns.SelectedItem = null;
        _columnHighlight.Visibility = Visibility.Collapsed; RestoreBeforeColumnFocus();
        _columnContext.Text = "Select a column below to locate it in the image.";
    }
}
