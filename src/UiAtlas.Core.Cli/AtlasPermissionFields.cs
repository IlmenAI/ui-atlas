using Google.Protobuf;
using UiAtlas.Catalog;
using UiAtlas.Core.Mapping.Atlas;
using UIMaps.Perception.Core;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

internal static class AtlasPermissionFields
{
    // Captions from retained accessibility trees can contain record values. Only fixed
    // field vocabulary may supply a local caption without a reviewed skill witness.
    private static readonly HashSet<string> Captions = new(StringComparer.OrdinalIgnoreCase)
        { "Name", "Guest", "Guest name", "Guest card", "From", "To", "Arrival", "Departure", "Adults", "Children", "Room", "Search" };

    internal static CatalogFieldCapabilities Read(AtlasMapWorkspace workspace, UiMapRevision revision, string map, CancellationToken token)
    {
        var hosts = revision.Entities.Where(g => g.HostedDataGrid != null).Select(g => g.HostedDataGrid.HostControlRef).ToHashSet();
        var fields = revision.Entities.Where(e => e.Applicable && e.EntityKind == MapEntityKind.MapControl &&
                (e.Matchers.Any(m => m.Key == "className" && m.ExpectedValue == "ProBrowser") ||
                 hosts.Contains(e.MapEntityRef) ||
                 e.Role.ToLowerInvariant() is "edit" or "datagrid" or "data grid" or "table" or "list" ||
                 e.Matchers.Any(m => m.Key == "controlType" && m.ExpectedValue.Replace("ControlType.", "") is "Edit" or "DataGrid" or "Table" or "List")))
            .SelectMany(e => e.EvidenceLinks.Select(l => (Id: e.MapEntityRef, Link: l))).ToLookup(x => x.Link.SourceObservationSetRef);
        var pins = revision.Sources.ToDictionary(s => s.SourceObservationSetRef);
        var sources = workspace.Index.Sources.Where(s => pins.ContainsKey(s.SourceRef) && fields.Contains(s.SourceRef)).ToArray();
        if (fields.Select(g => g.Key).Except(sources.Select(s => s.SourceRef), StringComparer.Ordinal).Any())
            throw new InvalidDataException("A recording needed to verify field capabilities is missing from this map version.");
        var found = new List<CatalogFieldCapability>();
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            var pin = pins[source.SourceRef];
            var publication = GetPublicationResponse.Parser.ParseFrom(File.ReadAllBytes(workspace.Resolve(source.PublicationFile)));
            var episode = SensorEpisodeBundle.Parser.ParseFrom(File.ReadAllBytes(workspace.Resolve(source.EpisodeFile)));
            IMessage? payload = (IMessage?)publication.Snapshot?.WorldStateSnapshot ?? publication.Survey;
            if (episode.EpisodeId != pin.EpisodeId || payload == null || AtlasEpisodeAdapter.Hash(payload.ToByteArray()) != pin.SourcePayloadHash)
                throw new InvalidDataException("The field evidence differs from the saved map revision.");
            new PacketValidator(PerceptionPolicy.Default).ValidateRecorded(episode);
            var objects = publication.Snapshot?.WorldStateSnapshot.WorldObjects.ToArray() ?? publication.Survey.WorldObjects.Select(x => x.WorldObject).ToArray();
            var world = objects.ToDictionary(x => x.WorldObjectId);
            var matched = new HashSet<(string Id, string Object)>();
            foreach (var packet in episode.SensorPackets.Select(p => p.Accessibility).OfType<AccessibilityPacket>())
            {
                var nodes = (packet.RawProviderResponses.Count == 0 ? packet.Nodes.AsEnumerable() :
                    packet.RawProviderResponses.SelectMany(r => r.Packet.Nodes)).Where(n => n.ProviderId == "uia").ToArray();
                var byEvidence = nodes.Where(n => n.AccessibilityEvidenceId.Length > 0).ToLookup(n => n.AccessibilityEvidenceId);
                var children = nodes.ToLookup(n => (n.WindowEvidenceId, n.ParentEvidenceId));
                var labels = nodes.Where(n => !n.IsOffscreen && !n.IsPassword && n.BoundsPx != null &&
                    (n.ControlType == "Text" || n.HasIsReadOnly && n.IsReadOnly) && n.Name.TrimEnd().EndsWith(':') &&
                    Captions.Contains(n.Name.Trim().TrimEnd(':').Trim())).ToLookup(n => (n.WindowEvidenceId, n.ParentEvidenceId));
                foreach (var (id, link) in fields[source.SourceRef])
                {
                    if (!world.TryGetValue(link.SourceObjectRef, out var obj)) continue;
                    foreach (var member in UIMaps.Protocol.RawWorldControlFusion.Members(obj).Prepend(obj))
                    foreach (var node in byEvidence[SurfaceSceneReconstructor.Property(member, "sourceAccessibilityEvidenceId")])
                    {
                        if (node.ControlType != "Edit")
                        {
                            if (node.ControlType is "Pane" or "DataGrid" or "List" or "Table")
                            {
                                var selectable = node.IsEnabled && !node.IsOffscreen &&
                                    children[(node.WindowEvidenceId, node.AccessibilityEvidenceId)].Any(n => n.IsEnabled && !n.IsOffscreen &&
                                        (n.ControlType is "DataItem" or "ListItem") && n.SupportedPatterns.Contains("SelectionItemPatternIdentifiers.Pattern"));
                                matched.Add((id, link.SourceObjectRef));
                                var grid = (node.ControlType is "DataGrid" or "Table") && node.SupportedPatterns.Any(SavedDataGridCounts.IsGridPattern);
                                found.Add(new(id, selectable ? "row-selection" : grid ? "grid-host" : "unknown", ""));
                            }
                            continue;
                        }
                        var access = node.IsPassword || node.HasIsReadOnly && node.IsReadOnly ? "read-only" :
                            node.HasIsReadOnly && node.HasIsPassword && node.IsEnabled && !node.IsOffscreen &&
                            node.SupportedPatterns.Contains("ValuePatternIdentifiers.Pattern") ? "writable" : "unknown";
                        var captions = labels[(node.WindowEvidenceId, node.ParentEvidenceId)].Where(n => node.WindowEvidenceId.Length > 0 &&
                            node.ParentEvidenceId.Length > 0 && node.BoundsPx is {} box && Math.Abs(box.Y - n.BoundsPx.Y) <= 3 &&
                            box.X - n.BoundsPx.X - n.BoundsPx.Width is >= 0 and <= 12)
                            .Select(n => n.Name.Trim().TrimEnd(':').Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        matched.Add((id, link.SourceObjectRef));
                        found.Add(new(id, access, captions is [var caption] ? caption : ""));
                    }
                }
            }
            foreach (var (id, link) in fields[source.SourceRef])
                if (!matched.Contains((id, link.SourceObjectRef))) found.Add(new(id, "unknown", ""));
        }
        var facts = found.GroupBy(x => x.Control).Select(g =>
        {
            var states = g.Select(x => x.Access).Distinct().ToArray();
            var captions = g.Select(x => x.Caption).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new CatalogFieldCapability(g.Key, states.Contains("read-only") ? "read-only" :
                states is [var state] ? state : states.All(s => s is "grid-host" or "row-selection") ? "grid-host" : "unknown", captions is [var caption] ? caption : "");
        }).ToArray();
        return new(map, Path.GetFileName(workspace.DirectoryPath), revision.MapRevisionRef, facts);
    }
}
