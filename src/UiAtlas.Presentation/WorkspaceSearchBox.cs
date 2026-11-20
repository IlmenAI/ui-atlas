using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace UiAtlas.Presentation;

public sealed record WorkspaceSearchItem(string Name, string Context, Action Open)
{
    public string Display => string.IsNullOrEmpty(Context) ? Name : Name + "   ·   " + Context;
}

public sealed class WorkspaceSearchBox : Grid
{
    private readonly TextBox _query = new() { Padding = new(31, 0, 55, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListBox _results = new() { DisplayMemberPath = "Display", MaxHeight = 280, BorderThickness = new(0) };
    private readonly TextBlock _empty = new() { Text = "No matching pages or business units", Margin = new(12) };
    private readonly Popup _popup = new() { Placement = PlacementMode.Bottom, AllowsTransparency = true, StaysOpen = true, Focusable = false };

    public WorkspaceSearchBox()
    {
        MaxWidth = 365; Height = 39; HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(_query, "Search workspace");
        Children.Add(_query);
        Children.Add(new WorkspaceIcon { Kind = "search", IsHitTestVisible = false, Width = 15, Height = 15, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(10, 0, 0, 0) });
        var hint = new TextBlock { Text = "Search workspace", VerticalAlignment = VerticalAlignment.Center, Margin = new(32, 0, 0, 0), IsHitTestVisible = false };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedInkBrush"); Children.Add(hint);
        var shortcut = new TextBlock { Text = "Ctrl K", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 0, 10, 0), IsHitTestVisible = false };
        shortcut.SetResourceReference(TextBlock.ForegroundProperty, "MutedInkBrush"); Children.Add(shortcut);
        var panel = new Grid(); panel.Children.Add(_results); panel.Children.Add(_empty);
        var frame = new Border { BorderThickness = new(1), CornerRadius = new(6), Padding = new(4), Child = panel };
        frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); frame.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _results.SetResourceReference(Control.BackgroundProperty, "PanelBrush"); _results.SetResourceReference(Control.ForegroundProperty, "InkBrush");
        _popup.Child = frame; _popup.PlacementTarget = this;
        _query.TextChanged += (_, _) => { hint.Visibility = _query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Refresh(); };
        _query.GotKeyboardFocus += (_, _) => Refresh();
        Window? owner = null;
        void Dismiss(object? sender, EventArgs e) => _popup.IsOpen = false;
        void ClickAway(object sender, MouseButtonEventArgs e)
        {
            if (!IsMouseOver && !_popup.IsMouseOver) _popup.IsOpen = false;
        }
        Loaded += (_, _) =>
        {
            owner = Window.GetWindow(this);
            if (owner is null) return;
            owner.Deactivated += Dismiss;
            owner.PreviewMouseDown += ClickAway;
        };
        _query.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!_query.IsKeyboardFocused && !_results.IsKeyboardFocusWithin) _popup.IsOpen = false; }));
        _query.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _popup.IsOpen = false; e.Handled = true; }
            if (_popup.IsOpen && e.Key is (Key.Down or Key.Up) && _results.Items.Count > 0)
            {
                _results.SelectedIndex = Math.Clamp(_results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _results.Items.Count - 1);
                _results.ScrollIntoView(_results.SelectedItem); e.Handled = true;
            }
            if (e.Key == Key.Enter && _popup.IsOpen) { OpenSelected(); e.Handled = true; }
        };
        _results.PreviewMouseLeftButtonUp += (_, e) => { if (ItemsControl.ContainerFromElement(_results, e.OriginalSource as DependencyObject) is ListBoxItem { Content: WorkspaceSearchItem hit }) { _popup.IsOpen = false; e.Handled = true; hit.Open(); } };
        _results.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; } };
        Unloaded += (_, _) =>
        {
            _popup.IsOpen = false;
            if (owner is null) return;
            owner.Deactivated -= Dismiss; owner.PreviewMouseDown -= ClickAway; owner = null;
        };
        void Refresh()
        {
            if (!_query.IsKeyboardFocused || !IsVisible || TryFindResource("WorkspaceSearchSource") is not Func<IEnumerable<WorkspaceSearchItem>> source) return;
            string term = _query.Text.Trim();
            if (term.Length == 0) { _popup.IsOpen = false; return; }
            var items = source().Where(i => i.Display.Contains(term, StringComparison.OrdinalIgnoreCase)).Take(60).ToList();
            _results.ItemsSource = items; _results.SelectedIndex = items.Count == 0 ? -1 : 0;
            _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            frame.Width = Math.Max(ActualWidth, 250); _popup.IsOpen = true;
        }
        void OpenSelected()
        {
            if (_results.SelectedItem is not WorkspaceSearchItem item) return;
            _popup.IsOpen = false; item.Open();
        }
    }
}
