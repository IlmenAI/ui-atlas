using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V3;

namespace UIMaps.Protocol;

/// <summary>Experimental definition identity. Original nodes remain immutable evidence.</summary>
public static class LogicalControlIdentity
{
    public const string Policy = "logical-control-identity/1";
    public sealed record Part(WorldObject Original, string Role, string Type, string Class, string Name, string Locator, string Variable);
    public sealed record Description(Part[] Parts, string Signature, string SurfaceSignature)
    {
        public string DefinitionRef => Id(Signature);
        public string SurfaceRef => Id(SurfaceSignature);
    }
    public static string Property(WorldObject node, string key) => node.Properties.FirstOrDefault(p => p.Key == key)?.Value ?? "";
    public static string Normalize(string value) => string.Join(" ", value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    public static string Id(string signature) => "uimaps:logical:sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
    public static string Parent(WorldObject node) => node.ObjectKind != WorldObjectKind.WindowObject && Property(node, "sceneReconstruction").Length > 0
        ? Property(node, "sceneReportedParentRef") : node.ParentWorldObjectId;
    public static WorldObject[] Path(WorldObject node, IReadOnlyDictionary<string, WorldObject> objects)
    {
        var path = new List<WorldObject>(); var seen = new HashSet<string>();
        while (seen.Add(node.WorldObjectId) && path.Count < 128)
        {
            path.Add(node);
            if (node.ObjectKind == WorldObjectKind.WindowObject) { path.Reverse(); return path.ToArray(); }
            if (!objects.TryGetValue(Parent(node), out node!)) break;
        }
        return [];
    }
    public static Description Describe(string executable, IReadOnlyList<WorldObject> original, IEnumerable<ControlIdentityKnowledge>? knowledge = null,
        bool variableWindowTitle = true)
    {
        if (original.Count == 0) return new([], "", "");
        // UIA can expose an owned dialog below its main window even when native
        // topology begins at that dialog. The closest window owns the field.
        var start = 0;
        for (var i = 0; i < original.Count; i++)
            if (original[i].ObjectKind == WorldObjectKind.WindowObject || Normalize(Property(original[i], "controlType")) == "window") start = i;
        var parts = original.Skip(start).Select((node, i) =>
        {
            var role = Normalize(node.Role); var type = Normalize(Property(node, "controlType"));
            if (type.Length == 0) type = role;
            var cls = Normalize(Property(node, "className")); var id = Normalize(Property(node, "automationId"));
            if (id.StartsWith("native:", StringComparison.Ordinal)) id = "";
            var name = Normalize(node.Name); var variable = "";
            // Window titles are observed content too (dates, document names, etc.).
            // Keep the original title in evidence, not in surface/control identity.
            if (i == 0)
            {
                role = type = "window"; id = "";
                if (variableWindowTitle) { name = "{content}"; variable = "content"; }
            }
            // A field's contents may also be exposed as Name. A real locator is
            // required before removing that name; flags alone are insufficient.
            else if (id.Length > 0 && (type is "edit" or "textbox" or "combobox"
                || type == "document" && Property(node, "supportedPatterns").Contains("TextPattern", StringComparison.Ordinal)))
            { name = "{value}"; variable = "field-value"; }
            else if (id.Length > 0 && type is "text" or "static" && Property(node, "isContentElement").Equals("true", StringComparison.OrdinalIgnoreCase))
            { name = "{content}"; variable = "content"; }
            return new Part(node, role, type, cls, name, id, variable);
        }).ToArray();
        var anchors = parts.Select((p, i) => new IdentityAnchor(p.Original.Name, Property(p.Original, "className"),
            i == 0 ? "" : Property(p.Original, "automationId"), i == 0 ? "window" : p.Type)).ToArray();
        var changes = (knowledge ?? []).SelectMany(r => r.ChangesFor(executable, anchors)).GroupBy(c => c.Index);
        foreach (var group in changes)
        {
            var values = group.Select(c => c.Canonical).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length != 1) throw new InvalidDataException("Conflicting reviewed identity rules. Disable the obsolete correction before rebuilding.");
            var change = group.First(); var part = parts[group.Key];
            parts[group.Key] = part with { Name = Normalize(change.Canonical), Variable = change.Variable ? "reviewed-content" : "reviewed-stable" };
        }
        string Signature(IEnumerable<Part> nodes) => JsonSerializer.Serialize(new { policy = Policy,
            application = Normalize(WindowsExecutableIdentity.ProcessName(executable)),
            path = nodes.Select(n => new[] { n.Role, n.Type, n.Class, n.Name, n.Locator, n.Variable }) });
        return new(parts, Signature(parts), Signature(parts.Take(1)));
    }

