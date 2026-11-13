using System.Windows;
using System.Windows.Controls;
using UIMaps.Protocol.V3;
using UIMaps.Map.Core;

namespace UiAtlas.Core.Desktop;

public sealed class GridSchemaHistoryWindow : Window
{
    public GridSchemaHistoryWindow(UiMapDraft draft, MapEntity grid, Func<string, Task> select)
    {
        // Work on a view copy: opening history never mutates the map.
        draft = draft.Clone(); grid = draft.Entities.Single(e => e.MapEntityRef == grid.MapEntityRef);
        GridSchemaHistory.EnsureBaseline(draft, grid);
        Title = "Schema history · " + grid.DisplayName; Width = 740; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var header = new TextBlock { Text = grid.DisplayName + "\nDefault selection: " +
            (grid.HostedDataGrid.SchemaDefaultPinned ? "Pinned version" : "Automatic") +
            "\nChanges update this draft. Mark Ready to use them in new tasks. Existing tasks keep their saved version.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) }; bottom.Children.Add(notice);
        var actions = new WrapPanel(); bottom.Children.Add(actions);
        var automatic = new Button { Content = "Automatic", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 0) };
        var pin = new Button { Content = "Use this version by default", Padding = new Thickness(12, 8, 12, 8) };
        actions.Children.Add(automatic); actions.Children.Add(pin);
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });
        layout.RowDefinitions.Add(new RowDefinition()); root.Children.Add(layout);
        var versions = new ListBox { DisplayMemberPath = "Label", Margin = new Thickness(0, 0, 0, 12) }; layout.Children.Add(versions);
        var preview = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(preview, 1); layout.Children.Add(preview);
        var choices = grid.HostedDataGrid.SchemaVersions.Select((v, i) => new VersionChoice(v,
            $"Version {i + 1}{(v.SchemaVersionRef == grid.HostedDataGrid.DefaultSchemaVersionRef ? " · Default" : "")} · " +
            $"{v.Snapshot.ExpectedHeaders.Count(h => !string.IsNullOrWhiteSpace(h))}/{v.Snapshot.ExpectedHeaders.Count} named columns · " +
            (DateTimeOffset.TryParse(v.ObservedAt, out var captured) ? captured.ToLocalTime().ToString("g") : "Earlier capture"))).ToArray();
        versions.ItemsSource = choices;
        versions.SelectionChanged += (_, _) =>
        {
            if (versions.SelectedItem is not VersionChoice choice) return;
            preview.Text = choice.Version.SelectionReason + "\n\n" + string.Join("\n", choice.Version.Snapshot.ExpectedHeaders.Select((h, i) =>
                $"{i + 1}. {(string.IsNullOrWhiteSpace(h) ? "(unnamed column)" : h)}")) +
                $"\n\nSupported by {choice.Version.SourceObservationSetRefs.Count} retained observations.";
        };
        versions.SelectedItem = choices.Single(c => c.Version.SchemaVersionRef == grid.HostedDataGrid.DefaultSchemaVersionRef);
        automatic.Click += (_, _) => Apply("automatic");
        pin.Click += (_, _) => { if (versions.SelectedItem is VersionChoice choice) Apply(choice.Version.SchemaVersionRef); };
        async void Apply(string value)
        {
            automatic.IsEnabled = pin.IsEnabled = versions.IsEnabled = false;
            notice.Text = "Saving the schema default and updating the map…";
            try { await select(value); DialogResult = true; }
            catch (Exception error) { notice.Text = error.Message; }
            finally { automatic.IsEnabled = pin.IsEnabled = versions.IsEnabled = true; }
        }
    }
    private sealed record VersionChoice(HostedGridSchemaVersion Version, string Label)
    {
        public override string ToString() => Label;
    }
}
