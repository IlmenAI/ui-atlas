using System.IO;
using System.Windows;
using System.Windows.Controls;
using Google.Protobuf;
using Microsoft.Win32;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Mapping;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Desktop;

/// <summary>The Atlas editor's command surface for UI Maps review authority.</summary>
public sealed class MapReviewWindow : Window
{
    private readonly IMappingReviewStore _workspace;
    private readonly ComboBox _drafts = new() { MinWidth = 280, Margin = new Thickness(4), DisplayMemberPath = "MapDraftRef" };
    private readonly DataGrid _entities = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, Margin = new Thickness(8) };
    private readonly DataGrid _interactions = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, Margin = new Thickness(8) };
    private readonly TabControl _tabs = new();
    private readonly TextBox _value = new() { MinWidth = 220, Margin = new Thickness(4), ToolTip = "New label or parent entity reference" };
    private readonly TextBox _reviewer = new() { Text = Environment.UserName, MinWidth = 130, Margin = new Thickness(4) };
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(8), MinHeight = 160 };
    private readonly TextBlock _status = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    private UiMapDraft? Current => _drafts.SelectedItem as UiMapDraft;

    public MapReviewWindow(string mapPath)
    {
        _workspace = Directory.Exists(mapPath) ? new EpisodeMappingReviewStore(mapPath) : AtlasMapWorkspace.Open(mapPath);
        Title = "UI Atlas — Review and publish map"; Width = 1180; Height = 780; MinWidth = 850; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel();
        var heading = new TextBlock { Text = "Review the observed application map", FontSize = 22, Margin = new Thickness(12) };
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var actions = new WrapPanel { Margin = new Thickness(8) };
        actions.Children.Add(_drafts);
        actions.Children.Add(new TextBlock { Text = "Reviewer", VerticalAlignment = VerticalAlignment.Center });
        actions.Children.Add(_reviewer);
        Add(actions, "Accept", () => Review(ReviewCommandKind.ReviewAcceptEntity));
        Add(actions, "Reject", () => Review(ReviewCommandKind.ReviewRejectEntity));
        actions.Children.Add(_value);
        Add(actions, "Rename", () => Review(ReviewCommandKind.ReviewRenameEntity));
        Add(actions, "Set parent", () => Review(ReviewCommandKind.ReviewSetParent));
        Add(actions, "Undo", () => Review(ReviewCommandKind.ReviewUndo));
        Add(actions, "Redo", () => Review(ReviewCommandKind.ReviewRedo));
        Add(actions, "Review all…", () =>
        {
            if (MessageBox.Show(this, "Accept every blocking item in this draft, including observed interactions and unresolved dispositions? Inspect the evidence before accepting.",
                "Review all items", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Review(ReviewCommandKind.ReviewAcceptAllBlocking);
        });
        Add(actions, "Validate", () => _details.Text = string.Join("\n\n", _workspace.Validate().Select(value => JsonFormatter.Default.Format(value))));
        Add(actions, "Publish revision", Publish);
        Add(actions, "Export revision…", ExportRevision);
        Add(actions, "Source evidence…", () => new MappingEvidenceWindow(mapPath) { Owner = this }.ShowDialog());
        DockPanel.SetDock(actions, Dock.Top); layout.Children.Add(actions);
        DockPanel.SetDock(_status, Dock.Bottom); layout.Children.Add(_status);
        foreach (var (label, path, width) in new[] { ("Name", "DisplayName", 240), ("Kind", "EntityKind", 165), ("Review", "ReviewState", 150), ("Role", "Role", 140), ("Entity reference", "MapEntityRef", 380) })
            _entities.Columns.Add(new DataGridTextColumn { Header = label, Binding = new System.Windows.Data.Binding(path), Width = width });
        foreach (var (label, path, width) in new[] { ("Sequence", "SequenceNumber", 75), ("Gesture", "Kind", 205), ("Result", "Outcome", 240), ("Review", "ReviewState", 150), ("Interaction reference", "InteractionRef", 380) })
            _interactions.Columns.Add(new DataGridTextColumn { Header = label, Binding = new System.Windows.Data.Binding(path), Width = width });
        _tabs.Items.Add(new TabItem { Header = "Map entities", Content = _entities });
        _tabs.Items.Add(new TabItem { Header = "Observed interactions", Content = _interactions });
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 120 });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 100 });
        body.Children.Add(_tabs);
        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(splitter, 1); body.Children.Add(splitter);
        Grid.SetRow(_details, 2); body.Children.Add(_details);
        layout.Children.Add(body); Content = layout;
        _drafts.SelectionChanged += (_, _) => ShowDraft();
        _entities.SelectionChanged += (_, _) =>
        {
            if (_entities.SelectedItem is MapEntity entity) { _value.Text = entity.DisplayName; _details.Text = JsonFormatter.Default.Format(entity); }
        };
        _interactions.SelectionChanged += (_, _) =>
        {
            if (_interactions.SelectedItem is ObservedMapInteraction interaction) _details.Text = JsonFormatter.Default.Format(interaction);
        };
        Refresh();
    }
    private void Add(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(10, 6, 10, 6) };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UIMaps.Map.Core.UiMapException)
            { _status.Text = ex.Message; MessageBox.Show(this, ex.Message, "Map review", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        panel.Children.Add(button);
    }
    private void ShowDraft()
    {
        if (Current is not { } draft) return;
        _entities.ItemsSource = draft.Entities;
        _interactions.ItemsSource = draft.Interactions;
        var blocking = draft.ReviewItems.Count(item => item.Blocking && item.ReviewState == ReviewState.ReviewUnreviewed);
        _status.Text = $"{draft.Entities.Count} entities · {blocking} blocking review items · {draft.Sources.Count} observations. A map records knowledge; publishing does not execute actions.";
        _details.Text = string.Join("\n\n", draft.ReviewItems.Where(item => item.ReviewState == ReviewState.ReviewUnreviewed)
            .Select(value => $"{value.Category}: {value.Message}\nEntity: {value.SubjectRef}"));
    }
    private void Refresh()
    {
        var selected = Current?.MapDraftRef;
        var values = _workspace.Drafts;
        _drafts.ItemsSource = values;
        _drafts.SelectedItem = values.FirstOrDefault(draft => draft.MapDraftRef == selected) ?? values.FirstOrDefault();
    }
    private void Review(ReviewCommandKind kind)
    {
        if (Current is not { } draft) return;
        var entity = _entities.SelectedItem as MapEntity;
        var interaction = _tabs.SelectedIndex == 1 ? _interactions.SelectedItem as ObservedMapInteraction : null;
        if (_tabs.SelectedIndex == 1 && interaction is null && kind is ReviewCommandKind.ReviewAcceptEntity or ReviewCommandKind.ReviewRejectEntity)
            throw new InvalidOperationException("Select an observed interaction first.");
        if (interaction is not null && kind is ReviewCommandKind.ReviewAcceptEntity or ReviewCommandKind.ReviewRejectEntity)
        {
            _workspace.Review(new ReviewCommand { RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = draft.MapDraftRef,
                ExpectedEtag = draft.Etag, SubjectRef = interaction.InteractionRef,
                CommandKind = kind == ReviewCommandKind.ReviewAcceptEntity ? ReviewCommandKind.ReviewAcceptInteraction : ReviewCommandKind.ReviewRejectInteraction,
                ReviewedBy = _reviewer.Text, Reason = "Explicit Atlas observed-interaction review." });
            Refresh(); return;
        }
        if (_tabs.SelectedIndex == 1 && kind is ReviewCommandKind.ReviewRenameEntity or ReviewCommandKind.ReviewSetParent)
            throw new InvalidOperationException("Select the Map entities tab to edit a name or parent.");
        if (entity is null && kind is not (ReviewCommandKind.ReviewAcceptAllBlocking or ReviewCommandKind.ReviewUndo or ReviewCommandKind.ReviewRedo))
            throw new InvalidOperationException("Select an entity first.");
        _workspace.Review(new ReviewCommand { RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = draft.MapDraftRef,
            ExpectedEtag = draft.Etag, CommandKind = kind, SubjectRef = entity?.MapEntityRef ?? "", Value = _value.Text,
            SecondaryRef = kind == ReviewCommandKind.ReviewSetParent ? _value.Text : "", ReviewedBy = _reviewer.Text,
            Reason = "Explicit Atlas editor review." });
        Refresh();
    }
    private void Publish()
    {
        var revisions = _workspace.Publish(_reviewer.Text);
        _details.Text = string.Join("\n\n", revisions.Select(value => JsonFormatter.Default.Format(value)));
        _status.Text = $"Published {revisions.Count} immutable revision(s).";
    }
    private void ExportRevision()
    {
        if (Current is not { } draft) return;
        var session = _workspace.Service.GetSession(draft.MappingSessionRef);
        if (string.IsNullOrEmpty(session.PublishedMapRevisionRef)) throw new InvalidOperationException("Review and publish this draft before exporting a revision.");
        var dialog = new SaveFileDialog { Filter = "UI Maps revision (*.uimap)|*.uimap", FileName = "application.uimap", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        var package = _workspace.Service.Export(session.PublishedMapRevisionRef, false);
        File.WriteAllBytes(dialog.FileName, package.PackageBytes.ToByteArray());
        _status.Text = "Revision exported. Screenshots and raw provider records are excluded; map labels and source references remain.";
    }
}
