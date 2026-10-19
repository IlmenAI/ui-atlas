using Google.Protobuf;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V3;
using UIMaps.Perception.Core;

namespace UiAtlas.Core.Mapping.Atlas;

/// <summary>Read-only Atlas presentation of UI Maps authority. The shared perception
/// reconstructor also upgrades retained publications in memory; source evidence and reviews stay immutable.</summary>
public static class AtlasMapProjection
{
    public static UiKnowledgeGraph Create(AtlasMapWorkspace workspace, MapBuildSettings? buildSettings = null)
    {
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var appId = "atlas-app:" + AtlasEpisodeAdapter.Hash(System.Text.Encoding.UTF8.GetBytes(workspace.Index.LogicalMapId))[..32];
        nodes.Add(appId, new(appId, GraphNodeKind.Application, "", appId, workspace.Index.ApplicationName,
            [new("layer", "shared"), new("mappingEngine", "UIMaps"), new("workspaceFormat", AtlasMapWorkspace.Format)], []));
        var worldBySource = new Dictionary<(string Source, string World), string>();
        var evidenceBySource = new Dictionary<string, AtlasMapSource>(StringComparer.Ordinal);
        var surfacesByNativeWindow = new Dictionary<(string Source, long Hwnd), string>();
        var controlsByEvidence = new Dictionary<(string Source, string Evidence), List<string>>();
        var reconstruction = new SurfaceSceneReconstructor();
        string? reconstructionBundle = null;
        EvidenceRef Evidence(AtlasMapSource source, RectPx? bounds) => new(source.BundleId, source.Frame,
            $"raw/observations/frame-{source.Frame:D6}.json", AtlasEpisodeAdapter.Rect(bounds),
            source.ScreenshotEntry == $"raw/frames/frame-{source.Frame:D6}.png" ? source.ScreenshotEntry : null);
        foreach (var source in workspace.Index.Sources.OrderBy(source => source.BundleId, StringComparer.Ordinal).ThenBy(source => source.Frame))
        {
            // Separate recordings do not establish continuous observation history.
            if (reconstructionBundle != source.BundleId) reconstruction = new SurfaceSceneReconstructor();
            reconstructionBundle = source.BundleId;
            evidenceBySource.Add(source.SourceRef, source);
            var publication = GetPublicationResponse.Parser.ParseFrom(File.ReadAllBytes(workspace.Resolve(source.PublicationFile)));
            var objects = publication.Snapshot?.WorldStateSnapshot.WorldObjects.ToArray()
                ?? publication.Survey.WorldObjects.Select(value => value.WorldObject).ToArray();
            var episode = SensorEpisodeBundle.Parser.ParseFrom(File.ReadAllBytes(workspace.Resolve(source.EpisodeFile)));
            objects = WorldObjectBuilder.CompleteAccessibility(episode, objects).ToArray();
            reconstruction.Apply(episode, objects);
            var byId = objects.ToDictionary(value => value.WorldObjectId, StringComparer.Ordinal);
            var projectedIds = objects.ToDictionary(value => value.WorldObjectId, value => value.ObjectKind == WorldObjectKind.WindowObject
                ? "world-surface:" + SurfaceSceneReconstructor.Property(value, "sceneSurfaceKey") : "world:" + value.WorldObjectId, StringComparer.Ordinal);
            string? Surface(WorldObject value)
            {
                var resolved = SurfaceSceneReconstructor.Property(value, "sceneOwnerSurfaceRef");
                if (resolved.Length > 0 && byId.ContainsKey(resolved)) return resolved;
                var seen = new HashSet<string>();
                while (seen.Add(value.WorldObjectId))
                {
                    if (value.ObjectKind == WorldObjectKind.WindowObject) return value.WorldObjectId;
                    if (!byId.TryGetValue(value.ParentWorldObjectId, out value!)) return null;
                }
                return null;
            }
            foreach (var value in objects.Where(value => value.ObjectKind is WorldObjectKind.WindowObject or WorldObjectKind.AccessibilityElement or WorldObjectKind.VisualText))
            {
                var surface = Surface(value);
                if (surface is null) continue; // unassigned facts remain inspectable in their raw stream
                var id = projectedIds[value.WorldObjectId];
                var isWindow = value.ObjectKind == WorldObjectKind.WindowObject;
                var parent = isWindow ? appId : projectedIds[byId.TryGetValue(value.ParentWorldObjectId, out var direct)
                    && Surface(direct) == surface ? direct.WorldObjectId : surface];
                var properties = value.Properties.Select(property => new GraphProperty(property.Key, Limit(property.Value), true)).ToList();
                properties.RemoveAll(property => property.Name is "layer" or "rawSurfaceId");
                foreach (var key in new[] { "scenePresentedOn", "sceneOccludedBy", "sceneReportedSurfaceRef" })
                    for (var propertyIndex = 0; propertyIndex < properties.Count; propertyIndex++)
                        if (properties[propertyIndex].Name == key && projectedIds.TryGetValue(properties[propertyIndex].Value, out var target))
                            properties[propertyIndex] = properties[propertyIndex] with { Value = target };
                properties.AddRange([new("layer", "raw-world"), new("sourcePublication", publication.Snapshot?.PublicationId ?? publication.Survey!.PublicationId),
                    new("acquisitionSemantic", publication.Survey is not null ? "Survey" : "Snapshot"),
                    new("acquisitionStatus", publication.Survey is not null ? "partial-survey" : "snapshot"), new("recordKind", "observation"),
                    new("captureStatus", value.Health.State.ToString()), new("sourceObjectRef", value.WorldObjectId)]);
                if (isWindow)
                {
                    if (long.TryParse(SurfaceSceneReconstructor.Property(value, "windowHandleEvidence"), out var hwnd))
                    {
                        properties.Add(new("hwnd", $"0x{hwnd:X}"));
                        surfacesByNativeWindow[(source.SourceRef, hwnd)] = id;
                    }
                }
                else properties.Add(new("rawSurfaceId", projectedIds[surface]));
                if (!isWindow && SurfaceSceneReconstructor.Property(value, "sourceAccessibilityEvidenceId") is { Length: > 0 } accessibilityEvidence)
                {
                    var key = (source.SourceRef, accessibilityEvidence);
                    if (!controlsByEvidence.TryGetValue(key, out var controlIds)) controlsByEvidence[key] = controlIds = [];
                    controlIds.Add(id);
                }
                var node = new GraphNode(id, isWindow ? GraphNodeKind.Surface : GraphNodeKind.Control, parent, id,
                    Limit(string.IsNullOrEmpty(value.Name) && isWindow ? SurfaceSceneReconstructor.Property(value, "className") : value.Name),
                    properties, [Evidence(source, value.BoundsPx)]);
                nodes[id] = nodes.TryGetValue(id, out var existing) ? existing with {
                    Evidence = existing.Evidence.Concat(node.Evidence).Distinct().ToArray(),
                    Properties = isWindow && properties.Any(property => property is { Name: "sceneVisible", Value: "True" })
                        ? existing.Properties.Where(property => property.Name != "sceneVisible").Append(new("sceneVisible", "True")).ToArray()
                        : existing.Properties } : node;
                if (isWindow)
                {
                    var stateId = "world-state:" + AtlasEpisodeAdapter.Hash(System.Text.Encoding.UTF8.GetBytes(source.SourceRef + "|" + value.WorldObjectId))[..32];
                    var handle = SurfaceSceneReconstructor.Property(value, "windowHandleEvidence");
                    var coveringDialog = objects.FirstOrDefault(other => other.ObjectKind == WorldObjectKind.WindowObject &&
                        other.ProcessInstanceEvidence.Length > 0 && other.ProcessInstanceEvidence == value.ProcessInstanceEvidence &&
                        SurfaceSceneReconstructor.Property(other, "surfaceClass") == "RawDialogWindow" &&
                        SurfaceSceneReconstructor.Property(other, "sceneVisible") == "True" && handle.Length > 0 &&
                        (SurfaceSceneReconstructor.Property(other, "ownerWindowHandleEvidence") == handle ||
                         SurfaceSceneReconstructor.Property(other, "rootOwnerWindowHandleEvidence") == handle) &&
                        int.TryParse(SurfaceSceneReconstructor.Property(other, "zOrder"), out var front) &&
                        int.TryParse(SurfaceSceneReconstructor.Property(value, "zOrder"), out var behind) && front < behind);
                    nodes.Add(stateId, new(stateId, GraphNodeKind.State, id, stateId, $"Observed frame {source.Frame}",
                        [new("layer", "raw-world"), new("sceneProjection", "surface-observation"),
                            new("sourceObservationSetRef", source.SourceRef),
                            new("windowHandleEvidence", handle),
                            new("sceneContentFingerprint", SurfaceSceneReconstructor.Property(value, "sceneContentFingerprint")),
                            new("sceneBackgroundOfDialog", coveringDialog is null ? "" : projectedIds[coveringDialog.WorldObjectId]),
                            new("sceneVisible", SurfaceSceneReconstructor.Property(value, "sceneVisible")),
                            new("sceneAlignment", SurfaceSceneReconstructor.Property(value, "sceneAlignment"))], node.Evidence));
                }
                worldBySource[(source.SourceRef, value.WorldObjectId)] = id;
            }
        }

        // Raw Data Streams come straight from sealed provider responses, including
        // provider/query HWND identity. They never use the perceived World State.
        foreach (var bundleFile in workspace.Index.BundleFiles)
        {
            using var bundle = RecordingBundle.Open(workspace.Resolve(bundleFile));
            var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
            foreach (var source in workspace.Index.Sources.Where(value => value.BundleId == manifest.SessionId))
            {
                var frame = bundle.ReadJson<FrameObservation>($"raw/observations/frame-{source.Frame:D6}.json");
                var episode = frame.NativeEpisodeEntry is { Length: > 0 } nativeEntry
                    ? SensorEpisodeBundle.Parser.ParseFrom(bundle.ReadBytes(nativeEntry)) : null;
                var nativeControls = episode?.SensorPackets.SelectMany(packet => packet.Accessibility is { } payload
                        ? payload.RawProviderResponses.Count > 0
                            ? payload.RawProviderResponses.SelectMany(response => response.Packet.Nodes)
                            : payload.Nodes.AsEnumerable()
                        : Enumerable.Empty<AccessibilityNodeRecord>())
                    .GroupBy(node => node.AccessibilityEvidenceId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                foreach (var raw in RecordingGraphBuilder.ProjectRawObservation(manifest, frame, appId))
                {
                    var properties = raw.Properties.ToList();
                    properties.AddRange([new("nativeEpisodeEntry", frame.NativeEpisodeEntry ?? ""),
                        new("sourceEpisodeFile", source.EpisodeFile), new("sourceFrame", source.Frame.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        new("sourceBundle", source.BundleId)]);
                    if (raw.Kind == GraphNodeKind.Control && raw.Observations?.SingleOrDefault()?.Control is { } control
                        && nativeControls?.GetValueOrDefault(control.RuntimeId) is { } native)
                    {
                        properties.RemoveAll(property => property.Name is "runtimeId" or "parentRuntimeId");
                        properties.AddRange([new("runtimeId", string.Join(",", native.RuntimeIdEvidence)),
                            new("parentEvidenceId", native.ParentEvidenceId), new("sourceAccessibilityEvidenceId", native.AccessibilityEvidenceId),
                            new("reportedWindowHwnd", native.NativeWindowHandleEvidence), new("queryScope", "native-query-window")]);
                        if (controlsByEvidence.TryGetValue((source.SourceRef, native.AccessibilityEvidenceId), out var worldControls))
                            foreach (var controlId in worldControls)
                            {
                                nodes[controlId] = nodes[controlId] with { Properties = nodes[controlId].Properties.Append(new("sourceRawDataStreamControlId", raw.Id)).Distinct().ToArray() };
                                var ownerId = nodes[controlId].Properties.First(property => property.Name == "rawSurfaceId").Value;
                                var rawSurface = properties.FirstOrDefault(property => property.Name == "rawDataStreamSurfaceId")?.Value;
                                if (rawSurface is not null)
                                    nodes[ownerId] = nodes[ownerId] with { Properties = nodes[ownerId].Properties.Append(new("sourceRawDataStreamSurfaceId", rawSurface)).Distinct().ToArray() };
                            }
                    }
                    nodes.Add(raw.Id, raw with { Properties = properties });
                }
                foreach (var window in frame.ScopedWindows ?? [frame.Window])
                {
                    if (!surfacesByNativeWindow.TryGetValue((source.SourceRef, window.Hwnd), out var worldId)) continue;
                    var world = nodes[worldId];
                    var matched = nodes.Values.Where(node => node.Kind == GraphNodeKind.Window
                        && node.Evidence.Any(evidence => evidence.BundleId == source.BundleId && evidence.FrameSequence == source.Frame)
                        && node.Properties.Any(property => property.Name == "hwnd"
                            && property.Value.Equals($"0x{window.Hwnd:X}", StringComparison.OrdinalIgnoreCase))).ToArray();
                    nodes[world.Id] = world with { Properties = world.Properties.Concat(matched.Select(node =>
                        new GraphProperty("sourceRawDataStreamSurfaceId", node.Id))).Distinct().ToArray() };
                }
            }
        }
        foreach (var draft in workspace.Drafts)
        {
            var entities = draft.Entities.ToDictionary(entity => entity.MapEntityRef, StringComparer.Ordinal);
            string? Surface(MapEntity entity)
            {
                var seen = new HashSet<string>();
                while (seen.Add(entity.MapEntityRef))
                {
                    if (entity.EntityKind == MapEntityKind.MapSurface) return entity.MapEntityRef;
                    if (!entities.TryGetValue(entity.ParentEntityRef, out entity!)) return null;
                }
                return null;
            }
            foreach (var entity in draft.Entities.Where(entity => entity.EntityKind is MapEntityKind.MapSurface or MapEntityKind.MapControl or MapEntityKind.MapDataGrid or MapEntityKind.MapGridColumn))
            {
                var isSurface = entity.EntityKind == MapEntityKind.MapSurface;
                var links = entity.EntityKind == MapEntityKind.MapGridColumn && entities.TryGetValue(entity.ParentEntityRef, out var gridParent)
                    ? gridParent.EvidenceLinks : entity.EvidenceLinks;
                var sources = links.Select(link => worldBySource.GetValueOrDefault((link.SourceObservationSetRef, link.SourceObjectRef)))
                    .Where(id => id is not null && nodes[id].Kind == (isSurface ? GraphNodeKind.Surface : GraphNodeKind.Control))
                    .Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
                if (sources.Length == 0) continue;
                var surface = Surface(entity);
                if (!isSurface && surface is null) continue;
                var parent = isSurface ? appId : entities.TryGetValue(entity.ParentEntityRef, out var parentEntity)
                    && parentEntity.EntityKind is MapEntityKind.MapControl or MapEntityKind.MapDataGrid ? parentEntity.MapEntityRef : surface!;
                var properties = new List<GraphProperty> { new("layer", "semantic-world"), new("mapDraftRef", draft.MapDraftRef),
                    new("reviewState", entity.ReviewState.ToString()), new("controlType", SemanticMapProjection.ControlKind(entity, sources.Select(id => nodes[id]).ToArray())), new("mappingEngine", "UIMaps"),
                    new("curationHidden", (entity.ReviewState == ReviewState.ReviewRejected).ToString()) };
                if (entity.EntityKind == MapEntityKind.MapGridColumn) properties.Add(new("evidenceScope", "reviewed-column-schema; source links identify the recorded grid host"));
                properties.Add(new("acquisitionStatus", draft.Sources.Any(value => value.AcquisitionSemantic == AcquisitionSemantic.AcquisitionSurvey) ? "partial-survey" : "snapshot"));
                properties.Add(new("recordKind", "observation"));
                var pathParts = new List<string> { entity.MapEntityRef };
                var ancestor = entity;
                var seenParents = new HashSet<string>(StringComparer.Ordinal) { entity.MapEntityRef };
                while (entities.TryGetValue(ancestor.ParentEntityRef, out ancestor!))
                {
                    if (!seenParents.Add(ancestor.MapEntityRef)) throw new InvalidDataException("Map containment contains a cycle.");
                    pathParts.Insert(0, ancestor.MapEntityRef);
                }
                properties.Add(new("controlPath", Limit(string.Join("/", pathParts))));
                properties.AddRange(sources.Select(id => new GraphProperty(isSurface ? "sourceRawSurfaceId" : "sourceRawControlId", id)));
                properties.AddRange(SemanticMapProjection.Describe(entity, sources.Select(id => nodes[id]).ToArray()).Select(p => p with { Value = Limit(p.Value) }));
                var variantAncestor = pathParts.Select(id => entities.GetValueOrDefault(id))
                    .FirstOrDefault(value => value?.EntityKind == MapEntityKind.MapSurfaceVariant);
                if (variantAncestor is not null) properties.Add(new("semanticVariantRef", variantAncestor.MapEntityRef));
                if (!isSurface && surface is not null && entities.TryGetValue(surface, out var semanticSurface))
                {
                    var declaredSources = semanticSurface.EvidenceLinks.Select(link => worldBySource.GetValueOrDefault((link.SourceObservationSetRef, link.SourceObjectRef)))
                        .Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
                    if (sources.Any(id => nodes[id].Properties.FirstOrDefault(property => property.Name == "rawSurfaceId") is { } owner &&
                        !declaredSources.Contains(owner.Value) && nodes[id].Properties.Any(property => property.Name == "sceneReportedSurfaceRef" && declaredSources.Contains(property.Value))))
                    {
                        properties.Add(new("sceneLineageBasis", "retained-reported-owner"));
                        properties.Add(new("sceneReviewNote", "Raw World now resolves different surface ownership. The existing semantic assignment is retained for review."));
                    }
                }
                if (isSurface) properties.Add(new("semanticSurfaceKind", "Window"));
                else properties.Add(new("semanticSurfaceId", surface!));
                var evidence = links.Where(link => evidenceBySource.ContainsKey(link.SourceObservationSetRef))
                    .Select(link =>
                    {
                        var source = evidenceBySource[link.SourceObservationSetRef];
                        var current = worldBySource.GetValueOrDefault((link.SourceObservationSetRef, link.SourceObjectRef));
                        return current is not null ? nodes[current].Evidence.FirstOrDefault(e => e.BundleId == source.BundleId && e.FrameSequence == source.Frame)
                            ?? Evidence(source, link.BoundsPx) : Evidence(source, link.BoundsPx);
                    })
                    .Distinct().ToArray();
                nodes.Add(entity.MapEntityRef, new(entity.MapEntityRef, isSurface ? GraphNodeKind.Surface : GraphNodeKind.Control,
                    parent, entity.MapEntityRef, Limit(entity.DisplayName), properties, evidence));
            }
            SemanticMapProjection.AddVariants(draft, nodes);
        }
        // Map variants are represented by the editor's observed-frame cards. A
        // missing projected parent cannot be filled by guessed containment.
        foreach (var node in nodes.Values.Where(node => node.Kind != GraphNodeKind.Application).ToArray())
            if (!nodes.ContainsKey(node.ParentId)) throw new InvalidDataException("Map projection has an unresolved parent: " + node.Id);
        var values = nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var containment = values.Where(node => node.Kind != GraphNodeKind.Application)
            .Select(node => new GraphEdge("contains:" + AtlasEpisodeAdapter.Hash(System.Text.Encoding.UTF8.GetBytes(node.Id))[..32],
                "contains", node.ParentId, node.Id, [], node.Evidence));
        var relationships = values.Where(node => node.Kind == GraphNodeKind.Control && node.Properties.Any(property => property is { Name: "layer", Value: "raw-world" }))
            .SelectMany(node => node.Properties.Where(property => property.Name is "scenePresentedOn" or "sceneOccludedBy" && nodes.ContainsKey(property.Value))
                .Select(property => new GraphEdge(property.Name + ":" + AtlasEpisodeAdapter.Hash(System.Text.Encoding.UTF8.GetBytes(property.Value + "|" + node.Id))[..32],
                    property.Name == "scenePresentedOn" ? "presents" : "occludes", property.Value, node.Id,
                    [new("reason", property.Name == "scenePresentedOn" ? "stable-owner-with-popup-presentation" : "same-observation-native-z-order"), new("confidence", "inferred")], node.Evidence)));
        var lineagePairs = values.Where(n => n.Properties.Any(p => p is { Name: "semanticProjection", Value: "map-interpretation/1" }))
            .SelectMany(n => n.Properties.Where(p => p.Name is "sourceRawSurfaceId" or "sourceRawControlId" or "sourceRawStateId")
                .Select(p => (Source: p.Value, Target: n))).ToArray();
        var sourceCounts = lineagePairs.GroupBy(p => p.Source).ToDictionary(g => g.Key, g => g.Count());
        var targetCounts = lineagePairs.GroupBy(p => p.Target.Id).ToDictionary(g => g.Key, g => g.Count());
        var lineage = lineagePairs.Select(p => new GraphEdge("interpretation:" + AtlasEpisodeAdapter.Hash(System.Text.Encoding.UTF8.GetBytes(p.Source + "|" + p.Target.Id))[..32],
            "semantic-interpreted-from", p.Source, p.Target.Id,
            [new("materializer", "map-interpretation/1"), new("cardinality", (targetCounts[p.Target.Id] > 1 ? "many" : "one") + "-to-" + (sourceCounts[p.Source] > 1 ? "many" : "one")),
                new("authority", "Map draft interpretation")], p.Target.Evidence.Where(e => nodes[p.Source].Evidence.Any(s => s.BundleId == e.BundleId && s.FrameSequence == e.FrameSequence)).ToArray()));
        var edges = containment.Concat(relationships).Concat(lineage).OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray();
        // Reject an over-limit build before serializing its entire projection for hashing.
        buildSettings?.CheckCounts(values.Length, edges.Length);
        var bundleIds = workspace.Index.Sources.Select(source => source.BundleId).Distinct(StringComparer.Ordinal).ToArray();
        return new(new(FormatVersions.Graph, FormatVersions.Tool, workspace.Index.LogicalMapId, DateTimeOffset.UtcNow,
            bundleIds[0], GraphSemantics.ComputeHash(values, edges), FormatVersions.FullEvidenceProfile, bundleIds, workspace.Index.LogicalMapId), values, edges);
    }
    private static string Limit(string text) => text.Length <= 4096 ? text : text[..4096];
}
