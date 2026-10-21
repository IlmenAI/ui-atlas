using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using UiAtlas.Catalog;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Storage;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

/// <summary>Compile accepted editor decisions to exact source-property bindings.</summary>
internal static class AtlasParameterMatching
{
    internal static CatalogParameterPolicy Read(AtlasMapWorkspace workspace, UiMapRevision revision,
        CatalogRawWorldSource[] sources, CancellationToken token)
    {
        var path = Path.Combine(workspace.DirectoryPath, "parameter-review.json");
        var document = MapParameterizationStore.LoadFile(path, workspace.Index.LogicalMapId);
        // Most maps have no decisions. Avoid rebuilding the presentation graph in that case.
        if (!document.Decisions.Any(d => d.State == ParameterReviewState.Accepted))
            return Compile(document, [], revision, sources, token);
        return Compile(document, Proposals(workspace, revision, document, token).Applicable, revision, sources, token);
    }

    internal static (ParameterProposal[] All, ParameterProposal[] Applicable) Proposals(AtlasMapWorkspace workspace,
        UiMapRevision revision, MapParameterizationDocument document, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var graph = workspace.Project();
        var proposals = ParameterizationAnalysis.Build(new UiMappingReadModel(graph),
            AtlasParameterizationAdapter.LoadGrids(workspace, graph), document.Proposals);
        var drafts = workspace.Drafts;
        foreach (var draft in drafts) UIMaps.Map.Core.UiMapCanonicalizer.Sort(draft);
        var draftEntities = drafts.SelectMany(d => d.Entities).ToDictionary(e => e.MapEntityRef, StringComparer.Ordinal);
        var exact = revision.Entities.ToDictionary(e => e.MapEntityRef, StringComparer.Ordinal);
        var applicable = proposals.Where(p => exact.TryGetValue(p.TargetId, out var published)
            && draftEntities.TryGetValue(p.TargetId, out var current) && published.Equals(current)).ToArray();
        return (proposals.ToArray(), applicable);
    }

