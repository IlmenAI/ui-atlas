using UiAtlas.Catalog;
using UiAtlas.Core.Mapping.Atlas;
using UIMaps.Protocol;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

internal static class AtlasWorkflowIdentityReview
{
    internal static CatalogReply Execute(AtlasMapWorkspace workspace, UiMapRevision revision, MapLibraryEntry? entry,
        string mapPath, CatalogRequest request, CancellationToken token)
    {
        if (entry is null || !entry.IsReady || entry.Archived) throw new InvalidOperationException("Identity review requires the current Ready revision.");
        var draft = workspace.Service.GetDraft(entry.MapDraftRef);
        var model = new AtlasIdentityEquivalence(revision.Entities, revision.Relationships);
        var entities = revision.Entities.ToDictionary(entity => entity.MapEntityRef);
        var candidates = model.Candidates().Select(members => new CatalogIdentityCandidate(model.Fingerprint(members),
            members.Select(model.Canonical).Order(StringComparer.Ordinal).First(), members,
            entities[members[0]].DisplayName, Describe(members))).ToArray();
        string Describe(string[] members)
        {
            var path = new List<string>(); var node = entities[members[0]]; var seen = new HashSet<string>();
            while (seen.Add(node.MapEntityRef))
            {
                if (node.EntityKind != MapEntityKind.MapSurfaceVariant) path.Add(node.DisplayName.Length > 0 ? node.DisplayName : node.Role);
                if (!entities.TryGetValue(node.ParentEntityRef, out node!)) break;
            }
            path.Reverse();
            return string.Join(" › ", path) + $"\n{members.Length} retained variants · same surface, roles and complete semantic path. Confirm one semantic identity only; runtime instances remain separate.";
        }
        var review = new CatalogIdentityReview(Path.GetFileNameWithoutExtension(mapPath), Path.GetFileName(workspace.DirectoryPath),
            revision.MapRevisionRef, draft.MapDraftRef, draft.Etag, candidates);
        if (request.Operation == "identity-review-read") return new() { IdentityReview = review };
        if (request.MapId != review.MapId || request.Generation != review.Generation || request.ExpectedDraftEtag != draft.Etag)
            throw new IOException("Atlas identity review changed. Reload before confirming.");
        var confirmations = request.IdentityConfirmations ?? [];
        if (confirmations.Length is < 1 or > 64 || confirmations.Distinct(StringComparer.Ordinal).Count() != confirmations.Length)
            throw new InvalidDataException("Choose between one and 64 distinct equivalence groups.");
        var selected = confirmations.Select(fingerprint => candidates.SingleOrDefault(candidate => candidate.Fingerprint == fingerprint)
            ?? throw new InvalidDataException("An equivalence candidate changed or is absent.")).ToArray();
        UiMapRevision? published = null;
        workspace.UpdateLibrary(service =>
        {
            var current = service.GetDraft(draft.MapDraftRef);
            var desired = selected.SelectMany(candidate => candidate.Members.Select(model.Canonical).Distinct(StringComparer.Ordinal)
                .Where(member => member != candidate.CanonicalRef).Select(member => new ReviewedMapAlias { FromEntityRef = member,
                    ToEntityRef = candidate.CanonicalRef, Reason = "Explicitly confirmed equivalent variants in Recording review. Candidate " + candidate.Fingerprint })).ToArray();
            if (current.Etag != draft.Etag || !MatchesPublication(current, service.GetSession(current.MappingSessionRef), revision, desired))
                throw new IOException("The authoring draft differs from the inspected publication. Finish or undo other edits before identity review.");
            var currentIdentity = new AtlasIdentityEquivalence(current.Entities, current.Relationships);
            var missing = desired.Where(value => currentIdentity.Canonical(value.FromEntityRef) != currentIdentity.Canonical(value.ToEntityRef)).ToArray();
            token.ThrowIfCancellationRequested();
            if (missing.Length > 0)
                current = service.ApplyReviewCommand(new ReviewCommand { RequestId = Guid.NewGuid().ToString("N"),
                    MapDraftRef = current.MapDraftRef, ExpectedEtag = current.Etag, CommandKind = ReviewCommandKind.ReviewSetAlias,
                    ReviewedBy = "Workflow identity review", Aliases = { missing } }).Draft;
            var validation = service.ValidateDraft(current.MapDraftRef, current.Etag);
            if (!validation.PublicationEligible) throw new InvalidDataException("Reviewed aliases remain in the draft; publication validation failed.");
            published = service.Publish(new PublishMapDraftRequest { RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = current.MapDraftRef,
                ExpectedEtag = current.Etag, ValidationRunRef = validation.ValidationRunRef, PublishedBy = "Workflow identity review" }).Revision;
            service.SetLibraryReadiness(new SetMapLibraryReadinessRequest { RequestId = Guid.NewGuid().ToString("N"),
                ChangedBy = "Workflow identity review", Ready = true, Entry = new MapLibraryEntry { MapDraftRef = current.MapDraftRef, MappingSessionRef = entry.MappingSessionRef } });
        });
        return new() { IdentityRevisionRef = published!.MapRevisionRef,
            Message = "Reviewed equivalences published in a new Atlas revision. Previous analyses retain their original revision." };
    }

