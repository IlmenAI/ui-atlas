using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Reader;

namespace UiAtlas.Core.Desktop;

public sealed partial class ExplorerWindow
{
    private readonly ComboBox _inspectionVariantSelector = new()
    { MinWidth = 100, MaxWidth = 170, DisplayMemberPath = nameof(UiInspectionFrameGroup.Label), Margin = new(3, 0, 8, 0) };
    private bool _updatingInspectionVariants;
    private TabControl? _inspectionTabs;
    private readonly ComboBox _inspectionGridSelector = new()
    { DisplayMemberPath = nameof(InspectionGridChoice.Label), Margin = new(0, 6, 0, 8) };
    private readonly TextBlock _inspectionGridSummary = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 7) };
    private readonly TextBlock _inspectionGridEvidence = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Image _inspectionGridImage = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
    private readonly DataGrid _inspectionGridColumns = new()
    { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column, Margin = new(0, 8, 0, 0) };
    private IReadOnlyList<ParameterGridSource> _inspectionGridSources = [];
    private bool _updatingInspectionGrids;
    private string? _inspectionScopeKey;
    private string? _inspectionRenderedGridKey;
    private bool _inspectionGridFit = true;
    private ScrollViewer? _inspectionImageScroll;
    private string? _inspectionUnavailableVariant;

    private UIElement BuildInspectionPane(UIElement pipeline)
    {
        var panel = new DockPanel { Margin = new(12) };
        var header = new StackPanel();
        header.Children.Add(Text("Data grids", 14, FontWeights.SemiBold, Ink));
        header.Children.Add(_inspectionGridSelector);
        header.Children.Add(_inspectionGridSummary);
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 6) };
        foreach (var label in new[] { "Fit grid", "Actual pixels" })
        {
            var button = new Button { Content = label, Margin = new(0, 0, 5, 0), Padding = new(8, 3, 8, 3) };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => { _inspectionGridFit = label == "Fit grid"; SizeInspectionGrid(); };
            zoom.Children.Add(button);
        }
        header.Children.Add(zoom);
        header.Children.Add(new Expander { Header = "Saved capture and schema evidence", Content = _inspectionGridEvidence });
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        var previews = new Grid();
        previews.RowDefinitions.Add(new() { Height = new(3, GridUnitType.Star), MinHeight = 90 });
        previews.RowDefinitions.Add(new() { Height = new(2, GridUnitType.Star), MinHeight = 90 });
        var imageScroll = new ScrollViewer { Content = _inspectionGridImage,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _inspectionImageScroll = imageScroll;
        imageScroll.SizeChanged += (_, _) => SizeInspectionGrid();
        previews.Children.Add(imageScroll);
        _inspectionGridColumns.Columns.Add(new DataGridTextColumn { Header = "Order", Binding = new System.Windows.Data.Binding(nameof(GridColumn.Ordinal)) });
        _inspectionGridColumns.Columns.Add(new DataGridTextColumn { Header = "Recorded header", Binding = new System.Windows.Data.Binding(nameof(GridColumn.Label)), Width = new(1, DataGridLengthUnitType.Star) });
        _inspectionGridColumns.Columns.Add(new DataGridTextColumn { Header = "Physical column", Binding = new System.Windows.Data.Binding(nameof(GridColumn.ColumnKey)) });
        Grid.SetRow(_inspectionGridColumns, 1); previews.Children.Add(_inspectionGridColumns);
        panel.Children.Add(previews);
        _inspectionTabs = new TabControl { BorderThickness = new(0), Background = Brushes.White };
        _inspectionTabs.Items.Add(new TabItem { Header = "Data grids", Content = panel });
        _inspectionTabs.Items.Add(new TabItem { Header = "Pipeline", Content = pipeline });
        _inspectionTabs.SelectedIndex = 0;
        AutomationProperties.SetName(_inspectionTabs, "Surface inspection previews");
        AutomationProperties.SetName(_inspectionGridSelector, "Retained grids on selected surface");
        AutomationProperties.SetName(_inspectionGridImage, "Retained data grid image");
        AutomationProperties.SetName(_inspectionGridColumns, "Retained physical grid columns");
        _inspectionGridSelector.SelectionChanged += (_, _) => { if (!_updatingInspectionGrids) RenderInspectionGrid(); };
        return CardBorder(_inspectionTabs);
    }

    private void RefreshInspectionPreview()
    {
        if (_inspectionTabs is null) return;
        _appMapTitle.Text = _selectedSurface?.DisplayName ?? "Control preview";
        _appMapTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _appMapTitle.ToolTip = _appMapTitle.Text;
        var scope = $"{_graphLoadVersion}|{_parameterReviewMode}|{_inspectionLevel}|{_selectedSurface?.Id}";
        if (_inspectionScopeKey != scope)
        {
            _inspectionScopeKey = scope;
            _inspectionTabs.SelectedIndex = _parameterReviewMode is not null ||
                _inspectionLevel == UiUnderstandingLevel.RawDataStreams ? 1 : 0;
        }
        if (_parameterReviewMode is not null) return;
        var selectedId = (_inspectionGridSelector.SelectedItem as InspectionGridChoice)?.Proposal.Id;
        var grids = _model is null ? [] : UiInspectionPresentation.GridsForSurface(_model, _selectedSurface, _parameterProposals);
        var choices = grids.Select(grid => new InspectionGridChoice(grid)).ToArray();
        _updatingInspectionGrids = true;
        _inspectionGridSelector.ItemsSource = choices;
        _inspectionGridSelector.SelectedItem = choices.FirstOrDefault(choice => choice.Proposal.Id == selectedId) ?? choices.FirstOrDefault();
        _inspectionGridSelector.IsEnabled = grids.Count > 0;
        _updatingInspectionGrids = false;
        RenderInspectionGrid();
    }

    private void RenderInspectionGrid()
    {
        if (_inspectionGridSelector.SelectedItem is not InspectionGridChoice choice)
        {
            _inspectionRenderedGridKey = null; _inspectionGridImage.Source = null; _inspectionGridColumns.ItemsSource = null;
            _inspectionGridEvidence.Text = "";
            var unbound = _parameterProposals.Count(p => p.IsGrid && !p.HostBound);
            _inspectionGridSummary.Text = _parameterError is not null ? "Retained grids unavailable: " + _parameterError
                : _inspectionLevel == UiUnderstandingLevel.RawDataStreams ? "Inspect a Raw World or Semantic World surface to see its retained grids."
                : "No retained grid is bound to this surface." + (unbound > 0 ? $" {unbound} grid(s) have unresolved hosts; inspect them in Data grids review." : "");
            return;
        }
        var proposal = choice.Proposal;
        var key = proposal.Id + "|" + proposal.Fingerprint + "|" + proposal.ImagePath + "|" + proposal.ImageSha256;
        if (_inspectionRenderedGridKey == key) return;
        _inspectionRenderedGridKey = key;
        _inspectionGridFit = true;
        _inspectionGridImage.Source = null;
        _inspectionGridColumns.ItemsSource = proposal.Schema?.Columns.OrderBy(c => c.Ordinal).ToArray();
        var source = _inspectionGridSources.FirstOrDefault(grid => "grid:" + grid.Id == proposal.Id);
        _inspectionGridSummary.Text = $"{proposal.Schema?.Columns.Count ?? 0} physical columns · " +
            (proposal.Schema?.IsComplete == true ? "complete mapped width" : "partial or unknown schema") +
            "\nSaved mapping capture; separate from the selected frame and live runtime data. Cell accuracy remains unverified.";
        _inspectionGridEvidence.Text = $"Grid: {proposal.DisplayName}\nHost: {proposal.TargetId}\nSchema: {proposal.Schema?.Revision ?? "unavailable"}\n" +
            (source is null ? "Capture identity unavailable.\n" : $"Saved: {source.Review.SavedUtc:O}\nAcquisition: {source.Review.Review.AcquisitionId}\n" +
                $"Source frame: {source.Review.Context.SourceBundleId ?? "unknown"} / {source.Review.Context.SourceFrame?.ToString() ?? "unknown"}\n") +
            $"{proposal.Coverage}\nImage SHA-256: {proposal.ImageSha256 ?? "unavailable"}\n" +
            string.Join("\n", proposal.Evidence.Select(e => e.Summary));
        try
        {
            using var stream = new MemoryStream(AtlasParameterizationAdapter.ReadGridImage(proposal));
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            _inspectionGridImage.Source = bitmap;
            SizeInspectionGrid();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        { _inspectionGridSummary.Text += "\nRetained image unavailable: " + ex.Message; }
    }

    private void SizeInspectionGrid()
    {
        _inspectionGridImage.Width = !_inspectionGridFit && _inspectionGridImage.Source is BitmapSource bitmap
            ? bitmap.PixelWidth : Math.Max(1, (_inspectionImageScroll?.ActualWidth ?? 300) - 20);
    }

    private void ConfigureInspectionVariantSelector()
    {
        AutomationProperties.SetName(_inspectionVariantSelector, "Surface variant");
        _inspectionVariantSelector.SelectionChanged += (_, _) =>
        {
            if (_updatingInspectionVariants || _inspectionVariantSelector.SelectedItem is not UiInspectionFrameGroup group) return;
            var target = group.Frames.FirstOrDefault(frame => frame.Id == _selectedVariant?.Id) ?? group.Frames.FirstOrDefault();
            if (target is null)
            {
                if (_selectedSurface is not null && group.Id.StartsWith("semantic:", StringComparison.Ordinal))
                    SelectHierarchyVariant(new(_selectedSurface.Id, group.Id["semantic:".Length..]));
                return;
            }
            BuildInspectionFrameButtons(group.Frames);
            SelectVariant(target);
        };
    }

    private sealed record InspectionVariantSelection(string SurfaceId, string VariantId);
    private sealed record InspectionGridChoice(ParameterProposal Proposal)
    {
        public string Label => Proposal.DisplayName;
        public override string ToString() => Label;
    }

    private void SelectHierarchyVariant(InspectionVariantSelection selection)
    {
        var surface = _model?.Layers.SelectMany(layer => layer.Surfaces).FirstOrDefault(s => s.Id == selection.SurfaceId);
        if (surface is null) return;
        SelectSurface(surface);
        var frame = _visibleVariants.FirstOrDefault(f => f.SemanticVariantId == selection.VariantId);
        if (frame is not null) { SelectVariant(frame); return; }
        var node = _model!.Graph.Nodes.FirstOrDefault(n => n.Id == selection.VariantId);
        var label = string.IsNullOrWhiteSpace(node?.Label) ? "Recorded variant" : node.Label;
        var reasons = surface.Variants.Where(f => f.SemanticVariantId == selection.VariantId)
            .Select(f => f.Reason).Distinct(StringComparer.Ordinal).ToArray();
        _selectedVariant = null;
        _inspectionUnavailableVariant = $"{label}: no eligible recorded frames for normal preview." +
            (reasons.Length > 0 ? " Recorded status: " + string.Join(", ", reasons) + "." : " No frame observation is attached.") +
            " Its control hierarchy remains available for inspection.";
        var unavailable = new UiInspectionFrameGroup("semantic:" + selection.VariantId, label + " · no eligible frames", []);
        _updatingInspectionVariants = true;
        _inspectionVariantSelector.ItemsSource = UiInspectionPresentation.FrameGroups(_visibleVariants).Append(unavailable).ToArray();
        _inspectionVariantSelector.SelectedItem = unavailable;
        _inspectionVariantSelector.IsEnabled = _visibleVariants.Count > 0;
        _updatingInspectionVariants = false;
        BuildInspectionFrameButtons([]);
        UpdateVariantNavigatorState();
        RenderAppMap();
        ShowProperties(surface.Source, surface.ControlCount, 0);
    }

    private void BuildInspectionFrameButtons(IReadOnlyList<UiMapVariantView> frames)
    {
        if (_variantCardsPanel is null) return;
        _variantCardsPanel.Children.Clear(); _variantCardButtons.Clear(); _variantCardSummaries.Clear();
        foreach (var frame in frames)
        {
            var button = new Button { Content = frame.FrameSequence.ToString(), MinWidth = 31, Height = 29,
                Margin = new(2), Padding = new(6, 2, 6, 2), Background = Brushes.White, BorderBrush = UiBorder,
                Template = RoundedButtonTemplate(6), Tag = frame.Id };
            button.ToolTip = VariantCardToolTip(frame) + $"\nCapture status: {frame.CaptureStatus}\n{_rawExclusionReasons.GetValueOrDefault(frame.Id)}\n{_rawCaptureWarnings.GetValueOrDefault(frame.Id)}";
            AutomationProperties.SetName(button, VariantCardAccessibleName(frame));
            AutomationProperties.SetHelpText(button, "Recorded frame. Left/Right moves between frames; Home/End selects the first/last frame.");
            button.Click += (_, _) => SelectVariant(frame);
            button.PreviewKeyDown += (_, args) =>
            {
                var offset = args.Key switch { System.Windows.Input.Key.Left => -1, System.Windows.Input.Key.Right => 1,
                    System.Windows.Input.Key.Home => int.MinValue, System.Windows.Input.Key.End => int.MaxValue, _ => 0 };
                if (offset == 0) return;
                var index = frames.ToList().FindIndex(f => f.Id == _selectedVariant?.Id);
                var next = offset == int.MinValue ? 0 : offset == int.MaxValue ? frames.Count - 1 : Math.Clamp(index + offset, 0, frames.Count - 1);
                SelectVariant(frames[next]); _variantCardButtons[frames[next].Id].Focus(); args.Handled = true;
            };
            _variantCardButtons[frame.Id] = button; _variantCardsPanel.Children.Add(button);
        }
    }
}
