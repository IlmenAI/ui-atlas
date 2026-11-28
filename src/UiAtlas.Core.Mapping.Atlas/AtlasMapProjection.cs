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
        => Create(workspace, workspace.Index, workspace.Drafts, buildSettings, null, default);

    public static UiKnowledgeGraph ForRevision(AtlasMapWorkspace workspace, UiMapRevision revision, CancellationToken token)
    {
        var retained = revision.Sources.ToDictionary(s => s.SourceObservationSetRef, StringComparer.Ordinal);
        var sources = workspace.Index.Sources.Where(s => retained.ContainsKey(s.SourceRef)).ToArray();
        if (sources.Length == 0 || sources.Length != retained.Count)
            throw new InvalidDataException("The recordings for this map version are incomplete.");
        var files = sources.Select(s => "recordings/" + s.BundleHash + ".mlrec").Distinct().ToArray();
        if (files.Except(workspace.Index.BundleFiles, StringComparer.Ordinal).Any())
            throw new InvalidDataException("A recording for this map version is missing.");
        var draft = new UiMapDraft { MapDraftRef = revision.MapRevisionRef, Entities = { revision.Entities },
            Sources = { revision.Sources }, ControlIdentityPolicy = revision.ControlIdentityPolicy,
            LogicalControlGroups = { revision.LogicalControlGroups }, Interactions = { revision.Interactions } };
        return Create(workspace, workspace.Index with { Sources = sources, BundleFiles = files }, [draft], null, retained, token);
    }

    private static UiKnowledgeGraph Create(AtlasMapWorkspace workspace, AtlasMapIndex index, IEnumerable<UiMapDraft> drafts,
        MapBuildSettings? buildSettings, Dictionary<string, SourceObservationSet>? retained, CancellationToken token)
    {
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var observedTransitions = new List<GraphEdge>();
        var recordedActions = new Dictionary<string, InteractionObservation>(StringComparer.Ordinal);
        var appId = "atlas-app:" + AtlasEpisodeAdapter.Hash(System.Text.Encoding.UTF8.GetBytes(index.LogicalMapId))[..32];
        nodes.Add(appId, new(appId, GraphNodeKind.Application, "", appId, index.ApplicationName,
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
        foreach (var source in index.Sources.OrderBy(source => source.BundleId, StringComparer.Ordinal).ThenBy(source => source.Frame))
        {
            token.ThrowIfCancellationRequested();
            // Separate recordings do not establish continuous observation history.
            if (reconstructionBundle != source.BundleId) reconstruction = new SurfaceSceneReconstructor();
            reconstructionBundle = source.BundleId;
            evidenceBySource.Add(source.SourceRef, source);
            var publication = GetPublicationResponse.Parser.ParseFrom(File.ReadAllBytes(workspace.Resolve(source.PublicationFile)));
            var objects = publication.Snapshot?.WorldStateSnapshot.WorldObjects.ToArray()
                ?? publication.Survey.WorldObjects.Select(value => value.WorldObject).ToArray();
            var episode = SensorEpisodeBundle.Parser.ParseFrom(File.ReadAllBytes(workspace.Resolve(source.EpisodeFile)));
            if (retained is not null)
            {
                var pin = retained[source.SourceRef];
                IMessage? payload = (IMessage?)publication.Snapshot?.WorldStateSnapshot ?? publication.Survey;
                if (episode.EpisodeId != pin.EpisodeId || payload is null || AtlasEpisodeAdapter.Hash(payload.ToByteArray()) != pin.SourcePayloadHash)
                    throw new InvalidDataException("An observation differs from the saved revision.");
                new PacketValidator(PerceptionPolicy.Default).ValidateRecorded(episode);
            }
            objects = WorldObjectBuilder.CompleteAccessibility(episode, objects).ToArray();
            reconstruction.Apply(episode, objects);
            objects = UIMaps.Protocol.RawWorldControlFusion.Reconstruct(objects).ToArray();
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
                foreach (var member in UIMaps.Protocol.RawWorldControlFusion.Members(value))
                if (!isWindow && SurfaceSceneReconstructor.Property(member, "sourceAccessibilityEvidenceId") is { Length: > 0 } accessibilityEvidence)
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
                    var previousSurface = "world-surface:" + SurfaceSceneReconstructor.Property(value, "sceneEnteredFromSurfaceKey");
                    if (nodes.TryGetValue(previousSurface, out var previous) && previousSurface != id
                        && previous.Evidence.LastOrDefault(e => e.BundleId == source.BundleId && e.FrameSequence < source.Frame) is { } priorEvidence)
                        observedTransitions.Add(new GraphEdge("surface-transition:" + source.SourceRef + ":" + value.WorldObjectId,
                            "observed-surface-transition", previousSurface, id,
                            [new("evidence", "observed foreground change; causality unverified"),
                                new("priorObservation", SurfaceSceneReconstructor.Property(value, "sceneTransitionPriorObservation")),
                                new("resultObservation", source.SourceRef)], new[] { priorEvidence }.Concat(node.Evidence).ToArray()));
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
                foreach (var member in UIMaps.Protocol.RawWorldControlFusion.Members(value))
                    worldBySource[(source.SourceRef, member.WorldObjectId)] = id;
            }
        }

        // Raw Data Streams come straight from sealed provider responses, including
        // provider/query HWND identity. They never use the perceived World State.
        foreach (var bundleFile in index.BundleFiles)
        {
            using var bundle = RecordingBundle.Open(workspace.Resolve(bundleFile));
            var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
            var bundleHash = Path.GetFileNameWithoutExtension(bundleFile);
            foreach (var action in AtlasInteractionAdapter.ReadInteractions(bundle))
                recordedActions.Add($"atlas-action:{bundleHash}:{action.Sequence}", action);
            foreach (var source in index.Sources.Where(value => value.BundleId == manifest.SessionId))
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
        foreach (var draft in drafts)
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
            // Old drafts may contain native owner-only windows. Project only
            // surfaces with positive geometry in at least one retained source;
            // never gate on the latest visibility or on control counts. This
            // preserves closed/hidden historical windows and image-only popups.
            var eligibleSurfaces = draft.Entities.Where(entity => entity.EntityKind == MapEntityKind.MapSurface &&
                entity.EvidenceLinks.Select(link => worldBySource.GetValueOrDefault((link.SourceObservationSetRef, link.SourceObjectRef)))
                    .OfType<string>().Any(id => nodes[id].Kind == GraphNodeKind.Surface &&
                        nodes[id].Evidence.Any(evidence => evidence.Bounds is { Width: > 0, Height: > 0 })))
                .Select(entity => entity.MapEntityRef).ToHashSet(StringComparer.Ordinal);
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
                if (surface is null || !eligibleSurfaces.Contains(surface)) continue;
                var parent = isSurface ? appId : entities.TryGetValue(entity.ParentEntityRef, out var parentEntity)
                    && parentEntity.EntityKind is MapEntityKind.MapControl or MapEntityKind.MapDataGrid ? parentEntity.MapEntityRef : surface!;
                var properties = new List<GraphProperty> { new("layer", "semantic-world"), new("mapDraftRef", draft.MapDraftRef),
                    new("reviewState", entity.ReviewState.ToString()), new("controlType", SemanticMapProjection.ControlKind(entity, sources.Select(id => nodes[id]).ToArray())), new("mappingEngine", "UIMaps"),
                    new("curationHidden", (entity.ReviewState == ReviewState.ReviewRejected || !entity.Applicable).ToString()) };
                properties.Add(new("mapEntityKind", entity.EntityKind.ToString()));
                if (draft.ControlIdentityPolicy.Length > 0)
                {
                    properties.Add(new("controlIdentityPolicy", draft.ControlIdentityPolicy));
                    if (draft.LogicalControlGroups.FirstOrDefault(g => g.SupportingEntityRefs.Contains(entity.MapEntityRef)) is { } definition)
                        properties.AddRange([new("logicalDefinitionRef", definition.DefinitionRef),
                            new("logicalSurfaceRef", definition.SurfaceDefinitionRef),
                            new("logicalSupportingExamples", definition.SupportingEntityRefs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            new("logicalVariableProperties", string.Join(", ", definition.VariablePropertyKeys))]);
                }
                if (entity.HostedDataGrid is { } hostedGrid)
                {
                    properties.Add(new("sourceGridRef", hostedGrid.SourceGridRef));
                    properties.Add(new("gridHostControlRef", hostedGrid.HostControlRef));
                    properties.Add(new("gridSchemaVersions", hostedGrid.SchemaVersions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    properties.Add(new("gridSchemaDefault", hostedGrid.DefaultSchemaVersionRef));
                    properties.Add(new("gridSchemaSelection", hostedGrid.SchemaDefaultPinned ? "Pinned" : "Automatic"));
                }
                if (!string.IsNullOrWhiteSpace(entity.GridInternalDataGridRef))
                    properties.Add(new("gridInternalDataGridRef", entity.GridInternalDataGridRef));
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
            var interactionEdges = InteractionEdges(draft, entities, nodes, evidenceBySource, worldBySource, recordedActions).ToArray();
            observedTransitions.AddRange(interactionEdges.Where(edge => edge.Kind != "opens-popup"));
            foreach (var group in interactionEdges.Where(edge => edge.Kind == "opens-popup").GroupBy(edge => edge.Id))
            {
                var edge = group.First();
                observedTransitions.Add(edge with { Evidence = group.SelectMany(e => e.Evidence).Distinct().ToArray() });
                var popup = nodes[edge.ToId];
                if (popup.Properties.Any(p => p.Name == "semanticAnchorStatus"))
                    nodes[popup.Id] = popup with { Properties = popup.Properties.Where(p => p.Name != "semanticAnchorStatus")
                        .Append(new GraphProperty("semanticAnchorStatus", "observed opener")).ToArray() };
            }
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
        var edges = containment.Concat(relationships).Concat(lineage).Concat(observedTransitions).OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray();
        // Reject an over-limit build before serializing its entire projection for hashing.
        buildSettings?.CheckCounts(values.Length, edges.Length);
        var bundleIds = index.Sources.Select(source => source.BundleId).Distinct(StringComparer.Ordinal).ToArray();
        return new(new(FormatVersions.Graph, FormatVersions.Tool, index.LogicalMapId, DateTimeOffset.UtcNow,
            bundleIds[0], GraphSemantics.ComputeHash(values, edges), FormatVersions.FullEvidenceProfile, bundleIds, index.LogicalMapId), values, edges);
    }
    private static string Limit(string text) => text.Length <= 4096 ? text : text[..4096];

    private static IEnumerable<GraphEdge> InteractionEdges(UiMapDraft draft,
        IReadOnlyDictionary<string, MapEntity> entities, IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyDictionary<string, AtlasMapSource> sources,
        IReadOnlyDictionary<(string Source, string World), string> worldBySource,
        IReadOnlyDictionary<string, InteractionObservation> actions)
    {
        static string PopupEdgeId(string from, string to) => "opens-popup:" +
            AtlasEpisodeAdapter.Hash(System.Text.Encoding.UTF8.GetBytes(from + "|" + to))[..32];
        var observations = draft.Sources.ToDictionary(s => s.SourceObservationSetRef, StringComparer.Ordinal);
        IEnumerable<GraphNode> StatesAt(string variant, AtlasMapSource frame) => nodes[variant].Properties
            .Where(property => property.Name == "sourceRawStateId").Select(property => nodes[property.Value])
            .Where(state => state.Evidence.Any(item => item.BundleId == frame.BundleId && item.FrameSequence == frame.Frame));
        foreach (var interaction in draft.Interactions)
        {
            if (interaction.ReviewState == ReviewState.ReviewRejected || !interaction.UserPerformed ||
                !actions.TryGetValue(interaction.ActionEventRef, out var action) || action.Actor != InteractionActor.User ||
                action.Outcome != InteractionOutcome.Succeeded ||
                !sources.TryGetValue(interaction.SourceObservationSetRef, out var before) ||
                !sources.TryGetValue(interaction.ResultObservationSetRef, out var after) || before.BundleHash != after.BundleHash ||
                interaction.RecordingSessionRef != "atlas-recording:" + before.BundleHash ||
                action.SourceFrameSequence != before.Frame || !action.ResultFrameSequences.Contains(after.Frame) ||
                !observations.TryGetValue(before.SourceRef, out var source) || !observations.TryGetValue(after.SourceRef, out var result) ||
                !entities.TryGetValue(interaction.SourceMapEntityRef, out var control) || !control.Applicable ||
                control.ReviewState == ReviewState.ReviewRejected || !nodes.ContainsKey(control.MapEntityRef) ||
                !worldBySource.TryGetValue((before.SourceRef, interaction.SourceWorldObjectRef), out var rawControl)) continue;
            var owner = AtlasSurfaceInventory.Owner(entities, control.MapEntityRef);
            var from = source.SurfaceObservations.Where(o => o.ResultingMapSurfaceRef == owner)
                .Select(o => o.ResultingMapVariantRef).Distinct().Where(nodes.ContainsKey).ToArray();
            if (from is not [var origin]) continue;
            var rawOwner = nodes[rawControl].Properties.FirstOrDefault(p => p.Name == "rawSurfaceId")?.Value;
            if (StatesAt(origin, before).Where(n => n.ParentId == rawOwner).ToArray() is not [var rawOrigin]) continue;
            var previous = source.SurfaceObservations.Select(o => o.ResultingMapVariantRef).ToHashSet(StringComparer.Ordinal);
            var visible = result.SurfaceObservations.Select(o => o.ResultingMapVariantRef).ToHashSet(StringComparer.Ordinal);
            var targets = interaction.ResultSurfaceVariantRefs.Where(id => visible.Contains(id) && nodes.ContainsKey(id))
                .Distinct().ToArray();
            var destinations = targets.Where(id => !previous.Contains(id)).ToArray();
            if (destinations.Length == 0 && visible.Count == 1 && !visible.Contains(origin)) destinations = targets;
            foreach (var destination in destinations.Where(id => entities.TryGetValue(id, out var entity) &&
                         entity.Applicable && entity.ReviewState != ReviewState.ReviewRejected)
                     .SelectMany(id => StatesAt(id, after)).DistinctBy(state => state.Id))
            {
                var evidence = destination.Evidence.Where(e => e.BundleId == after.BundleId && e.FrameSequence == after.Frame).ToArray();
                var popupId = destination.ParentId;
                if (nodes[popupId].Properties.Any(p => p is { Name: "surfaceClass", Value: "RawPopupWindow" }) &&
                    destination.Properties.Any(p => p is { Name: "sceneVisible", Value: "True" }) &&
                    !nodes[popupId].Evidence.Any(e => e.BundleId == before.BundleId && e.FrameSequence == before.Frame))
                {
                    var semanticPopups = targets.Where(t => nodes[t].Properties.Any(p => p.Name == "sourceRawStateId" && p.Value == destination.Id))
                        .Select(t => nodes[t].ParentId).Distinct().ToArray();
                    yield return new GraphEdge(PopupEdgeId(rawControl, popupId), "opens-popup", rawControl, popupId, [], evidence);
                    if (semanticPopups is [var semanticPopup])
                        yield return new GraphEdge(PopupEdgeId(control.MapEntityRef, semanticPopup), "opens-popup",
                            control.MapEntityRef, semanticPopup, [], evidence);
                }
                yield return new GraphEdge(interaction.InteractionRef + ":" + destination.Id, "interaction", rawOrigin.Id, destination.Id,
                    [new("sessionId", before.BundleId), new("interactionId", action.InteractionId),
                     new("operationId", action.OperationId), new("attempt", action.Attempt.ToString()),
                     new("sequence", action.Sequence.ToString()), new("actor", action.Actor.ToString()),
                     new("gesture", action.Gesture.ToString()), new("action", action.Action.ToString()),
                     new("screenX", interaction.ScreenX.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                     new("screenY", interaction.ScreenY.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                     new("outcome", action.Outcome.ToString()), new("diagnosticCode", action.DiagnosticCode),
                     new("startedUtc", action.StartedUtc.ToString("O")), new("completedUtc", action.CompletedUtc.ToString("O")),
                     new("sourceControlId", rawControl), new("sourceFrameSequence", before.Frame.ToString()),
                     new("resultFrameSequence", after.Frame.ToString())], evidence);
            }
        }
    }
}
