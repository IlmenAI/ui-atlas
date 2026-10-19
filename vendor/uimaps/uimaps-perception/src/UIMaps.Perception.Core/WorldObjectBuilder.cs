using UIMaps.Protocol.V1;

namespace UIMaps.Perception.Core;

public sealed class WorldObjectBuilder(PerceptionPolicy policy)
{
    // Upgrade old publications without replacing existing IDs or borrowing another frame.
    // Additional IDs are deterministic so reopening the same workspace is reproducible.
    public static IReadOnlyList<WorldObject> CompleteAccessibility(SensorEpisodeBundle bundle, IReadOnlyList<WorldObject> objects)
    {
        var result = objects.ToList();
        var byEvidence = result.Where(v => v.ObjectKind == WorldObjectKind.AccessibilityElement)
            .GroupBy(v => SurfaceSceneReconstructor.Property(v, "sourceAccessibilityEvidenceId"))
            .ToDictionary(g => g.Key, g => g.First().WorldObjectId, StringComparer.Ordinal);
        foreach (var native in bundle.SensorPackets.Where(p => p.WindowTopology is not null).SelectMany(p => p.WindowTopology.Windows))
        {
            var window = result.FirstOrDefault(v => v.ObjectKind == WorldObjectKind.WindowObject &&
                SurfaceSceneReconstructor.Property(v, "windowHandleEvidence") == native.WindowHandleEvidence);
            if (window is not null) byEvidence[native.WindowEvidenceId] = window.WorldObjectId;
        }
        var missing = new ObservationNormalizer().Normalize(bundle, 0, DateTimeOffset.UnixEpoch)
            .Where(o => o.ObservationKind == ObservationKind.AccessibilityObservation && !byEvidence.ContainsKey(o.SourceEvidenceId)).ToArray();
        foreach (var observation in missing)
            byEvidence[observation.SourceEvidenceId] = "supplement:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(bundle.EpisodeId + "|" + observation.SourceEvidenceId))).ToLowerInvariant();
        foreach (var observation in missing)
        {
            var parent = byEvidence.GetValueOrDefault(observation.ParentEvidenceId) ?? byEvidence.GetValueOrDefault(observation.WindowEvidenceId);
            if (parent is null) continue;
            var element = FromObservation(observation, WorldObjectKind.AccessibilityElement, observation.Role, parent);
            element.WorldObjectId = byEvidence[observation.SourceEvidenceId];
            element.DisplayId = bundle.DisplayDescriptor.DisplayId;
            element.Properties.Add(new StringProperty { Key = "sceneSourceCompletion", Value = "same-episode-worker-response/1" });
            result.Add(element);
        }
        return result;
    }

    public IReadOnlyList<WorldObject> Build(SensorEpisodeBundle bundle, IReadOnlyList<Observation> observations)
    {
        if (observations.Count + 2 > policy.MaximumObjectCount)
            throw new PerceptionException("PERCEPTION_EPISODE_REJECTED", "The episode would exceed the configured object limit.");

        var objects = new List<WorldObject>();
        var visualObservation = observations.Single(x => x.ObservationKind == ObservationKind.VisualSurfaceObservation);
        var displayObject = FromObservation(visualObservation, WorldObjectKind.Display, "display", string.Empty);
        displayObject.Name = bundle.DisplayDescriptor.DisplayId;
        objects.Add(displayObject);

        var visualObject = FromObservation(visualObservation, WorldObjectKind.VisualSurface, "visual_surface", displayObject.WorldObjectId);
        objects.Add(visualObject);

        var evidenceToObject = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [visualObservation.SourceEvidenceId] = visualObject.WorldObjectId,
        };

        foreach (var observation in observations.Where(x => x.ObservationKind == ObservationKind.Window))
        {
            var window = FromObservation(observation, WorldObjectKind.WindowObject, observation.Role, displayObject.WorldObjectId);
            foreach (var property in observation.Properties.Where(x => x.Key is "processId" or "processName" or "executablePath" or "applicationUserModelId" or "packageFamilyName" or "windowHandleEvidence"))
                window.ProcessEvidence.Add(property.Clone());
            objects.Add(window);
            evidenceToObject[observation.SourceEvidenceId] = window.WorldObjectId;
        }

