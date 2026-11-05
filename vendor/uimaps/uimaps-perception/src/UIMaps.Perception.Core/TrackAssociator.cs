using UIMaps.Protocol.V1;

namespace UIMaps.Perception.Core;

public sealed class TrackAssociator(AssociationPolicyV1 policy)
{
    public IReadOnlyList<Track> Associate(IReadOnlyList<WorldObject> current, WorldStateSnapshot? previous)
    {
        var tracks = new List<Track>();
        var previousObjects = previous?.WorldObjects.ToList() ?? [];
        var previousByWorldId = previousObjects.ToDictionary(x => x.WorldObjectId, StringComparer.Ordinal);
        var assignedPrevious = new HashSet<string>(StringComparer.Ordinal);
        var currentWorldToTrack = new Dictionary<string, string>(StringComparer.Ordinal);
        var providerIds = new Dictionary<WorldObject, string?>(ReferenceEqualityComparer.Instance);
        foreach (var value in current.Concat(previousObjects)) providerIds[value] = ProviderIdentity(value);
        var currentProviderIds = current.Select(value => providerIds[value]).Where(id => id is not null)
            .GroupBy(id => id!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var previousProviderIds = previousObjects.Select(value => providerIds[value]).Where(id => id is not null)
            .GroupBy(id => id!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var value in current.OrderBy(ObjectDepth))
        {
            var parentTrack = currentWorldToTrack.GetValueOrDefault(value.ParentWorldObjectId);
            var providerId = providerIds[value];
            var duplicateProviderIdentity = providerId is not null
                && (currentProviderIds[providerId] > 1 || previousProviderIds.GetValueOrDefault(providerId) > 1);
            var candidates = previousObjects
                .Where(x => !assignedPrevious.Contains(x.WorldObjectId))
                .Where(x => CompatibleScope(value, x))
                // Positive UIA identities distinguish overlapping Ribbon panes.
                // Never alias two different provider IDs in one process/window.
                .Where(x => providerId is null || providerIds[x] is not { } priorId || priorId == providerId)
                .Select(x => (Object: x, Result: policy.Score(value, NormalizePreviousParent(x, previousByWorldId), parentTrack)))
                .Where(x => x.Result.Score > int.MinValue)
                .OrderByDescending(x => x.Result.Score)
                .ToList();

            if (providerId is not null && !duplicateProviderIdentity)
            {
                var exact = candidates.Where(c => providerIds[c.Object] == providerId).ToArray();
                if (exact.Length == 1)
                    candidates = [(exact[0].Object, (100, (IReadOnlyList<string>)["same_scoped_uia_runtime_identity"]))];
            }

            Track track;
            if (!duplicateProviderIdentity && candidates.Count > 0 && policy.IsConfident(candidates[0].Result.Score, candidates.Count > 1 ? candidates[1].Result.Score : null))
            {
                var matched = candidates[0].Object;
                var trackId = string.IsNullOrWhiteSpace(matched.TrackId) ? Guid.NewGuid().ToString("N") : matched.TrackId;
                value.TrackId = trackId;
                assignedPrevious.Add(matched.WorldObjectId);
                track = CreateTrack(value, trackId, matched.WorldObjectId, AssociationState.Matched, candidates[0].Result.Evidence, candidates[0].Result.Score / 100d);
            }
            else if (duplicateProviderIdentity || candidates.Count > 1 && candidates[0].Result.Score >= 30 && candidates[0].Result.Score - candidates[1].Result.Score < 15)
            {
                value.TrackId = Guid.NewGuid().ToString("N");
                value.Health.State = HealthState.Partial;
                value.Health.Issues.Add(new HealthIssue { State = HealthState.IdentityAmbiguous, Code = "PERCEPTION_IDENTITY_AMBIGUOUS", SafeMessage = "Multiple current or prior objects were plausible association candidates.", Source = "track_associator" });
                track = CreateTrack(value, value.TrackId, string.Empty, AssociationState.Ambiguous, ["multiple_close_candidates"], 0);
            }
            else
            {
                value.TrackId = Guid.NewGuid().ToString("N");
                track = CreateTrack(value, value.TrackId, string.Empty, AssociationState.New, ["no_confident_prior_match"], 0);
            }
            currentWorldToTrack[value.WorldObjectId] = value.TrackId;
            tracks.Add(track);
        }
        return tracks;
    }

    private static string? ProviderIdentity(WorldObject value)
    {
        string Property(string key) => value.Properties.FirstOrDefault(p => p.Key == key)?.Value ?? "";
        var runtime = Property("runtimeIdentityEvidence");
        var provider = Property("providerId");
        var nativeClass = Property("className");
        if (value.ObjectKind != WorldObjectKind.AccessibilityElement || provider != "uia" || runtime.Length == 0
            || nativeClass.Length == 0 || value.ProcessInstanceEvidence.Length == 0 || value.WindowScopeRef.Length == 0) return null;
        return System.Text.Json.JsonSerializer.Serialize(new[] { value.ProcessInstanceEvidence, value.WindowScopeRef, provider,
            runtime, nativeClass, value.Role });
    }

    private static bool CompatibleScope(WorldObject current, WorldObject previous)
    {
        var sceneKey = current.ObjectKind == WorldObjectKind.WindowObject ? "sceneSurfaceKey" : "sceneOwnerSurfaceKey";
        var currentScene = SurfaceSceneReconstructor.Property(current, sceneKey);
        var previousScene = SurfaceSceneReconstructor.Property(previous, sceneKey);
        if (currentScene.Length > 0 && previousScene.Length > 0 && currentScene != previousScene) return false;
        if (!string.IsNullOrWhiteSpace(current.DisplayId) && !string.IsNullOrWhiteSpace(previous.DisplayId) && current.DisplayId != previous.DisplayId) return false;
        if (!string.IsNullOrWhiteSpace(current.ProcessInstanceEvidence) && !string.IsNullOrWhiteSpace(previous.ProcessInstanceEvidence) && current.ProcessInstanceEvidence != previous.ProcessInstanceEvidence) return false;
        if (!string.IsNullOrWhiteSpace(current.WindowScopeRef) && !string.IsNullOrWhiteSpace(previous.WindowScopeRef) && current.WindowScopeRef != previous.WindowScopeRef) return false;
        return current.ObjectKind == previous.ObjectKind;
    }

    private static WorldObject NormalizePreviousParent(WorldObject value, IReadOnlyDictionary<string, WorldObject> previous)
    {
        var clone = value.Clone();
        if (previous.TryGetValue(value.ParentWorldObjectId, out var parent)) clone.ParentWorldObjectId = parent.TrackId;
        return clone;
    }

    private static int ObjectDepth(WorldObject value) => value.ObjectKind switch
    {
        WorldObjectKind.Display => 0,
        WorldObjectKind.VisualSurface => 1,
        WorldObjectKind.WindowObject => 1,
        _ => 2,
    };

    private static Track CreateTrack(WorldObject value, string trackId, string previousId, AssociationState state, IEnumerable<string> evidence, double confidence)
    {
        var track = new Track
        {
            TrackId = trackId,
            CurrentWorldObjectId = value.WorldObjectId,
            PreviousWorldObjectId = previousId,
            AssociationState = state,
            Confidence = state == AssociationState.Matched
                ? new Confidence { State = ConfidenceState.Computed, Value = Math.Clamp(confidence, 0, 1), Rationale = "AssociationPolicyV1 evidence score" }
                : new Confidence { State = ConfidenceState.NotComputed, Rationale = "No exact association claimed" },
            Health = value.Health.Clone(),
        };
        track.AssociationEvidence.AddRange(evidence);
        return track;
    }
}
