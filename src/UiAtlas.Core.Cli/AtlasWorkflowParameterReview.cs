using UiAtlas.Catalog;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Storage;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

/// <summary>Review existing graph suggestions in the context of a workflow comparison.</summary>
internal static class AtlasWorkflowParameterReview
{
    internal static CatalogReply Execute(AtlasMapWorkspace workspace, UiMapRevision revision,
        CatalogRawWorldSource[] sources, string mapPath, CatalogRequest request, CancellationToken token)
    {
        var storagePath = Path.Combine(workspace.DirectoryPath, "parameter-review.json");
        var generation = Path.GetFileName(workspace.DirectoryPath);
        var mapId = Path.GetFileNameWithoutExtension(mapPath);
        var document = MapParameterizationStore.LoadFile(storagePath, workspace.Index.LogicalMapId);
        var proposals = AtlasParameterMatching.Proposals(workspace, revision, document, token);
        var review = Build(document, proposals.Applicable, revision, sources, mapId, generation, token);
        if (request.Operation == "parameter-review-read") return new() { ParameterReview = review };

        if (request.MapId != mapId || request.Generation != generation ||
            request.ExpectedReviewRevision != document.Revision ||
            request.ExpectedReviewSha256 != review.Accepted.ReviewSha256)
            throw new IOException("Atlas parameter review changed. Reload the comparison before confirming.");
        var confirmations = request.ParameterConfirmations ?? [];
        if (confirmations.Length is < 1 or > 64 ||
            confirmations.Select(c => c.ProposalId).Distinct(StringComparer.Ordinal).Count() != confirmations.Length)
            throw new InvalidDataException("Confirm between 1 and 64 distinct parameter candidates.");
        var decisions = document.Decisions.ToDictionary(d => d.ProposalId, StringComparer.Ordinal);
        foreach (var confirmation in confirmations)
        {
            token.ThrowIfCancellationRequested();
            var candidate = review.Candidates.SingleOrDefault(c => c.ProposalId == confirmation.ProposalId
                && c.SourceFingerprint == confirmation.SourceFingerprint)
                ?? throw new IOException("A candidate is no longer suggested for this exact revision. Reload the comparison.");
            if (confirmation.Note is null || confirmation.Note.Length > 16_000)
                throw new InvalidDataException("Parameter comparison note exceeds its limit.");
            var proposal = proposals.Applicable.Single(p => p.Id == candidate.ProposalId);
            decisions[proposal.Id] = new(proposal.Id, proposal.Fingerprint, ParameterReviewState.Accepted,
                proposal.ParameterName, proposal.ValueType, proposal.Scope, null, [],
                "Workflow review", DateTimeOffset.UtcNow, confirmation.Note);
        }
        // Save atomically with the editor's generation/review concurrency checks.
        // Retain other proposals and decisions, including grids and explicit denials.
        MapParameterizationStore.Save(mapPath, document with
        {
            Proposals = proposals.All, Decisions = decisions.Values.ToArray()
        }, document.Revision, storagePath);
        return new() { Message = "Parameters confirmed in Atlas. Start a new analysis to use them." };
    }

    internal static CatalogParameterReview Build(MapParameterizationDocument document, ParameterProposal[] applicable,
        UiMapRevision revision, CatalogRawWorldSource[] sources, string mapId, string generation, CancellationToken token = default)
    {
        var accepted = AtlasParameterMatching.Compile(document, applicable, revision, sources, token);
        var effective = ParameterDecisionReuse.Resolve(applicable, document.Decisions);
        var decisions = effective.ToDictionary(d => d.Key, d => d.Value.Decision, StringComparer.Ordinal);
        var suggested = applicable.Where(p => !p.IsGrid && p.PropertyName is "Name" or "Value"
            && ParameterizationAnalysis.State(p, decisions.GetValueOrDefault(p.Id)) == ParameterReviewState.Suggested).ToArray();
        var previewDocument = document with
        {
            Decisions = document.Decisions.Where(d => !suggested.Any(p => p.Id == d.ProposalId))
                .Concat(suggested.Select(p => new ParameterDecision(p.Id, p.Fingerprint, ParameterReviewState.Accepted,
                    p.ParameterName, p.ValueType, p.Scope, null, [], "Comparison preview", DateTimeOffset.UnixEpoch))).ToArray()
        };
        var preview = AtlasParameterMatching.Compile(previewDocument, applicable, revision, sources, token);
        // Conflicting masks and explicit denials withheld by the compiler cannot be confirmed here.
        var available = preview.Rules.Select(r => r.ProposalId).ToHashSet(StringComparer.Ordinal);
        var covered = accepted.Rules.Select(r => r.TargetEntityRef).ToHashSet(StringComparer.Ordinal);
        return new(mapId, generation, accepted, preview, suggested.Where(p => available.Contains(p.Id))
            .Select(p => new CatalogParameterCandidate(p.Id, p.Fingerprint, p.ParameterName, p.DisplayName,
                p.PropertyName, p.Scope, string.Join("\n", p.Evidence.Select(e => e.Summary)),
                p.ReuseKey.Length == 0 ? 1 : applicable.Count(other => other.ReuseKey == p.ReuseKey &&
                    (!decisions.ContainsKey(other.Id) || other.Id == p.Id)))).ToArray(),
            applicable.Count(p => covered.Contains(p.TargetId) && effective.TryGetValue(p.Id, out var d) && d.IsInherited));
    }
}