        var pending = observations.Where(x => x.ObservationKind == ObservationKind.AccessibilityObservation).ToList();
        var safety = pending.Count + 1;
        while (pending.Count > 0 && safety-- > 0)
        {
            var progress = false;
            for (var index = pending.Count - 1; index >= 0; index--)
            {
                var observation = pending[index];
                var parentEvidence = string.IsNullOrWhiteSpace(observation.ParentEvidenceId) ? observation.WindowEvidenceId : observation.ParentEvidenceId;
                if (!evidenceToObject.TryGetValue(parentEvidence, out var parentObjectId)) continue;
                var element = FromObservation(observation, WorldObjectKind.AccessibilityElement, observation.Role, parentObjectId);
                objects.Add(element);
                evidenceToObject[observation.SourceEvidenceId] = element.WorldObjectId;
                pending.RemoveAt(index);
                progress = true;
            }
            if (!progress) break;
        }

        foreach (var orphan in pending)
        {
            var parent = evidenceToObject.TryGetValue(orphan.WindowEvidenceId, out var windowId) ? windowId : displayObject.WorldObjectId;
            var element = FromObservation(orphan, WorldObjectKind.AccessibilityElement, orphan.Role, parent);
            element.Health.State = HealthState.Partial;
            element.Health.Issues.Add(new HealthIssue
            {
                State = HealthState.IdentityAmbiguous,
                Code = "PERCEPTION_PARENT_UNRESOLVED",
                SafeMessage = "Source hierarchy parent was unavailable; object was attached to the nearest supported ancestor.",
                Source = "world_object_builder",
            });
            objects.Add(element);
        }
        foreach (var observation in observations.Where(x => x.ObservationKind == ObservationKind.VisualTextObservation))
        {
            var parentEvidence = string.IsNullOrWhiteSpace(observation.ParentEvidenceId)
                ? observation.WindowEvidenceId
                : observation.ParentEvidenceId;
            var resolved = evidenceToObject.TryGetValue(parentEvidence, out var parentObjectId);
            if (!resolved)
                parentObjectId = evidenceToObject.TryGetValue(observation.WindowEvidenceId, out var windowId)
                    ? windowId
                    : displayObject.WorldObjectId;
            var visualText = FromObservation(observation, WorldObjectKind.VisualText, "visual text", parentObjectId!);
            if (!resolved)
            {
                visualText.Health.State = HealthState.Partial;
                visualText.Health.Issues.Add(new HealthIssue
                {
                    State = HealthState.IdentityAmbiguous,
                    Code = "PERCEPTION_VISUAL_TEXT_PARENT_UNRESOLVED",
                    SafeMessage = "Visual text was attached to the nearest supported window or display ancestor.",
                    Source = "world_object_builder",
                });
            }
            objects.Add(visualText);
            evidenceToObject[observation.SourceEvidenceId] = visualText.WorldObjectId;
        }
        foreach (var value in objects) value.DisplayId = bundle.DisplayDescriptor.DisplayId;
        return objects;
    }

    private static WorldObject FromObservation(Observation observation, WorldObjectKind kind, string role, string parent)
    {
        var value = new WorldObject
        {
            WorldObjectId = Guid.NewGuid().ToString("N"),
            ObjectKind = kind,
            Role = role,
            Name = observation.ObservedName,
            BoundsPx = observation.BoundsPx.Clone(),
            ParentWorldObjectId = parent,
            Health = observation.Health.Clone(),
            Confidence = observation.Confidence.Clone(),
            Freshness = observation.Freshness.Clone(),
            Lineage = new Lineage(),
            DisplayId = string.Empty,
            ProcessInstanceEvidence = observation.ProcessInstanceEvidence,
            WindowScopeRef = observation.Properties.FirstOrDefault(x => x.Key == "windowScopeRef")?.Value ?? observation.WindowEvidenceId,
            FirstObservedAt = observation.SourceInterval?.EndedAtUtc ?? string.Empty,
            LastObservedAt = observation.SourceInterval?.EndedAtUtc ?? string.Empty,
        };
        value.Properties.AddRange(observation.Properties.Select(x => x.Clone()));
        value.Lineage.ObservationIds.Add(observation.ObservationId);
        value.Lineage.SourcePacketIds.AddRange(observation.Lineage.SourcePacketIds);
        return value;
    }
}
