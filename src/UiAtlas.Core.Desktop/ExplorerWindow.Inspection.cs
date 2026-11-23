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
        global::UiAtlas.Presentation.SurfaceVariantSelector.Configure(_inspectionVariantSelector);
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

    private bool InspectingWorld => _inspectionLevel is UiUnderstandingLevel.RawWorld or UiUnderstandingLevel.SemanticWorld;

    private string? SurfaceExclusion(UiMapVariantView variant)
    {
        if (!InspectingWorld) return null;
        if (_evidence is null) return SurfaceFrameVisibility.Unknown;
        try { return _evidence.SurfaceExclusion(variant); }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        { return SurfaceFrameVisibility.Unknown; }
    }

    private UiInspectionFrameGroup PreviewGroup(UiInspectionFrameGroup group)
    {
        if (!InspectingWorld) return group;
        var frames = group.Frames.Where(f => SurfaceExclusion(f) is null).ToArray();
        if (frames.Length > 0) return group with { Frames = frames };
        var reason = group.Frames.Select(SurfaceExclusion).FirstOrDefault() ?? "No frame is attached to this state.";
        return group with { Label = group.Label + " · no clear capture", Frames = [], Unavailable = "No clear capture of this state. " + reason };
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
        var candidates = UiInspectionPresentation.IncludeEquivalentFrames(_model.VariantsFor([surface]), surface.Variants);
        var groups = UiInspectionPresentation.FrameGroups(candidates).Select(PreviewGroup).ToList();
        var selectedGroup = groups.FirstOrDefault(g => g.Id == selection.GroupId);
        if (selectedGroup is null)
        {
            selectedGroup = group with { Label = group.Label + " · no eligible frames", Frames = [], Unavailable = $"{group.Label}: no eligible recorded frames for this state." };
            groups.Add(selectedGroup);
        }
        _visibleVariants = groups.SelectMany(g => g.Frames).ToArray();
        var frame = selectedGroup.Frames.FirstOrDefault(f => f.Id == current?.Id) ?? selectedGroup.Frames.FirstOrDefault();
        _selectedVariant = frame;
        _inspectionUnavailableGroupId = frame is null ? selectedGroup.Id : null;
        _inspectionUnavailableVariant = frame is null ? selectedGroup.Unavailable : null;
        BuildVariantCards(groups);
        if (frame is not null) { SelectVariant(frame); return; }
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
