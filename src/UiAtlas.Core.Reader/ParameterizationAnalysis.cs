using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Reader;

public static class ParameterizationAnalysis
{
    public static IReadOnlyList<ParameterProposal> Build(UiMappingReadModel model,
        IReadOnlyList<ParameterGridSource>? mappedGrids = null,
        IReadOnlyList<ParameterProposal>? manualProposals = null)
    {
        var layer = model.LayerFor(UiUnderstandingLevel.SemanticWorld);
        var result = new List<ParameterProposal>();
        var nodes = model.Graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var controls = layer.Controls.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var gridIds = layer.Controls.Where(IsGrid).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var importedHosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grid in mappedGrids ?? [])
        {
            var review = grid.Review.Review;
            var bounds = controls.TryGetValue(grid.TargetId, out var control) ? control.Bounds : grid.Review.Context.RelativeBounds;
            var evidence = new ParameterEvidence("Reviewed mapping schema",
                $"{review.Schema.Columns.Count} physical columns; {(review.Schema.IsComplete ? "complete width" : "partial width")}. Header review does not verify current rows or value types.",
                $"grid:{grid.Id}; schema:{review.Schema.Revision}; image:{review.ImageSha256}");
            result.Add(new("grid:" + grid.Id, grid.TargetId, grid.SurfaceId, grid.DisplayName,
                "table_data", "Collection", "This mapped grid", "Contents", true, bounds, [evidence],
                Fingerprint(grid.Id, grid.TargetId, grid.HostBound.ToString(), JsonSerializer.Serialize(review.Schema),
                    review.ImageSha256, JsonSerializer.Serialize(review.Coverage)),
                review.Schema, review.HeaderBounds, grid.ImagePath, review.ImageSha256, grid.HostBound,
                Coverage: $"Columns: {(review.Schema.IsComplete ? "complete" : "partial")} · Captured rows: {(review.Coverage.RowCoverageComplete ? "complete extent" : "partial / unknown extent")} · Row values not verified"));
            if (grid.HostBound)
            {
                importedHosts.Add(grid.TargetId); gridIds.Add(grid.TargetId);
                var host = controls.TryGetValue(grid.TargetId, out var mapped)
                    ? UiMappingReadModel.Property(mapped.Source, "gridHostControlRef") : null;
                if (!string.IsNullOrEmpty(host)) { importedHosts.Add(host); gridIds.Add(host); }
            }
        }
        foreach (var c in layer.Controls)
        {
            if (result.Count >= 9000) break;
            if (gridIds.Contains(c.Id))
            {
                if (importedHosts.Contains(c.Id)) continue;
                result.Add(Create(c.Source, c.OwnerSurfaceId, c.Bounds, "Contents", "grid_data", "Collection",
                    "This grid", true, [new("Control role", "Grid contents are variable by default. No schema is applied.",
                        $"role:{c.CanonicalKind}; target:{c.Id}")]));
                continue;
            }
            if (InsideGrid(c.Source, nodes, gridIds) || c.CanonicalKind.Contains("Column", StringComparison.OrdinalIgnoreCase)) continue;
            var observations = (c.ObservationSources ?? [c.Source]).SelectMany(n => n.Observations ?? [])
                .Select(o => o.Control.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).Take(12).ToArray();
            var evidence = new List<ParameterEvidence>();
            var name = "element_name";
            var property = "Name";
            var scope = "This control within its surface";
            var kind = Role(c.CanonicalKind);
            var isCommand = kind is "button" or "menuitem" or "tabitem" or "toolbar" or "splitbutton";
            var isContainer = kind is "window" or "pane" or "custom" or "group" or "document" or "titlebar" or "tab";
            var isValueControl = kind is "combobox" or "edit" or "editbox" or "textbox" or "spinner" or "slider" or "listbox";
            if (!isCommand && !isContainer && !isValueControl && observations.Length > 1)
                evidence.Add(new("Observed variation", string.Join(" / ", observations), "Retained observations of the same mapped control"));
            var excel = model.ApplicationDisplayName.Contains("EXCEL", StringComparison.OrdinalIgnoreCase);
            var word = model.ApplicationDisplayName.Contains("WINWORD", StringComparison.OrdinalIgnoreCase)
                || model.ApplicationDisplayName.EndsWith("Word", StringComparison.OrdinalIgnoreCase);
            var powerpoint = model.ApplicationDisplayName.Contains("POWERPNT", StringComparison.OrdinalIgnoreCase)
                || model.ApplicationDisplayName.Contains("PowerPoint", StringComparison.OrdinalIgnoreCase);
            if (excel && (kind == "sheettab" || kind == "tabitem" && HasIdentity(c, "SheetTab")))
            {
                name = "sheet_name"; scope = "Worksheet within a workbook";
                evidence.Add(new("Product documentation", "Worksheet names can be changed. This does not identify the intended worksheet.",
                    "https://support.microsoft.com/en-us/excel/rename-a-worksheet",
                    "Excel for Microsoft 365, 2024, 2021, 2019, 2016; verify the mapped version."));
            }
            else if ((excel || word || powerpoint) && (kind is "button" or "text") &&
                controls.TryGetValue(c.ParentControlId, out var parent) && Role(parent.CanonicalKind) == "titlebar" &&
                layer.Surfaces.Any(s => s.Id == c.OwnerSurfaceId &&
                    NormalizeTitle(s.DisplayName) == NormalizeTitle(c.DisplayName)))
            {
                name = excel ? "workbook_name" : word ? "document_name" : "presentation_name";
                scope = excel ? "Workbook caption; retain the application suffix" : "Document caption; retain the application suffix";
                evidence.Add(new("Role inference", excel
                    ? "The recorded caption control displays the workbook name. Its name may vary between workbooks."
                    : "The recorded caption control displays the document name. Its name may vary between documents.",
                    "Named caption control within the recorded title bar"));
            }
            else if (powerpoint && kind is "slide" or "slidethumbnail" or "section")
            {
                name = kind == "section" ? "section_name" : "slide_name";
                scope = "This slide or section within its presentation";
                evidence.Add(new("Control role", "The recorded slide or section label may vary. This does not identify the intended item.",
                    $"Recorded {c.CanonicalKind} role"));
            }
            else if (isValueControl)
            {
                // A stable label such as Font Size names the setting; the value is the parameter.
                if (controls.TryGetValue(c.ParentControlId, out var inputParent) &&
                    Role(inputParent.CanonicalKind) == "combobox" && (kind is "edit" or "editbox" or "textbox")) continue;
                property = "Value"; name = "control_value"; scope = "This control in the current UI context";
                evidence.Add(new("Control role", "This input or selection control can hold a contextual value. Editability and allowed values require review.",
                    $"Recorded {c.CanonicalKind} control; this does not authorize changing it"));
            }
            // Container names remain raw evidence; do not pretend they locate the visible label.
            if (!isContainer && evidence.Count > 0)
                result.Add(Create(c.Source, c.OwnerSurfaceId, c.Bounds, property, name, "Text", scope, false, evidence));
        }
        foreach (var manual in manualProposals?.Where(p => p.IsManual) ?? [])
        {
            ParameterProposal updated;
            if (manual.ParentProposalId is { } parentId)
            {
                var sourceId = manual.Evidence.LastOrDefault(e => e.Kind == "Mapper association")?.Source;
                var source = result.FirstOrDefault(p => p.Id == sourceId);
                var parent = result.FirstOrDefault(p => p.Id == parentId);
                if (source is null || parent is null) continue;
                else updated = source with { Id = manual.Id, IsManual = true, ParentProposalId = parentId,
                    Fingerprint = Fingerprint(parent.Fingerprint, source.Fingerprint),
                    Evidence = source.Evidence.Append(new ParameterEvidence("Mapper association",
                        "Explicit subregion of " + parent.DisplayName, source.Id)).ToArray() };
            }
            else
            {
                var target = nodes.GetValueOrDefault(manual.TargetId);
                if (target is null) continue;
                updated = Create(target, manual.SurfaceId, target.Evidence.FirstOrDefault()?.Bounds ?? manual.Bounds,
                    manual.PropertyName, manual.ParameterName, manual.ValueType, manual.Scope, manual.IsGrid,
                    manual.Evidence, true);
            }
            result.RemoveAll(p => p.Id == updated.Id);
            result.Add(updated);
        }
        return ParameterDecisionReuse.WithStructuralKeys(model, result)
            .GroupBy(p => p.Id, StringComparer.Ordinal).Select(g => g.First())
            .OrderByDescending(p => p.IsGrid).ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static ParameterProposal Create(GraphNode node, string surfaceId, RectI bounds,
        string property, string name, string type, string scope, bool grid, IReadOnlyList<ParameterEvidence> evidence,
        bool manual = false) =>
        new("param:" + Fingerprint(node.Id, property)[..24], node.Id, surfaceId, node.Label,
            name, type, scope, property, grid, bounds, evidence,
            Fingerprint(node.StableKey, property, node.Label, JsonSerializer.Serialize(evidence),
                string.Join("|", node.Evidence.Select(e => $"{e.BundleId}:{e.FrameSequence}").Order(StringComparer.Ordinal))),
            IsManual: manual);

