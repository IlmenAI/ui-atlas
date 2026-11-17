using UIMaps.Protocol;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V3;

namespace UIMaps.Map.Core;

public sealed partial class UiMapService
{
    // Called within the workspace generation's write lease, before publication.
    public void SetControlIdentity(string draftRef, bool experimental,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, WorldObject>> sources,
        IReadOnlyList<ControlIdentityKnowledge>? reviewedKnowledge = null)
    {
        var draft = _store.LoadDraft(draftRef);
        var session = _store.LoadSession(draft.MappingSessionRef);
        draft.ControlIdentityPolicy = experimental ? LogicalControlIdentity.Policy : "";
        draft.LogicalControlGroups.Clear();
        draft.ControlIdentityKnowledge.Clear();
        // Persist observed dialog content with the surface, rather than deriving
        // identity from a generic title or the root of a provider query.
        foreach (var surface in draft.Entities.Where(e => e.EntityKind == MapEntityKind.MapSurface))
        {
            var contexts = surface.EvidenceLinks.Select(link =>
                sources.TryGetValue(link.SourceObservationSetRef, out var scene) && scene.TryGetValue(link.SourceObjectRef, out var node)
                    ? SurfaceContextEvidence.Describe(node, scene) : null).Where(c => c is { ClassName: "#32770", Text.Length: > 0 }).ToArray();
            if (contexts.Length == 0) continue;
            foreach (var text in contexts[0]!.Text.Where(text => contexts.All(c => c!.Text.Contains(text))))
                if (!surface.Assertions.Any(a => a.Predicate == "surface.observedText" && a.Value == text))
                    surface.Assertions.Add(new MapAssertion { AssertionRef = "uimaps:map-assertion:" + Guid.NewGuid().ToString("N"),
                        SubjectRef = surface.MapEntityRef, Predicate = "surface.observedText", Value = text,
                        EpistemicStatus = EpistemicStatus.EpistemicObserved, ReviewState = ReviewState.ReviewUnreviewed });
        }
        if (experimental)
        {
            var knowledge = (reviewedKnowledge ?? new ControlIdentityKnowledgeStore().Load()).Where(r => r.Enabled &&
                WindowsExecutableIdentity.ProcessName(r.Executable).Equals(WindowsExecutableIdentity.ProcessName(session.Target.ExecutableName), StringComparison.OrdinalIgnoreCase)).ToArray();
            draft.ControlIdentityKnowledge.AddRange(knowledge.Select(ControlIdentityKnowledge.Serialize));
            draft.LogicalControlGroups.AddRange(LogicalControlIdentity.Build(session.Target.ExecutableName, draft.Entities, sources, knowledge));
        }
        draft.Version++; draft.UpdatedAt = Now();
        draft.Etag = UiMapCanonicalizer.HashDraft(draft);
        _store.SaveSessionAndDraft(session, draft);
    }
}