    public static LogicalControlGroup[] Build(string executable, IEnumerable<MapEntity> entities,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, WorldObject>> sources, IEnumerable<ControlIdentityKnowledge>? knowledge = null)
    {
        var observations = new List<(MapEntity Entity, string Source, WorldObject Node, Description Identity)>();
        foreach (var entity in entities.Where(e => e.Applicable && e.EntityKind is MapEntityKind.MapControl or MapEntityKind.MapSurface))
            foreach (var link in entity.EvidenceLinks)
            {
                if (!sources.TryGetValue(link.SourceObservationSetRef, out var objects) || !objects.TryGetValue(link.SourceObjectRef, out var node)) continue;
                var path = Path(node, objects); if (path.Length == 0) continue;
                var identity = Describe(executable, path, knowledge);
                if (identity.Parts.Any(p => p.Class.Length == 0 && p.Name.Length == 0 && p.Locator.Length == 0)) continue;
                observations.Add((entity, link.SourceObservationSetRef, node, identity));
            }
        // One source containing distinct matching objects is positive evidence of
        // multiplicity. Never manufacture uniqueness by choosing a representative.
        var ambiguous = observations.GroupBy(o => (o.Source, o.Identity.Signature))
            .Where(g => g.Select(o => Instance(o.Node)).Distinct().Count() > 1).Select(g => g.Key.Signature).ToHashSet();
        var inconsistent = observations.GroupBy(o => o.Entity.MapEntityRef)
            .Where(g => g.Select(o => o.Identity.Signature).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();
        var layoutConflicts = observations.GroupBy(o => o.Identity.Signature).Where(g =>
        {
            var layouts = g.Select(o => RelativeNativeLayout(o.Identity)).Where(v => v is not null).Distinct().ToArray();
            return layouts.SelectMany((a, i) => layouts.Skip(i + 1).Select(b => (a, b)))
                .Any(pair => NativeControlLayout.Conflicts(pair.a!.Value.Box, pair.a.Value.Owner, pair.b!.Value.Box, pair.b.Value.Owner));
        }).Select(g => g.Key).ToHashSet();
        return observations.Where(o => !ambiguous.Contains(o.Identity.Signature) && !layoutConflicts.Contains(o.Identity.Signature) && !inconsistent.Contains(o.Entity.MapEntityRef))
            .GroupBy(o => o.Identity.Signature).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            {
                var first = g.OrderBy(o => o.Entity.MapEntityRef, StringComparer.Ordinal).First();
                var group = new LogicalControlGroup { DefinitionRef = first.Identity.DefinitionRef, StructuralSignature = g.Key,
                    SurfaceDefinitionRef = first.Identity.SurfaceRef, DisplayName = first.Entity.DisplayName, Role = first.Entity.Role };
                group.SupportingEntityRefs.AddRange(g.Select(o => o.Entity.MapEntityRef).Distinct().Order(StringComparer.Ordinal));
                group.ClassificationReasons.Add("Same canonical application, owning surface and control path; content and state retained in source observations.");
                group.VariablePropertyKeys.AddRange(first.Identity.Parts.Select((p, i) => (p, i)).Where(x => x.p.Variable.Length > 0).Select(x => $"path[{x.i}].Name:{x.p.Variable}"));
                return group;
            }).ToArray();
    }

    private static string Instance(WorldObject node)
    {
        var handle = Property(node, node.ObjectKind == WorldObjectKind.WindowObject ? "windowHandleEvidence" : "nativeWindowHandleEvidence");
        var runtime = Property(node, "runtimeId");
        if (node.ProcessInstanceEvidence.Length == 0) return node.WorldObjectId;
        if (long.TryParse(handle, out var native) && native > 0)
            return node.ProcessInstanceEvidence + "/hwnd:" + handle
                + (node.ObjectKind == WorldObjectKind.WindowObject || Normalize(Property(node, "controlType")) == "window" ? "" : "/runtime:" + runtime);
        return runtime.Length > 0 ? node.ProcessInstanceEvidence + "/runtime:" + runtime : node.WorldObjectId;
    }

    private static (RectPx Box, RectPx Owner)? RelativeNativeLayout(Description identity)
    {
        if (identity.Parts.Length < 2) return null;
        var leaf = identity.Parts[^1]; var box = leaf.Original.BoundsPx;
        var owner = identity.Parts[0].Original.BoundsPx;
        // Numeric Win32 IDs may be reused for a different field. Geometry is a
        // contradiction check within the form, never a sufficient identity anchor.
        if (leaf.Locator.Length == 0 || !leaf.Locator.All(char.IsAsciiDigit)
            || !Property(leaf.Original, "providerId").Equals("uia", StringComparison.OrdinalIgnoreCase)
            || box is not { Width: > 0, Height: > 0 } || owner is not { Width: > 0, Height: > 0 }) return null;
        return (box, owner);
    }
}