    internal static bool MatchesPublication(UiMapDraft draft, MappingSessionDescriptor session, UiMapRevision revision,
        IReadOnlyCollection<ReviewedMapAlias>? selectedAliases = null)
    {
        // Compare the publisher's projection, including its canonical collection ordering.
        // Retain publication metadata, which cannot be reconstructed from the authoring draft.
        var projected = revision.Clone();
        projected.ApplicationRef = draft.ApplicationRef;
        projected.Target = session.Target.Clone();
        projected.ParentRevisionRefs.Clear();
        if (!string.IsNullOrWhiteSpace(draft.BaseMapRevisionRef)) projected.ParentRevisionRefs.Add(draft.BaseMapRevisionRef);
        projected.Sources.Clear(); projected.Sources.AddRange(draft.Sources.Select(value => value.Clone()));
        projected.Entities.Clear(); projected.Entities.AddRange(draft.Entities.Where(value => value.ReviewState != ReviewState.ReviewRejected).Select(value => value.Clone()));
        var included = projected.Entities.Select(value => value.MapEntityRef).ToHashSet(StringComparer.Ordinal);
        var interactions = draft.Interactions.Where(value => value.ReviewState is ReviewState.ReviewAccepted or ReviewState.ReviewCorrected).ToArray();
        var interactionRefs = interactions.Select(value => value.InteractionRef).ToHashSet(StringComparer.Ordinal);
        projected.Relationships.Clear();
        projected.Relationships.AddRange(draft.Relationships.Where(value => included.Contains(value.FromEntityRef) && included.Contains(value.ToEntityRef)
            && (string.IsNullOrWhiteSpace(value.InteractionRef) || interactionRefs.Contains(value.InteractionRef))).Select(value => value.Clone()));
        // Recover a previous interrupted confirmation only for the exact aliases
        // selected again now. All other unpublished edits still reject publication.
        var publishedRefs = revision.Relationships.Select(value => value.MapRelationshipRef).ToHashSet(StringComparer.Ordinal);
        foreach (var pending in projected.Relationships.Where(value => !publishedRefs.Contains(value.MapRelationshipRef)
            && value.RelationshipKind == MapRelationshipKind.MapAliasOf && value.ReviewState == ReviewState.ReviewAccepted
            && selectedAliases?.Any(selected => selected.FromEntityRef == value.FromEntityRef && selected.ToEntityRef == value.ToEntityRef) == true).ToArray())
            projected.Relationships.Remove(pending);
        projected.DispositionSummary.Clear(); projected.DispositionSummary.AddRange(draft.Dispositions.Select(value => value.Clone()));
        projected.Interactions.Clear(); projected.Interactions.AddRange(interactions.Select(value => value.Clone()));
        projected.GridHostDeclarations.Clear(); projected.GridHostDeclarations.AddRange(draft.GridHostDeclarations.Where(value =>
            value.ReviewState is ReviewState.ReviewAccepted or ReviewState.ReviewCorrected).Select(value => value.Clone()));
        projected.EvidenceStreamSelections.Clear(); projected.EvidenceStreamSelections.AddRange(draft.EvidenceStreamSelections.Select(value => value.Clone()));
        projected.Limitations.Clear(); projected.Limitations.AddRange(draft.Limitations);
        return UiMapRevisionIntegrity.Hash(projected) == UiMapRevisionIntegrity.Hash(revision);
    }
}
