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
    private readonly ListBox _inspectionVariantSelector = global::UiAtlas.Presentation.Appearance.Bind(new ListBox(){ MinWidth = 100, MaxWidth = 650, MaxHeight = 64, DisplayMemberPath = nameof(UiInspectionFrameGroup.Label),
        BorderThickness = new(0), Margin = new(3, 0, 8, 0) }, ("Background", "TransparentBrush"));
    private bool _updatingInspectionVariants;

    private void RefreshInspectionTitle()
    {
        _appMapTitle.Text = _selectedSurface?.DisplayName ?? "Control preview";
        _appMapTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _appMapTitle.ToolTip = _appMapTitle.Text;
    }

    private void ConfigureInspectionVariantSelector()
    {
        AutomationProperties.SetName(_inspectionVariantSelector, "Surface variant");
        global::UiAtlas.Presentation.SurfaceVariantSelector.Configure(_inspectionVariantSelector);
        _inspectionVariantSelector.SelectionChanged += (_, _) =>
        {
            if (_updatingInspectionVariants || _inspectionVariantSelector.SelectedItem is not UiInspectionFrameGroup group) return;
            var target = group.Frames.FirstOrDefault(frame => frame.Id == _selectedVariant?.Id) ?? group.Frames[0];
            BuildInspectionFrameButtons(group.Frames);
            SelectVariant(target);
        };
    }

    private bool InspectingWorld => _inspectionLevel is UiUnderstandingLevel.RawWorld or UiUnderstandingLevel.SemanticWorld;

    private string? SurfaceExclusion(UiMapVariantView variant)
    {
        if (_evidence is null) return SurfaceFrameVisibility.Unknown;
        try { return _evidence.SurfaceExclusion(variant); }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        { return SurfaceFrameVisibility.Unknown; }
    }

    private IReadOnlyList<UiInspectionFrameGroup> PreviewGroups(IReadOnlyList<UiMapVariantView> variants, UiUnderstandingLevel level)
    {
        var groups = UiInspectionPresentation.FrameGroups(variants);
        if (level is not (UiUnderstandingLevel.RawWorld or UiUnderstandingLevel.SemanticWorld)) return groups;
        return groups.Select(group => group with { Frames = group.Frames.Where(f => SurfaceExclusion(f) is null).ToArray() })
            .Where(group => group.Frames.Count > 0).ToArray();
    }

    private IReadOnlyList<UiInspectionFrameGroup> SurfacePreviewGroups(UiMapSurfaceView surface)
    {
        var candidates = UiInspectionPresentation.IncludeEquivalentFrames(_model!.VariantsFor([surface]), surface.Variants);
        return PreviewGroups(candidates, surface.Level);
    }

    private bool SelectHierarchyVariant(InspectionTreeSelection selection)
    {
        var surface = _model?.Layers.SelectMany(layer => layer.Surfaces).FirstOrDefault(s => s.Id == selection.SurfaceId);
        if (surface is null) return false;
        var groups = SurfacePreviewGroups(surface);
        var selectedGroup = groups.FirstOrDefault(g => g.Id == selection.GroupId);
        if (selectedGroup is null) return false;
        var current = _selectedSurface?.Id == surface.Id ? _selectedVariant : null;
        if (_selectedSurface?.Id != surface.Id || _selectedControl is not null) SelectSurface(surface);
        _visibleVariants = groups.SelectMany(g => g.Frames).ToArray();
        var frame = selectedGroup.Frames.FirstOrDefault(f => f.Id == current?.Id) ?? selectedGroup.Frames[0];
        _selectedVariant = frame;
        BuildVariantCards(groups);
        SelectVariant(frame);
        return true;
    }

    private void BuildInspectionFrameButtons(IReadOnlyList<UiMapVariantView> frames)
    {
        if (_variantCardsPanel is null) return;
        _variantCardsPanel.Children.Clear(); _variantCardButtons.Clear(); _variantCardSummaries.Clear();
        foreach (var frame in frames)
        {
            var button = global::UiAtlas.Presentation.Appearance.Bind(new Button { Content = frame.FrameSequence.ToString(), MinWidth = 31, Height = 29,
                Margin = new(2), Padding = new(6, 2, 6, 2),                 Template = RoundedButtonTemplate(6), Tag = frame.Id },("BorderBrush", "LineBrush"),("Background", "PanelBrush"));
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
