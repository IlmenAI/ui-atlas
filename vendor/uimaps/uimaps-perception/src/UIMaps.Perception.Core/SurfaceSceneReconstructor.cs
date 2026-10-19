using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UIMaps.Protocol.V1;

namespace UIMaps.Perception.Core;

/// <summary>Derives observation-scoped surface relationships without changing sensor evidence.
/// History is used only to recognize stable ownership; it never supplies missing scene content.</summary>
public sealed class SurfaceSceneReconstructor
{
    public const string Version = "surface-scene/2";
    private Dictionary<string, List<RectPx>> _previous = new(StringComparer.Ordinal);
    private string _clock = string.Empty;

    public void Apply(SensorEpisodeBundle bundle, IReadOnlyList<WorldObject> objects)
    {
        OfficeMsaaGeometry.Apply(bundle, objects);
        if (_clock != bundle.ClockMetadata.ClockId) _previous.Clear();
        _clock = bundle.ClockMetadata.ClockId;
        var byId = objects.ToDictionary(value => value.WorldObjectId, StringComparer.Ordinal);
        var originalParents = objects.ToDictionary(value => value.WorldObjectId,
            value => Property(value, "sceneReportedParentRef") is { Length: > 0 } parent ? parent : value.ParentWorldObjectId, StringComparer.Ordinal);
        var windows = objects.Where(value => value.ObjectKind == WorldObjectKind.WindowObject).ToArray();
        var topology = bundle.SensorPackets.Where(packet => packet.WindowTopology is not null)
            .SelectMany(packet => packet.WindowTopology.Windows).GroupBy(window => window.WindowHandleEvidence)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var sourceNodes = bundle.SensorPackets.Where(packet => packet.Accessibility is not null)
            .SelectMany(packet => AccessibilityEvidence.Nodes(packet.Accessibility)).GroupBy(node => node.AccessibilityEvidenceId)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var ownerById = new Dictionary<string, WorldObject?>(StringComparer.Ordinal);
        var presentedById = new Dictionary<string, WorldObject>(StringComparer.Ordinal);
        var nextHistory = new Dictionary<string, List<RectPx>>(StringComparer.Ordinal);
        foreach (var window in windows)
        {
            topology.TryGetValue(Property(window, "windowHandleEvidence"), out var native);
            var tooltipRoot = sourceNodes.Values.Any(node => IsTooltip(node.ControlType, node.ClassName) &&
                node.NativeWindowHandleEvidence == native?.WindowHandleEvidence && Contains(window.BoundsPx, node.BoundsPx, 4));
            Set(window, "surfaceClass", tooltipRoot ? "RawTooltipWindow" : Classify(native));
            Set(window, "sceneVisible", (native is { IsVisible: true, IsMinimized: false, IsCloakedOrUnavailable: false }
                && Valid(window.BoundsPx)).ToString());
            Set(window, "sceneObservation", bundle.EpisodeId);
            Set(window, "sceneAlignment", bundle.ObservationConsistency?.Status ?? "unverified");
            Set(window, "sceneReconstruction", Version);
        }
        WorldObject? ReportedOwner(WorldObject value)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (seen.Add(value.WorldObjectId))
            {
                if (value.ObjectKind == WorldObjectKind.WindowObject) return value;
                if (!originalParents.TryGetValue(value.WorldObjectId, out var parent) || !byId.TryGetValue(parent, out value!)) return null;
            }
            return null;
        }
        IEnumerable<WorldObject> Ancestry(WorldObject value)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (value.ObjectKind != WorldObjectKind.WindowObject && seen.Add(value.WorldObjectId))
            {
                yield return value;
                if (!originalParents.TryGetValue(value.WorldObjectId, out var parent) || !byId.TryGetValue(parent, out value!)) yield break;
            }
        }
        var controls = objects.Where(value => value.ObjectKind is WorldObjectKind.AccessibilityElement or WorldObjectKind.VisualText).ToArray();
        foreach (var control in controls)
        {
            // Old retained publications can be reconstructed with the same algorithm.
            // Enrich identity from their sealed episode, never from another frame.
            if (sourceNodes.TryGetValue(Property(control, "sourceAccessibilityEvidenceId"), out var source))
            {
                Set(control, "runtimeIdentityEvidence", string.Join(",", source.RuntimeIdEvidence));
                Set(control, "reportedWindowEvidenceId", source.WindowEvidenceId);
                Set(control, "reportedParentEvidenceId", source.ParentEvidenceId);
            }
            var reported = ReportedOwner(control);
            var owner = reported;
            Clear(control, "scenePresentedOn");
            Set(control, "sceneReconstruction", Version);
            Set(control, "sceneReportedSurfaceRef", reported?.WorldObjectId ?? string.Empty);
            Set(control, "sceneReportedParentRef", originalParents[control.WorldObjectId]);
            Set(control, "sceneOwnerReason", reported is null ? "unresolved-source-owner" : "reported-owner");
            var ancestry = Ancestry(control).ToArray();
            var popupRoots = ancestry.Where(root => IsPopupBoundary(root) ||
                Property(root, "providerId") == "msaa" && Property(root, "controlType") == "Window" &&
                windows.Any(window => IsPopup(window) && SameProcess(root, window) &&
                    Contains(window.BoundsPx, root.BoundsPx, 2) && Contains(root.BoundsPx, window.BoundsPx, 2))).ToArray();
            var hidden = ancestry.Any(value => Property(value, "isOffscreen").Equals("True", StringComparison.OrdinalIgnoreCase));
            Set(control, "sceneOffscreen", hidden.ToString());
            if (reported is not null && popupRoots.Length > 0)
            {
                var candidates = windows.Where(window => IsPopup(window) && IsVisible(window) &&
                    SameProcess(reported, window) && CompatibleOwner(reported, window) &&
                    popupRoots.Any(root => Contains(window.BoundsPx, root.BoundsPx, 4) ||
                        Contains(window.BoundsPx, control.BoundsPx, 2) && Intersect(root.BoundsPx, window.BoundsPx) is not null))
                    .OrderBy(window => Area(window.BoundsPx)).ToArray();
                // Equal geometric candidates are ambiguous. Never use enumeration order as ownership.
                if (candidates.Length > 0 && candidates[0].WorldObjectId != reported.WorldObjectId &&
                    (candidates.Length == 1 || Area(candidates[0].BoundsPx) < Area(candidates[1].BoundsPx)))
                {
                    var popup = candidates[0];
                    var key = ContinuityKey(control, reported);
                    var stable = key is not null && _previous.TryGetValue(key, out var earlier) && earlier.Any(bounds => Continuous(bounds, control.BoundsPx));
                    if (stable)
                    {
                        presentedById[control.WorldObjectId] = popup;
                        Set(control, "scenePresentedOn", popup.WorldObjectId);
                        Set(control, "sceneOwnerReason", "stable-owner-with-popup-presentation");
                    }
                    else
                    {
                        owner = popup;
                        Set(control, "sceneOwnerReason", "same-observation-popup-ancestry-and-native-bounds");
                    }
                    Set(control, "sceneRelationshipConfidence", "inferred");
                }
                else if (candidates.Length > 1 && Area(candidates[0].BoundsPx) == Area(candidates[1].BoundsPx))
                {
                    Set(control, "sceneOwnerReason", "ambiguous-popup-candidates; reported-owner-preserved");
                    Set(control, "sceneRelationshipConfidence", "unresolved");
                }
            }
            ownerById[control.WorldObjectId] = owner;
            Set(control, "sceneOwnerSurfaceRef", owner?.WorldObjectId ?? string.Empty);
            if (owner is not null && !IsPopup(owner) && (popupRoots.Length == 0 || presentedById.ContainsKey(control.WorldObjectId)) &&
                !hidden && ContinuityKey(control, owner) is { } continuity)
            {
                if (!nextHistory.TryGetValue(continuity, out var bounds)) nextHistory[continuity] = bounds = [];
                bounds.Add(control.BoundsPx.Clone());
            }
        }
        foreach (var control in controls)
        {
            var owner = ownerById[control.WorldObjectId];
            if (owner is null)
            {
                Set(control, "sceneVisibility", "unresolved");
                continue;
            }
            // Preserve hierarchy inside the reconstructed surface; cut only the edge crossing its boundary.
            var parent = originalParents[control.WorldObjectId];
            control.ParentWorldObjectId = ownerById.TryGetValue(parent, out var parentOwner) && parentOwner?.WorldObjectId == owner.WorldObjectId
                ? parent : owner.WorldObjectId;
            control.WindowScopeRef = owner.WindowScopeRef;
            var display = presentedById.GetValueOrDefault(control.WorldObjectId) ?? owner;
            var visible = Intersect(control.BoundsPx, display.BoundsPx);
            if (visible is not null) visible = Intersect(visible, bundle.DisplayDescriptor.BoundsPx);
            var regions = visible is null || Property(control, "sceneOffscreen") == "True" || !IsVisible(display)
                ? new List<RectPx>() : [visible];
            var initialArea = regions.Sum(Area);
            var covering = windows.Where(window => window.WorldObjectId != display.WorldObjectId && IsVisible(window) &&
                InFrontOf(window, display) && Intersect(window.BoundsPx, control.BoundsPx) is not null).ToArray();
            Clear(control, "sceneOccludedBy");
            Clear(control, "sceneVisibleRegion");
            foreach (var cover in covering)
            {
                if (!regions.Any(region => Intersect(region, cover.BoundsPx) is not null)) continue;
                control.Properties.Add(new StringProperty { Key = "sceneOccludedBy", Value = cover.WorldObjectId });
                regions = regions.SelectMany(region => Subtract(region, cover.BoundsPx)).ToList();
            }
            foreach (var region in regions) control.Properties.Add(new StringProperty { Key = "sceneVisibleRegion", Value = Encode(region) });
            Set(control, "sceneVisibility", initialArea == 0 ? "offscreen-or-unavailable" : regions.Count == 0 ? "occluded" :
                regions.Sum(Area) < Area(control.BoundsPx) ? "partially-visible" : "visible");
        }
        foreach (var window in windows)
        {
            var members = controls.Where(control => ownerById[control.WorldObjectId]?.WorldObjectId == window.WorldObjectId ||
                presentedById.GetValueOrDefault(control.WorldObjectId)?.WorldObjectId == window.WorldObjectId).ToArray();
            var scope = window.WindowScopeRef.Length > 0 ? window.WindowScopeRef : window.ProcessInstanceEvidence + "|" + Property(window, "windowHandleEvidence");
            var key = scope;
            if (IsPopup(window))
            {
                // Content/layout identify an observation variant, not a named surface family.
                var signatures = members.Where(control => Property(control, "sceneOffscreen") != "True" && Valid(control.BoundsPx))
                    .Select(control => string.Join("|", control.Role, control.Name, Property(control, "automationId"),
                        (control.BoundsPx.X - window.BoundsPx.X) / 4, (control.BoundsPx.Y - window.BoundsPx.Y) / 4,
                        control.BoundsPx.Width / 4, control.BoundsPx.Height / 4)).Distinct().Order(StringComparer.Ordinal);
                var fingerprint = Hash(string.Join("\n", signatures));
                Set(window, "sceneContentFingerprint", fingerprint);
                topology.TryGetValue(Property(window, "windowHandleEvidence"), out var native);
                var rootNames = members.Where(control => !string.IsNullOrWhiteSpace(control.Name) &&
                        Property(control, "nativeWindowHandleEvidence") == Property(window, "windowHandleEvidence") &&
                        Contains(window.BoundsPx, control.BoundsPx, 2) && Contains(control.BoundsPx, window.BoundsPx, 2))
                    .Select(control => control.Name.Trim()).Distinct(StringComparer.Ordinal).ToArray();
                var title = !string.IsNullOrWhiteSpace(native?.Title) ? native.Title.Trim() : rootNames.Length == 1 ? rootNames[0] : "";
                var family = title.Length > 0 ? title : fingerprint;
                Set(window, "sceneFamilyEvidence", !string.IsNullOrWhiteSpace(native?.Title) ? "native-title-and-owner" :
                    rootNames.Length == 1 ? "native-accessibility-root-and-owner" : "content-fallback");
                key = string.Join("|", window.ProcessInstanceEvidence, Property(window, "surfaceClass"), Property(window, "className"),
                    Property(window, "ownerWindowHandleEvidence"), family);
                // Two simultaneous identical popups remain separate physical instances.
                if (windows.Count(other => IsPopup(other) && SameProcess(other, window) && other.BoundsPx.Equals(window.BoundsPx)) > 1)
                    key += "|" + Property(window, "windowHandleEvidence");
                var namedRoot = members.Where(IsPopupBoundary).Where(control => !string.IsNullOrWhiteSpace(control.Name))
                    .OrderBy(control => Math.Abs(Area(control.BoundsPx) - Area(window.BoundsPx))).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(window.Name) && namedRoot is not null) window.Name = namedRoot.Name;
            }
            if (IsPopup(window) && (window.ProcessInstanceEvidence.Length == 0 || Property(window, "windowHandleEvidence").Length == 0))
                key += "|observation:" + bundle.EpisodeId + "|" + window.WorldObjectId;
            Set(window, "sceneSurfaceKey", Hash(key));
        }
        foreach (var collision in windows.GroupBy(window => Property(window, "sceneSurfaceKey")).Where(group => group.Count() > 1))
            foreach (var window in collision) Set(window, "sceneSurfaceKey", Hash(collision.Key + "|" + Property(window, "windowHandleEvidence")));
        foreach (var control in controls)
            if (ownerById[control.WorldObjectId] is { } owner)
                Set(control, "sceneOwnerSurfaceKey", Property(owner, "sceneSurfaceKey"));
        _previous = nextHistory;
    }

    public static string Property(WorldObject value, string key) => value.Properties.FirstOrDefault(property => property.Key == key)?.Value ?? string.Empty;
    private static void Clear(WorldObject value, string key)
    {
        for (var index = value.Properties.Count - 1; index >= 0; index--) if (value.Properties[index].Key == key) value.Properties.RemoveAt(index);
    }
    private static void Set(WorldObject value, string key, string text)
    {
        Clear(value, key); value.Properties.Add(new StringProperty { Key = key, Value = text });
    }
    private static string Classify(WindowObservationRecord? window)
    {
        if (window is null) return "RawWindow";
        if (IsTooltip("", window.ClassName)) return "RawTooltipWindow";
        if (window.ClassName.Equals("#32770", StringComparison.OrdinalIgnoreCase) || window.ClassName.StartsWith("bosa_", StringComparison.OrdinalIgnoreCase) ||
            window.ClassName.Equals("NUIDialog", StringComparison.OrdinalIgnoreCase)) return "RawDialogWindow";
        var framed = (window.WindowStyle & 0x00C40000) != 0;
        var tool = window.IsToolWindow || (window.WindowExStyle & 0x80) != 0 || window.ClassName.Contains("tool window", StringComparison.OrdinalIgnoreCase);
        return tool ? framed ? "RawToolWindow" : "RawPopupWindow" : !framed && (window.WindowStyle & 0x80000000) != 0 ? "RawPopupWindow" : "RawWindow";
    }
    private static bool IsPopup(WorldObject value) => Property(value, "surfaceClass") is "RawPopupWindow" or "RawTooltipWindow";
    private static bool IsTooltip(string role, string cls) => role.Replace("ControlType.", "", StringComparison.Ordinal).Equals("ToolTip", StringComparison.OrdinalIgnoreCase) ||
        cls.Equals("OfficeTooltip", StringComparison.OrdinalIgnoreCase) || cls.Equals("tooltips_class32", StringComparison.OrdinalIgnoreCase) ||
        cls.Equals("NetUITooltip", StringComparison.OrdinalIgnoreCase);
    private static bool IsVisible(WorldObject value) => Property(value, "sceneVisible") == "True";
    private static bool SameProcess(WorldObject a, WorldObject b) => a.ProcessInstanceEvidence.Length > 0 && a.ProcessInstanceEvidence == b.ProcessInstanceEvidence;
    private static bool CompatibleOwner(WorldObject owner, WorldObject popup)
    {
        var nativeOwner = Property(popup, "ownerWindowHandleEvidence");
        return nativeOwner.Length == 0 || nativeOwner == Property(owner, "windowHandleEvidence") ||
            Property(popup, "rootOwnerWindowHandleEvidence") == Property(owner, "windowHandleEvidence");
    }
    private static bool IsPopupBoundary(WorldObject value)
    {
        var cls = Property(value, "className").Replace(" ", "").ToLowerInvariant();
        return IsTooltip(Property(value, "controlType"), Property(value, "className")) ||
            cls.Contains("popup") || cls.Contains("flyout") || cls.Contains("dropdown") || cls is "netuitoolwindow" or "netuigallerycontainer" or "netuitwmenuitemgroup" or "netuitwbtnmenuitem" or "menucontainer" or "gallerycontainer" ||
            Property(value, "controlType") == "Menu";
    }
    private static string? ContinuityKey(WorldObject control, WorldObject owner)
    {
        var identity = Property(control, "runtimeIdentityEvidence");
        if (identity.Length == 0) identity = Property(control, "automationId");
        if (identity.Length == 0 || owner.WindowScopeRef.Length == 0) return null;
        return string.Join("|", owner.WindowScopeRef, Property(control, "providerId"), identity, control.Role, control.Name);
    }
    private static bool Continuous(RectPx a, RectPx b) => Valid(a) && Valid(b) &&
        Math.Abs((long)a.X - b.X) <= 12 && Math.Abs((long)a.Y - b.Y) <= 12 && Math.Abs((long)a.Width - b.Width) <= 8 && Math.Abs((long)a.Height - b.Height) <= 8;
    private static bool InFrontOf(WorldObject a, WorldObject b) => int.TryParse(Property(a, "zOrder"), out var first) &&
        int.TryParse(Property(b, "zOrder"), out var second) && first < second;
    private static bool Valid(RectPx bounds) => bounds.Width > 0 && bounds.Height > 0;
    private static long Area(RectPx bounds) => Valid(bounds) ? (long)bounds.Width * bounds.Height : 0;
    private static bool Contains(RectPx outer, RectPx inner, int tolerance) => Valid(outer) && Valid(inner) &&
        (long)inner.X >= (long)outer.X - tolerance && (long)inner.Y >= (long)outer.Y - tolerance &&
        (long)inner.X + inner.Width <= (long)outer.X + outer.Width + tolerance && (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height + tolerance;
    private static RectPx? Intersect(RectPx a, RectPx b)
    {
        if (!Valid(a) || !Valid(b)) return null;
        var x = Math.Max((long)a.X, b.X); var y = Math.Max((long)a.Y, b.Y);
        var right = Math.Min((long)a.X + a.Width, (long)b.X + b.Width); var bottom = Math.Min((long)a.Y + a.Height, (long)b.Y + b.Height);
        return right <= x || bottom <= y ? null : new() { X = (int)x, Y = (int)y, Width = (int)(right - x), Height = (int)(bottom - y) };
    }
    private static IEnumerable<RectPx> Subtract(RectPx source, RectPx cover)
    {
        var hit = Intersect(source, cover);
        if (hit is null) { yield return source; yield break; }
        var candidates = new[] {
            new RectPx { X = source.X, Y = source.Y, Width = source.Width, Height = hit.Y - source.Y },
            new RectPx { X = source.X, Y = hit.Y + hit.Height, Width = source.Width, Height = (source.Y + source.Height) - (hit.Y + hit.Height) },
            new RectPx { X = source.X, Y = hit.Y, Width = hit.X - source.X, Height = hit.Height },
            new RectPx { X = hit.X + hit.Width, Y = hit.Y, Width = (source.X + source.Width) - (hit.X + hit.Width), Height = hit.Height } };
        foreach (var region in candidates.Where(Valid)) yield return region;
    }
    private static string Encode(RectPx rect) => string.Join(",", new[] { rect.X, rect.Y, rect.Width, rect.Height }.Select(value => value.ToString(CultureInfo.InvariantCulture)));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