    public static ParameterReviewState State(ParameterProposal p, ParameterDecision? decision) =>
        decision is null ? ParameterReviewState.Suggested :
        decision.SourceFingerprint != p.Fingerprint ? ParameterReviewState.NeedsEvidence : decision.State;

    public static string Fingerprint(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", values)))).ToLowerInvariant();

    private static bool IsGrid(UiMapControlView c) =>
        c.CanonicalKind.ToLowerInvariant() is "datagrid" or "data grid" or "table" or "grid" or "mapdatagrid" ||
        UiMappingReadModel.Property(c.Source, "mapEntityKind") == "MapDataGrid";

    private static string Role(string value) => value.Replace(" ", "").ToLowerInvariant();
    private static string NormalizeTitle(string value) => string.Join(" ", new string(value
        .Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray())
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    private static bool HasIdentity(UiMapControlView control, string value) =>
        new[] { control.Source }.Concat(control.ObservationSources ?? [])
            .Any(n => n.Properties.Any(p => p.Name == "automationId" && p.Value == value ||
                p.Name == "semanticSelector" && p.Value == "automationId=" + value));

    private static bool InsideGrid(GraphNode node, IReadOnlyDictionary<string, GraphNode> nodes, IReadOnlySet<string> grids)
    {
        var id = node.ParentId;
        for (var i = 0; i < 64 && nodes.TryGetValue(id, out var parent); i++)
        { if (grids.Contains(id)) return true; id = parent.ParentId; }
        return false;
    }
}
