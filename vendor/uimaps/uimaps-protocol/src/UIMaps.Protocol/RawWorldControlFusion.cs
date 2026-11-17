using System.Security.Cryptography;
using System.Text;
using UIMaps.Protocol.V1;

namespace UIMaps.Protocol;

/// <summary>Pure, capture-local reconstruction. Never consults Atlas or live UI.
/// Only direct native-control equivalence is supported by this policy. Container,
/// label and component relationships are deliberately not equivalence.</summary>
public static class RawWorldControlFusion
{
    public const string PolicyVersion = "raw-world-control-fusion/4";

    public static IEnumerable<WorldObject> Members(WorldObject value) =>
        value.Fusion is { Members.Count: > 0 } fusion ? fusion.Members : [value];

    public static WorldObject? Resolve(IEnumerable<WorldObject> scene, string originalRef) =>
        scene.SingleOrDefault(value => value.WorldObjectId == originalRef ||
            value.Fusion?.Members.Any(member => member.WorldObjectId == originalRef) == true);

    public static IReadOnlyList<WorldObject> Reconstruct(IReadOnlyList<WorldObject> input,
        CancellationToken cancellationToken = default)
    {
        // A published fused scene is immutable input. Do not nest memberships or
        // reinterpret a scene made under an unknown policy.
        if (input.Any(value => value.Fusion is not null))
        {
            if (input.Any(value => value.Fusion is { } f && f.PolicyVersion != PolicyVersion && f.PolicyVersion != "raw-world-control-fusion/1" && f.PolicyVersion != "raw-world-control-fusion/2" && f.PolicyVersion != "raw-world-control-fusion/3"))
                throw new InvalidOperationException("Unsupported Raw World fusion policy.");
            return input.Select(value => value.Clone()).ToArray();
        }
        var byId = input.ToDictionary(value => value.WorldObjectId, StringComparer.Ordinal);
        var replacement = new Dictionary<string, string>(StringComparer.Ordinal);
        var fused = new Dictionary<string, WorldObject>(StringComparer.Ordinal);
        foreach (var group in input.Where(Eligible).GroupBy(value =>
            (value.ProcessInstanceEvidence, Handle: Property(value, "nativeWindowHandleEvidence"))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var members = group.OrderBy(value => value.WorldObjectId, StringComparer.Ordinal).ToArray();
            if (members.Length < 2 || !members.Any(value => Property(value, "providerId") == "uia")) continue;
            var owners = members.Select(value => OwnerHandle(value, byId)).Distinct(StringComparer.Ordinal).ToArray();
            // A native host handle is not an identity for all of its virtual children.
            // Require agreement of direct control class/type, owner and acquisition.
            if (owners.Length != 1 || owners[0].Length == 0 || owners[0] == group.Key.Handle
                || members.Select(value => Property(value, "className")).Distinct(StringComparer.Ordinal).Count() != 1
                || members.Select(PhysicalType).Distinct(StringComparer.Ordinal).Count() != 1
                || members.Where(value => Property(value, "providerId") == "uia")
                    .Select(value => Property(value, "runtimeId")).Distinct(StringComparer.Ordinal).Count() != 1
                || members.Any(value => Property(value, "runtimeId").Length == 0)
                || members.Any(a => members.Any(b => !Compatible(a, b)))) continue;
            // This ID belongs to this observation only, not to Atlas or a process
            // restart. Source refs make replay independent of enumeration order.
            var id = "fused:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                PolicyVersion + "|" + string.Join("|", members.Select(value => value.WorldObjectId))))).ToLowerInvariant();
            var primary = members.Where(value => Property(value, "providerId") == "uia")
                .OrderByDescending(value => Property(value, "sceneVisibility") == "visible")
                .ThenBy(value => value.WorldObjectId, StringComparer.Ordinal).First();
            var result = primary.Clone();
            result.WorldObjectId = id;
            result.TrackId = "";
            result.Fusion = new ControlFusion { PolicyVersion = PolicyVersion };
            result.Fusion.Members.AddRange(members.Select(value => value.Clone()));
            result.Fusion.Reasons.Add("Same direct native control handle, process lifetime, owning surface, control class/type, unique UIA runtime identity, overlapping source intervals and geometry. A separate Win32 report is optional.");
            result.Lineage.ObservationIds.Clear();
            result.Lineage.ObservationIds.AddRange(members.SelectMany(value => value.Lineage.ObservationIds).Distinct().Order());
            result.Lineage.SourcePacketIds.Clear();
            result.Lineage.SourcePacketIds.AddRange(members.SelectMany(value => value.Lineage.SourcePacketIds).Distinct().Order());
            fused.Add(id, result);
            foreach (var member in members) replacement.Add(member.WorldObjectId, id);
        }
        // MSAA may expose the window wrapper of a native leaf control. A
        // wrapper has no UIA role/class; do not treat that as a competing button.
        // Accept only reciprocal, unique correspondence to an already proven
        // native/UIA control, within the same process, owner and acquisition.
        var msaaCandidates = input.Where(v => Property(v, "providerId") == "msaa"
            && Property(v, "nativeControlIdEvidence") == "0"
            && Property(v, "controlType") is "Window" or "Button" or "Edit" or "CheckBox" or "RadioButton")
            .Select(v => (Node: v, Matches: fused.Values.Where(c => MsaaEquivalent(v, c, byId)).ToArray()))
            .Where(pair => pair.Matches.Length == 1).ToArray();
        foreach (var group in msaaCandidates.GroupBy(pair => pair.Matches[0].WorldObjectId))
        {
            // Duplicate provider reports need a direct identity, not an arbitrary
            // choice among overlapping accessible objects.
            if (group.Count() != 1) continue;
            var member = group.Single().Node; var control = fused[group.Key];
            control.Fusion.Members.Add(member.Clone());
            control.Fusion.Reasons.Add(Property(member, "nativeWindowHandleEvidence").Length > 0
                ? "MSAA self object agrees on direct native handle, owner, process, interval and geometry."
                : "MSAA native leaf wrapper: reciprocal unique caption/geometry correspondence, same process lifetime and native owning surface, overlapping acquisition; no conflicting direct handle or actionable descendant.");
            control.Lineage.ObservationIds.AddRange(member.Lineage.ObservationIds.Except(control.Lineage.ObservationIds));
            control.Lineage.SourcePacketIds.AddRange(member.Lineage.SourcePacketIds.Except(control.Lineage.SourcePacketIds));
            replacement.Add(member.WorldObjectId, control.WorldObjectId);
        }
        foreach (var control in fused.Values)
        {
            var ordered = control.Fusion.Members.OrderBy(v => v.WorldObjectId, StringComparer.Ordinal).ToArray();
            control.Fusion.Members.Clear(); control.Fusion.Members.AddRange(ordered);
            var id = "fused:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                PolicyVersion + "|" + string.Join("|", ordered.Select(v => v.WorldObjectId))))).ToLowerInvariant();
            control.WorldObjectId = id;
            foreach (var member in ordered) replacement[member.WorldObjectId] = id;
        }
        var output = input.Where(value => !replacement.ContainsKey(value.WorldObjectId)).Select(value => value.Clone())
            .Concat(fused.Values).ToArray();
        foreach (var value in output)
        {
            value.ParentWorldObjectId = replacement.GetValueOrDefault(value.ParentWorldObjectId, value.ParentWorldObjectId);
            foreach (var property in value.Properties.Where(property => property.Key is "sceneReportedParentRef" or "sceneOwnerSurfaceRef"))
                property.Value = replacement.GetValueOrDefault(property.Value, property.Value);
        }
        return output;
    }

    private static bool Eligible(WorldObject value) => value.ObjectKind == WorldObjectKind.AccessibilityElement
        && value.ProcessInstanceEvidence.Length > 0
        && long.TryParse(Property(value, "nativeWindowHandleEvidence"), out var handle) && handle > 0
        && Property(value, "providerId") is "native-hwnd" or "uia"
        && PhysicalType(value) is "Button" or "Edit" or "CheckBox" or "RadioButton"
        && Property(value, "className").Length > 0;

    // A native Win32 edit may expose UIA TextPattern as Document rather than
    // Edit. The direct HWND/runtime/owner/interval/geometry checks still apply;
    // arbitrary document panes never become native edits.
    private static string PhysicalType(WorldObject value) => Property(value, "providerId") == "uia"
        && Property(value, "controlType") == "Document" && Property(value, "className") == "Edit"
        && Property(value, "automationId").Length > 0
        && Property(value, "supportedPatterns").Split('|').Contains("TextPatternIdentifiers.Pattern", StringComparer.Ordinal)
        ? "Edit" : Property(value, "controlType");

    private static bool MsaaEquivalent(WorldObject msaa, WorldObject control, IReadOnlyDictionary<string, WorldObject> objects)
    {
        var handle = Property(msaa, "nativeWindowHandleEvidence");
        // A ROLE_SYSTEM_WINDOW wrapper can carry the leaf's own HWND. Its
        // owning surface is above that wrapper, just as for the native/UIA leaf.
        var owner = objects.TryGetValue(msaa.ParentWorldObjectId, out var parent) ? OwnerHandle(parent, objects) : "";
        if (msaa.ProcessInstanceEvidence != control.ProcessInstanceEvidence
            || owner.Length == 0 || owner != OwnerHandle(control, objects)
            || !control.Fusion.Members.All(member => Compatible(msaa, member))
            || handle.Length > 0 && handle != Property(control, "nativeWindowHandleEvidence")) return false;
        var type = Property(msaa, "controlType");
        if (type != "Window" && type != Property(control, "controlType")) return false;
        if (handle.Length > 0) return true;
        // Legacy captures lack WindowFromAccessibleObject. Their wrapper can
        // still be associated, but only with these independent corroborators.
        if (type != "Window" || msaa.Name.Length == 0 || msaa.Name != control.Name) return false;
        return !objects.Values.Any(child => child.ParentWorldObjectId == msaa.WorldObjectId
            && Property(child, "controlType") is "Button" or "Edit" or "CheckBox" or "RadioButton" or "DataGrid" or "List");
    }

    private static bool Compatible(WorldObject a, WorldObject b)
    {
        var first = a.Freshness?.SourceInterval; var second = b.Freshness?.SourceInterval;
        if (first is null || second is null || first.EndedAtMonotonicTicks < first.StartedAtMonotonicTicks
            || second.EndedAtMonotonicTicks < second.StartedAtMonotonicTicks
            || first.EndedAtMonotonicTicks < second.StartedAtMonotonicTicks
            || second.EndedAtMonotonicTicks < first.StartedAtMonotonicTicks) return false;
        var x = a.BoundsPx; var y = b.BoundsPx;
        return x is { Width: > 0, Height: > 0 } && y is { Width: > 0, Height: > 0 }
            && Math.Abs((long)x.X - y.X) <= 2 && Math.Abs((long)x.Y - y.Y) <= 2
            && Math.Abs((long)x.Width - y.Width) <= 2 && Math.Abs((long)x.Height - y.Height) <= 2;
    }

    private static string OwnerHandle(WorldObject value, IReadOnlyDictionary<string, WorldObject> objects)
    {
        var seen = new HashSet<string>();
        // Closest native surface, including a UIA window nested under a root
        // capture. Recorded root scope alone is not the owning dialog.
        while (seen.Add(value.WorldObjectId))
        {
            if (value.ObjectKind == WorldObjectKind.WindowObject) return Property(value, "windowHandleEvidence");
            if (Property(value, "controlType") == "Window" && Property(value, "nativeWindowHandleEvidence") is { Length: > 0 } handle)
                return handle;
            if (!objects.TryGetValue(value.ParentWorldObjectId, out value!)) break;
        }
        return "";
    }

    private static string Property(WorldObject value, string name) => value.Properties.FirstOrDefault(p => p.Key == name)?.Value ?? "";
}
