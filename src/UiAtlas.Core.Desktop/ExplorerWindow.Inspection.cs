using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Reader;

namespace UiAtlas.Core.Desktop;

public sealed partial class ExplorerWindow
{
    private readonly ListBox _inspectionVariantSelector = new()
    { MinWidth = 100, MaxWidth = 650, MaxHeight = 64, DisplayMemberPath = nameof(UiInspectionFrameGroup.Label),
        BorderThickness = new(0), Background = Brushes.Transparent, Margin = new(3, 0, 8, 0) };
    private bool _updatingInspectionVariants;
    private string? _inspectionUnavailableVariant;

    private void RefreshInspectionTitle()
    {
        _appMapTitle.Text = _selectedSurface?.DisplayName ?? "Control preview";
        _appMapTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _appMapTitle.ToolTip = _appMapTitle.Text;
    }

    private void ConfigureInspectionVariantSelector()
    {
        AutomationProperties.SetName(_inspectionVariantSelector, "Surface variant");
        var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        _inspectionVariantSelector.ItemsPanel = new ItemsPanelTemplate(panel);
        ScrollViewer.SetHorizontalScrollBarVisibility(_inspectionVariantSelector, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_inspectionVariantSelector, ScrollBarVisibility.Disabled);
        _inspectionVariantSelector.PreviewKeyDown += (_, e) =>
        {
            if (_inspectionVariantSelector.Items.Count == 0 || e.Key is not (System.Windows.Input.Key.Left or System.Windows.Input.Key.Right)) return;
            _inspectionVariantSelector.SelectedIndex = Math.Clamp(_inspectionVariantSelector.SelectedIndex + (e.Key == System.Windows.Input.Key.Left ? -1 : 1), 0, Math.Max(0, _inspectionVariantSelector.Items.Count - 1));
            _inspectionVariantSelector.ScrollIntoView(_inspectionVariantSelector.SelectedItem); e.Handled = true;
        };
        AutomationProperties.SetHelpText(_inspectionVariantSelector, "Visible surface variants. Select a variant to inspect its recorded frames.");
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 5, 9, 5)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(1, 2, 1, 2)));
        itemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type ListBoxItem}">
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                    BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Stretch" VerticalAlignment="Center"/>
                </Border>
            </ControlTemplate>
            """)));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#EFE8FF")));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#8B5CF6")));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#6D28D9")));
        itemStyle.Triggers.Add(selected);
        var hovered = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hovered.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#A78BFA")));
        itemStyle.Triggers.Add(hovered);
        _inspectionVariantSelector.ItemContainerStyle = itemStyle;
        _inspectionVariantSelector.SelectionChanged += (_, _) =>
        {
            if (_updatingInspectionVariants || _inspectionVariantSelector.SelectedItem is not UiInspectionFrameGroup group) return;
            var target = group.Frames.FirstOrDefault(frame => frame.Id == _selectedVariant?.Id) ?? group.Frames.FirstOrDefault();
            if (target is null)
            {
                if (_selectedSurface is not null)
                    SelectHierarchyVariant(new(_selectedSurface.Id, group.Id));
                return;
            }
            BuildInspectionFrameButtons(group.Frames);
            SelectVariant(target);
        };
    }

    private sealed record InspectionVariantSelection(string SurfaceId, string GroupId);
    private void SelectHierarchyVariant(InspectionVariantSelection selection)
    {
        var surface = _model?.Layers.SelectMany(layer => layer.Surfaces).FirstOrDefault(s => s.Id == selection.SurfaceId);
        if (surface is null || _model is null) return;
        var group = UiInspectionPresentation.Variants(_model, surface, _parameterProposals)
            .FirstOrDefault(v => v.Group.Id == selection.GroupId)?.Group;
        if (group is null) return;
        var current = _selectedSurface?.Id == surface.Id ? _selectedVariant : null;
        if (_selectedSurface?.Id != surface.Id || _selectedControl is not null) SelectSurface(surface);
        var eligible = UiInspectionPresentation.IncludeEquivalentFrames(_model.VariantsFor([surface]), surface.Variants);
        var frameIds = group.Frames.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        _visibleVariants = eligible;
        var frame = eligible.FirstOrDefault(f => f.Id == current?.Id && frameIds.Contains(f.Id))
            ?? eligible.FirstOrDefault(f => frameIds.Contains(f.Id));
        BuildVariantCards(eligible);
        if (frame is not null) { SelectVariant(frame); return; }
        var label = group.Label;
        var reasons = group.Frames
            .Select(f => f.Reason).Distinct(StringComparer.Ordinal).ToArray();
        _selectedVariant = null;
        _inspectionUnavailableGroupId = group.Id;
        _inspectionUnavailableVariant = $"{label}: no eligible recorded frames for normal preview." +
            (reasons.Length > 0 ? " Recorded status: " + string.Join(", ", reasons) + "." : " No frame observation is attached.") +
            " Its control hierarchy remains available for inspection.";
        var unavailable = new UiInspectionFrameGroup(group.Id, label + " · no eligible frames", []);
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
