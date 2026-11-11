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
