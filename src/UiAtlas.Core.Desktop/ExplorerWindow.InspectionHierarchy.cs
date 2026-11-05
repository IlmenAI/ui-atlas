using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Desktop;

public sealed partial class ExplorerWindow
{
    private sealed record InspectionTreeSelection(string SurfaceId, string GroupId,
        string? GridId = null, string? ColumnKey = null, string? ControlId = null);
    private readonly List<(TreeViewItem Branch, UiMapSurfaceView Surface, string GroupId)> _inspectionGridBranches = [];
    private string? _inspectionUnavailableGroupId;

    private void PopulateInspectionHierarchy(TreeViewItem surfaceItem, UiMapSurfaceView surface,
        string query, string? revealedControlId, bool expand)
    {
        if (_model is null) return;
        surfaceItem.Items.Clear();
        foreach (var variant in UiInspectionPresentation.Variants(_model, surface, _parameterProposals))
        {
            var group = variant.Group;
            var groupMatches = group.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                group.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
            var parents = UiControlHierarchy.Parents(variant.Controls, groupMatches ? "" : query, revealedControlId);
            if (query.Length > 0 && parents.Count == 0 && !groupMatches && !variant.Grids.Any(g => UiInspectionPresentation.Matches(g, query))) continue;
            var selection = new InspectionTreeSelection(surface.Id, group.Id);
            var variantItem = TreeItem((group.Id.StartsWith("semantic:", StringComparison.Ordinal) ? "Variant · " : "") + group.Label,
                null, expand, LevelBrush(surface.Level), FontWeights.SemiBold);
            variantItem.Tag = selection;
            variantItem.ToolTip = $"{group.Id}\n{group.Frames.Count} recorded frames · {variant.Controls.Count} controls · {variant.Grids.Count} retained grids";
            surfaceItem.Items.Add(variantItem);
            var controlsBranch = TreeItem("Controls", null, query.Length > 0, LevelBrush(surface.Level), FontWeights.SemiBold);
            controlsBranch.Tag = selection;
            variantItem.Items.Add(controlsBranch);
            var gridBranch = TreeItem("Data grids", null, query.Length > 0, LevelBrush(surface.Level), FontWeights.SemiBold);
            gridBranch.Tag = selection with { GridId = "" };
            variantItem.Items.Add(gridBranch);
            _inspectionGridBranches.Add((gridBranch, surface, group.Id));
            PopulateInspectionGridBranch(gridBranch, selection, variant.Grids, groupMatches ? "" : query);
            var controlItems = new Dictionary<string, TreeViewItem>(StringComparer.Ordinal);
            foreach (var control in variant.Controls.Where(c => parents.ContainsKey(c.Id))
                .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal))
            {
                var unverified = control.Source.Properties.Any(p => p.Name == "verificationStatus" && p.Value == "Unverified");
                var canConfirm = GraphControlConfirmation.IsConfirmableButtonCandidate(control.Source);
                var canRemove = GraphControlConfirmation.IsRemovableButtonCandidate(control.Source);
                var label = UiInspectionPresentation.ControlLabel(control);
                var child = TreeItem(label + (canConfirm ? " · button candidate" : unverified ? " · unverified" : ""),
                    null, query.Length > 0, canConfirm ? Brush("#F97316") : unverified ? Muted : LevelBrush(surface.Level),
                    FontWeights.Normal, canConfirm ? () => ConfirmButtonCandidateAsync(control.Id) : null,
                    canRemove ? () => RemoveButtonCandidateAsync(control.Id) : null);
                child.Tag = selection with { ControlId = control.Id };
                child.Opacity = unverified && !canConfirm ? 0.68 : 1;
                child.ToolTip = $"{label}\nID: {control.Id}\nParent: {control.ParentControlId}\n" +
                    (canConfirm ? "Potential button: hover to confirm or remove" : unverified ? "Discovered but not yet visible or confirmed" : "Observed");
                controlItems[control.Id] = child;
                // Prefer the active observation when a raw control appears in several groups.
                if (!_hierarchyItems.ContainsKey(control.Id) || group.Frames.Any(f => f.Id == _selectedVariant?.Id))
                    _hierarchyItems[control.Id] = child;
            }
            foreach (var (id, child) in controlItems)
                (controlItems.GetValueOrDefault(parents[id]) ?? controlsBranch).Items.Add(child);
            if (controlItems.Count == 0)
                controlsBranch.Items.Add(TreeItem(variant.Controls.Count == 0 ? "No recorded controls" : "No matching controls", null, false, Muted, FontWeights.Normal));
        }
    }

    private void PopulateInspectionGridBranch(TreeViewItem branch, InspectionTreeSelection selection,
        IReadOnlyList<ParameterProposal> grids, string query)
    {
        var expanded = branch.Items.OfType<TreeViewItem>().Where(item => item.IsExpanded)
            .Select(item => item.Tag).OfType<InspectionTreeSelection>().Select(tag => tag.GridId).ToHashSet(StringComparer.Ordinal);
        var selected = (_hierarchy.SelectedItem as TreeViewItem)?.Tag as InspectionTreeSelection;
        var restore = selected?.SurfaceId == selection.SurfaceId && selected.GroupId == selection.GroupId && selected.GridId is { Length: > 0 };
        TreeViewItem? selectedItem = null;
        branch.Items.Clear();
        foreach (var grid in grids.Where(g => query.Length == 0 || UiInspectionPresentation.Matches(g, query)))
        {
            var gridSelection = selection with { GridId = grid.Id };
            var item = TreeItem(string.IsNullOrWhiteSpace(grid.DisplayName) ? "Unnamed data grid" : grid.DisplayName,
                null, query.Length > 0 || expanded.Contains(grid.Id), Blue, FontWeights.Normal);
            item.Tag = gridSelection;
            if (restore && selected == gridSelection) selectedItem = item;
            item.ToolTip = $"{grid.Id}\nHost: {grid.TargetId}\n{grid.Coverage}\nSaved mapping capture; separate from the selected frame.";
            branch.Items.Add(item);
            foreach (var column in (grid.Schema?.Columns ?? []).OrderBy(c => c.Ordinal))
            {
                var label = $"{column.Ordinal + 1}. " + (string.IsNullOrWhiteSpace(column.Label) ? "(unnamed column)" : column.Label);
                var child = TreeItem(label, null, false, Ink, FontWeights.Normal);
                child.Tag = gridSelection with { ColumnKey = column.ColumnKey };
                if (restore && selected == (InspectionTreeSelection)child.Tag) selectedItem = child;
                child.ToolTip = $"Physical column: {column.ColumnKey}\nRecorded ordinal: {column.Ordinal}\nRecorded header: {column.Label}";
                item.Items.Add(child);
            }
            if (item.Items.Count == 0) item.Items.Add(TreeItem("No retained column schema", null, false, Muted, FontWeights.Normal));
        }
        if (branch.Items.Count == 0) branch.Items.Add(TreeItem(_parameterError is not null ? "Retained grids unavailable: " + _parameterError
            : grids.Count == 0 ? "No retained data grids for this variant" : "No matching data grids", null, false, Muted, FontWeights.Normal));
        if (restore)
        {
            var synchronizing = _synchronizing;
            _synchronizing = true;
            (selectedItem ?? branch).IsSelected = true;
            _synchronizing = synchronizing;
        }
    }

    private void RefreshInspectionHierarchyGrids()
    {
        if (_parameterReviewMode is not null || _model is null) return;
        if (_search.Text.Trim().Length > 0) { BuildHierarchy(_search.Text.Trim()); return; }
        foreach (var surfaceBranches in _inspectionGridBranches.GroupBy(entry => entry.Surface.Id))
        {
            var variants = UiInspectionPresentation.Variants(_model, surfaceBranches.First().Surface, _parameterProposals);
            foreach (var (branch, surface, groupId) in surfaceBranches)
            {
                var variant = variants.FirstOrDefault(v => v.Group.Id == groupId);
                if (variant is not null)
                {
                    PopulateInspectionGridBranch(branch, new(surface.Id, groupId), variant.Grids, "");
                    if (branch.Parent is TreeViewItem parent)
                        parent.ToolTip = $"{groupId}\n{variant.Group.Frames.Count} recorded frames · {variant.Controls.Count} controls · {variant.Grids.Count} retained grids";
                }
            }
        }
    }

    private void SelectInspectionTreeItem(TreeViewItem item, InspectionTreeSelection selection)
    {
        // Internal frame navigation reveals the surface. Preserve the clicked tree viewport
        // so an expanded grid's full subtree does not scroll its variant out of view.
        DependencyObject? ancestor = item;
        while (ancestor is not null && ancestor is not ScrollViewer) ancestor = VisualTreeHelper.GetParent(ancestor);
        var scroll = ancestor as ScrollViewer;
        var horizontal = scroll?.HorizontalOffset ?? 0;
        var vertical = scroll?.VerticalOffset ?? 0;
        SelectHierarchyVariant(new(selection.SurfaceId, selection.GroupId));
        if (selection.ControlId is not null) SelectControlById(selection.ControlId);
        _synchronizing = true;
        item.IsSelected = true;
        _synchronizing = false;
        if (scroll is not null)
            item.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!item.IsSelected || !_hierarchy.IsAncestorOf(item)) return;
                scroll.ScrollToHorizontalOffset(horizontal);
                scroll.ScrollToVerticalOffset(vertical);
            }));
    }
}