    internal static CatalogParameterPolicy Compile(MapParameterizationDocument document,
        IReadOnlyList<ParameterProposal> current, UiMapRevision revision,
        CatalogRawWorldSource[] sources, CancellationToken token = default)
    {
        MapParameterizationStore.Validate(document, document.LogicalMapId);
        var entities = revision.Entities.ToDictionary(e => e.MapEntityRef, StringComparer.Ordinal);
        var raw = sources.ToDictionary(s => s.SourceObservationSetRef, s =>
        {
            var bytes = Convert.FromBase64String(s.Payload);
            if (Hash(bytes) != s.PayloadSha256 || !revision.Sources.Any(d =>
                d.SourceObservationSetRef == s.SourceObservationSetRef && d.SourcePayloadHash == s.PayloadSha256))
                throw new InvalidDataException("Parameter source does not belong to the exact Atlas revision.");
            return (s.PayloadKind == "snapshot" ? WorldStateSnapshot.Parser.ParseFrom(bytes).WorldObjects.ToArray()
                : s.PayloadKind == "survey" ? SurveyScenePublication.Parser.ParseFrom(bytes).WorldObjects.Select(o => o.WorldObject).ToArray()
                : throw new InvalidDataException("Unsupported parameter source.")).ToDictionary(o => o.WorldObjectId, StringComparer.Ordinal);
        }, StringComparer.Ordinal);
        var effective = ParameterDecisionReuse.Resolve(current, document.Decisions);
        var decisions = effective.ToDictionary(d => d.Key, d => d.Value.Decision, StringComparer.Ordinal);
        var rules = new List<CatalogParameterRule>();
        foreach (var p in current.Where(p => !p.IsGrid && p.PropertyName is "Name" or "Value"))
        {
            token.ThrowIfCancellationRequested();
            if (ParameterizationAnalysis.State(p, decisions.GetValueOrDefault(p.Id)) != ParameterReviewState.Accepted
                || !entities.TryGetValue(p.TargetId, out var entity)) continue;
            foreach (var link in entity.EvidenceLinks)
            {
                if (!raw.TryGetValue(link.SourceObservationSetRef, out var objects)
                    || !objects.TryGetValue(link.SourceObjectRef, out var origin) || Surface(origin, objects) is not { } owner) continue;
                var source = sources.Single(s => s.SourceObservationSetRef == link.SourceObservationSetRef);
                void Add(WorldObject target, string property, string prefix = "", string suffix = "")
                {
                    var expected = property == "Name" ? target.Name : Property(target, property == "Value" ? "value" : "automationId");
                    if (property == "Name" && Normalize(expected).Length <= prefix.Length + suffix.Length) return;
                    var approval = effective[p.Id];
                    rules.Add(new(approval.SourceProposalId, approval.SourceFingerprint, decisions[p.Id].ParameterName, p.TargetId,
                        source.SourceObservationSetRef, source.PayloadSha256, origin.WorldObjectId,
                        target.WorldObjectId, property, expected, prefix, suffix));
                    if (rules.Count > 10_000) throw new InvalidDataException("Parameter bindings exceed their limit; none were omitted.");
                    if (property == "Name" && p.ParameterName is "sheet_name" or "workbook_name" or "document_name" or "presentation_name"
                        && Property(target, "automationId") is { Length: > 0 } id)
                    {
                        var normalized = Normalize(expected);
                        if (Normalize(id) == normalized) Add(target, "AutomationId", prefix, suffix);
                        else if (Normalize(id) == normalized.Substring(prefix.Length, normalized.Length - prefix.Length - suffix.Length))
                            Add(target, "AutomationId");
                    }
                }
                if (p.PropertyName == "Value")
                {
                    Add(origin, "Value");
                    // Some providers expose the value as the accessible name. A
                    // stable label (e.g. Font Size) must never be wildcarded.
                    var value = Property(origin, "value");
                    if (value.Length > 0 && origin.Name == value) Add(origin, "Name");
                    continue;
                }
                var name = Normalize(origin.Name);
                var suffix = "";
                if (p.ParameterName is "workbook_name" or "document_name" or "presentation_name")
                {
                    var app = Path.GetFileNameWithoutExtension(revision.Target.ExecutableName).ToUpperInvariant();
                    var appSuffix = app switch { "EXCEL" => " - Excel", "WINWORD" => " - Word", "POWERPNT" => " - PowerPoint", _ => "" };
                    if (appSuffix.Length > 0 && name.EndsWith(appSuffix, StringComparison.OrdinalIgnoreCase)) suffix = name[^appSuffix.Length..];
                    foreach (var state in new[] { " [Read-Only]", " [Compatibility Mode]", " [Protected View]" })
                        if (suffix.Length > 0 && name[..^suffix.Length].EndsWith(state, StringComparison.OrdinalIgnoreCase))
                            suffix = name[^(suffix.Length + state.Length)..];
                }
                Add(origin, "Name", suffix: suffix);
                // Repeated document/sheet names in containment are the same
                // parameter only on this original surface. Commands and unrelated
                // labels are never included by substring or role alone.
                if (p.ParameterName is not ("workbook_name" or "document_name" or "presentation_name" or "sheet_name")
                    || decisions[p.Id].Scope != p.Scope) continue;
                var tokenValue = suffix.Length > 0 ? name[..^suffix.Length] : name;
                if (tokenValue.Length == 0) continue;
                foreach (var node in objects.Values.Where(o => o.WorldObjectId != origin.WorldObjectId
                    && (o.ObjectKind == WorldObjectKind.WindowObject || Role(o.Role) is "pane" or "custom" or "document" or "titlebar")
                    && Surface(o, objects) == owner))
                {
                    var label = Normalize(node.Name);
                    if (p.ParameterName == "sheet_name")
                    {
                        if (Role(node.Role) == "pane" && label == "Sheet " + tokenValue) Add(node, "Name", "Sheet ");
                    }
                    else if (label == name && (node.ObjectKind == WorldObjectKind.WindowObject
                        || Role(node.Role) == "titlebar" || suffix.Length > 0 && Role(node.Role) == "pane"))
                        Add(node, "Name", suffix: suffix);
                    else if (label == tokenValue && Role(node.Role) is "custom" or "document") Add(node, "Name");
                }
            }
        }
        // Several mapped controls may propose the same source property. Agreeing
        // rules converge; conflicting masks abstain instead of broadening scope.
        var blocked = current.Where(p => decisions.TryGetValue(p.Id, out var decision) &&
                ParameterizationAnalysis.State(p, decision) != ParameterReviewState.Accepted)
            .SelectMany(p => entities.TryGetValue(p.TargetId, out var entity) ? entity.EvidenceLinks.Select(l =>
                (l.SourceObservationSetRef, l.SourceObjectRef, p.PropertyName)) : [])
            .ToHashSet();
        var unique = rules.Where(r => !blocked.Contains((r.SourceObservationSetRef, r.WorldObjectRef, r.PropertyName == "AutomationId" ? "Name" : r.PropertyName))
                && !blocked.Contains((r.SourceObservationSetRef, r.OriginWorldObjectRef, r.PropertyName == "AutomationId" ? "Name" : r.PropertyName)))
            .GroupBy(r => (r.SourceObservationSetRef, r.WorldObjectRef, r.PropertyName))
            .Where(g => g.Select(r => (r.Prefix, r.Suffix, r.ExpectedValue)).Distinct().Count() == 1)
            .Select(g => g.OrderBy(r => r.ProposalId, StringComparer.Ordinal).First())
            .OrderBy(r => r.SourceObservationSetRef, StringComparer.Ordinal).ThenBy(r => r.WorldObjectRef, StringComparer.Ordinal)
            .ThenBy(r => r.PropertyName, StringComparer.Ordinal).ToArray();
        return new(1, revision.MapRevisionRef, revision.CanonicalPayloadHash, document.Revision,
            Hash(JsonSerializer.SerializeToUtf8Bytes(document)), unique);
    }

    private static string Normalize(string text) => string.Join(" ", new string(text
        .Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Role(string role) => role.Replace("ControlType.", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "").ToLowerInvariant();
    private static string Property(WorldObject node, string key) =>
        node.Properties.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
    private static string? Surface(WorldObject node, IReadOnlyDictionary<string, WorldObject> objects)
    {
        var seen = new HashSet<string>();
        while (seen.Count < 32 && seen.Add(node.WorldObjectId))
        {
            if (node.ObjectKind == WorldObjectKind.WindowObject) return node.WorldObjectId;
            if (!objects.TryGetValue(node.ParentWorldObjectId, out node!)) return null;
        }
        return null;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
