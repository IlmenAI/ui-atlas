using System.Globalization;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Reader;

public sealed record EffectiveParameterDecision(ParameterDecision Decision, string SourceProposalId,
    string SourceFingerprint)
{
    public bool IsInherited => Decision.ProposalId != SourceProposalId;
}

/// <summary>Reuse a current confirmation for equivalent roles within one map.
/// Derived decisions are never persisted as new human confirmations.</summary>
public static class ParameterDecisionReuse
{
    public static IReadOnlyDictionary<string, EffectiveParameterDecision> Resolve(
        IReadOnlyList<ParameterProposal> current, IReadOnlyList<ParameterDecision> decisions)
    {
        var direct = decisions.ToDictionary(d => d.ProposalId, StringComparer.Ordinal);
        var result = direct.ToDictionary(d => d.Key,
            d => new EffectiveParameterDecision(d.Value, d.Key, d.Value.SourceFingerprint), StringComparer.Ordinal);
        var approved = current.Where(p => p.ReuseKey.Length > 0 && !p.IsManual && !p.IsGrid &&
            direct.TryGetValue(p.Id, out var d) && ParameterizationAnalysis.State(p, d) == ParameterReviewState.Accepted &&
            d.Scope == p.Scope && d.ParameterName == p.ParameterName && d.ValueType == p.ValueType)
            .GroupBy(p => p.ReuseKey).ToDictionary(g => g.Key,
                g => g.OrderBy(p => p.Id, StringComparer.Ordinal).First());
        foreach (var p in current)
        {
            // A local decision, including a stale one, always takes precedence.
            if (direct.ContainsKey(p.Id) || p.IsManual || p.IsGrid || p.ReuseKey.Length == 0 ||
                !approved.TryGetValue(p.ReuseKey, out var source)) continue;
            var decision = direct[source.Id];
            result[p.Id] = new(decision with { ProposalId = p.Id, SourceFingerprint = p.Fingerprint },
                source.Id, source.Fingerprint);
        }
        return result;
    }

    internal static IReadOnlyList<ParameterProposal> WithStructuralKeys(UiMappingReadModel model,
        IReadOnlyList<ParameterProposal> proposals)
    {
        var layer = model.LayerFor(UiUnderstandingLevel.SemanticWorld);
        var controls = layer.Controls.ToDictionary(c => c.Id);
        var surfaces = layer.Surfaces.ToDictionary(s => s.Id);
        var byTarget = proposals.Where(IsNamedRole).GroupBy(p => p.TargetId).ToDictionary(g => g.Key, g => g.First());
        var captions = proposals.Where(p => IsNamedRole(p) && p.ParameterName != "sheet_name" &&
            p.ParameterName != "slide_name" && p.ParameterName != "section_name")
            .GroupBy(p => p.SurfaceId).ToDictionary(g => g.Key, g => g.Select(p => Normalize(p.DisplayName)).ToHashSet());

        string Name(string id, string surface, string role, string label)
        {
            if (byTarget.TryGetValue(id, out var parameter)) return "$" + parameter.ParameterName;
            var normalized = Normalize(label);
            if (role is "window" or "titlebar" or "pane" or "custom" or "document" &&
                captions.TryGetValue(surface, out var names) &&
                names.Any(n => n == normalized || role is "custom" or "document" &&
                    (n == normalized + " - EXCEL" || n == normalized + " - WORD" || n == normalized + " - POWERPOINT")))
                return "$document_name";
            return normalized;
        }
        string Identity(GraphNode node, string name)
        {
            var selectors = node.Properties.Where(p => p.Name is "automationId" or "className" or "frameworkId")
                .Select(p => (Key: p.Name, Value: p.Value))
                .Concat(node.Properties.Where(p => p.Name == "semanticSelector" && p.Value.Contains('='))
                    .Select(p => (Key: p.Value[..p.Value.IndexOf('=')], Value: p.Value[(p.Value.IndexOf('=') + 1)..]))
                    .Where(p => p.Key is "automationId" or "className" or "frameworkId"))
                .Select(p => p.Key + "=" + (p.Key == "automationId" && name.StartsWith('$') &&
                    Normalize(p.Value) == Normalize(node.Label) ? name : p.Value))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            return ParameterizationAnalysis.Fingerprint(string.Join("\n", selectors), name);
        }
        string Key(ParameterProposal p)
        {
            if (p.IsManual || p.IsGrid || !(IsNamedRole(p) || p.PropertyName == "Value" && p.ParameterName == "control_value") ||
                !controls.TryGetValue(p.TargetId, out var control) || !surfaces.TryGetValue(p.SurfaceId, out var surface)) return "";
            var parts = new List<string> { Normalize(model.ApplicationDisplayName), p.ParameterName, p.PropertyName, p.ValueType, p.Scope };
            var seen = new HashSet<string>();
            while (seen.Count < 64 && seen.Add(control.Id))
            {
                var role = Role(control.CanonicalKind);
                parts.Add(role);
                parts.Add(Identity(control.Source, Name(control.Id, p.SurfaceId, role, control.DisplayName)));
                if (!controls.TryGetValue(control.ParentControlId, out var parent))
                {
                    if (control.Source.ParentId != surface.Id) return "";
                    parts.Add(surface.SurfaceKind);
                    parts.Add(Identity(surface.Source, Name(surface.Id, surface.Id, "window", surface.DisplayName)));
                    return ParameterizationAnalysis.Fingerprint(parts.ToArray());
                }
                if (parent.OwnerSurfaceId != p.SurfaceId) return "";
                control = parent;
            }
            return ""; // Broken or cyclic ancestry cannot establish a reusable role.
        }
        return proposals.Select(p => p with { ReuseKey = Key(p) }).ToArray();
    }

    private static bool IsNamedRole(ParameterProposal p) => !p.IsManual && !p.IsGrid && p.PropertyName == "Name" &&
        p.ParameterName is "workbook_name" or "document_name" or "presentation_name" or "sheet_name" or "slide_name" or "section_name";
    private static string Role(string value) => value.Replace("ControlType.", "", StringComparison.OrdinalIgnoreCase)
        .Replace(" ", "").ToLowerInvariant();
    private static string Normalize(string value) => string.Join(" ", new string(value
        .Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray())
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
