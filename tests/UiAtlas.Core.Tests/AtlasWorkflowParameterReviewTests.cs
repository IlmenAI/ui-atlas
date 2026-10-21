using Google.Protobuf;
using UiAtlas.Catalog;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class AtlasWorkflowParameterReviewTests
{
    [Fact]
    public void SuggestionsAreAvailableWithoutAcceptingThemAndDeniedOrStaleCandidatesStayExcluded()
    {
        var (graph, revision, sources) = AtlasParameterMatchingTests.Fixture();
        var proposals = ParameterizationAnalysis.Build(new(graph)).ToArray();
        var original = revision.ToByteArray();
        var doc = MapParameterizationDocument.Empty("map") with { Proposals = proposals };
        var review = AtlasWorkflowParameterReview.Build(doc, proposals, revision, sources, "map", "generation");
        Assert.Empty(review.Accepted.Rules); Assert.NotEmpty(review.Preview.Rules);
        Assert.Contains(review.Candidates, c => c.ParameterName == "sheet_name");
        var sheet = proposals.Single(p => p.TargetId == "sheet");
        var denied = new ParameterDecision(sheet.Id, sheet.Fingerprint, ParameterReviewState.Denied,
            sheet.ParameterName, sheet.ValueType, sheet.Scope, null, [], "test", DateTimeOffset.UtcNow);
        foreach (var decision in new[] { denied, denied with { State = ParameterReviewState.Accepted, SourceFingerprint = new string('b', 64) } })
        {
            var excluded = AtlasWorkflowParameterReview.Build(doc with { Decisions = [decision] }, proposals, revision, sources, "map", "generation");
            Assert.DoesNotContain(excluded.Candidates, c => c.ProposalId == sheet.Id);
            Assert.DoesNotContain(excluded.Preview.Rules, r => r.ProposalId == sheet.Id);
        }
        Assert.Equal(original, revision.ToByteArray());
    }

    [Fact]
    public async Task WorkflowConfirmationUsesExactCandidateAndConcurrentReviewGuardsWithoutChangingRawEvidence()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source.mlrec"); var root = Path.Combine(temp.Path, "catalog");
        Assert.Equal(0, await Program.Main(["synthetic-record", "--out", source]));
        await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: source));
        var recording = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings);
        await AtlasCatalogCommand.ExecuteAsync(new("build", root, RecordingId: recording.Id));
        var selected = (await AtlasCatalogCommand.ExecuteAsync(new("maps", root))).Maps[0];
        var workspace = AtlasMapWorkspace.Open(selected.Path);
        var revision = workspace.MarkReady(workspace.Index.Sessions[0].DraftRef, "test", requireReview: false);
        var publishedDraft = workspace.Service.GetDraft(workspace.Index.Sessions[0].DraftRef);
        var publishedSession = workspace.Service.GetSession(publishedDraft.MappingSessionRef);
        Assert.True(AtlasWorkflowIdentityReview.MatchesPublication(publishedDraft, publishedSession, revision));
        // Reordering authoring evidence is immaterial; a changed source or entity is not.
        var reordered = publishedDraft.Clone();
        foreach (var entity in reordered.Entities)
        {
            var evidence = entity.EvidenceLinks.Reverse().ToArray();
            entity.EvidenceLinks.Clear(); entity.EvidenceLinks.AddRange(evidence);
        }
        Assert.True(AtlasWorkflowIdentityReview.MatchesPublication(reordered, publishedSession, revision));
        var edited = publishedDraft.Clone(); edited.Entities[0].DisplayName += " changed";
        Assert.False(AtlasWorkflowIdentityReview.MatchesPublication(edited, publishedSession, revision));
        var pending = publishedDraft.Clone();
        var entityRefs = pending.Entities.Where(value => value.ReviewState != UIMaps.Protocol.V3.ReviewState.ReviewRejected).Take(2).Select(value => value.MapEntityRef).ToArray();
        pending.Relationships.Add(new UIMaps.Protocol.V3.MapRelationship { MapRelationshipRef = "pending-alias",
            FromEntityRef = entityRefs[0], ToEntityRef = entityRefs[1], RelationshipKind = UIMaps.Protocol.V3.MapRelationshipKind.MapAliasOf,
            ReviewState = UIMaps.Protocol.V3.ReviewState.ReviewAccepted });
        Assert.False(AtlasWorkflowIdentityReview.MatchesPublication(pending, publishedSession, revision));
        var selectedAliases = new[] { new UIMaps.Protocol.V3.ReviewedMapAlias { FromEntityRef = entityRefs[0], ToEntityRef = entityRefs[1] } };
        Assert.True(AtlasWorkflowIdentityReview.MatchesPublication(pending, publishedSession, revision, selectedAliases));
        pending.Sources[0].CoverageLimitations.Add("Unrelated unpublished change");
        Assert.False(AtlasWorkflowIdentityReview.MatchesPublication(pending, publishedSession, revision, selectedAliases));
        edited = publishedDraft.Clone(); edited.Sources[0].CoverageLimitations.Add("Unpublished change");
        Assert.False(AtlasWorkflowIdentityReview.MatchesPublication(edited, publishedSession, revision));
        var node = workspace.Project().Nodes.First(n => n.Kind == GraphNodeKind.Control && revision.Entities.Any(e =>
            e.MapEntityRef == n.Id && e.EvidenceLinks.Count > 0));
        var proposal = ParameterizationAnalysis.Create(node, UiMappingReadModel.Property(node, "semanticSurfaceId") ?? "",
            node.Evidence[0].Bounds ?? new(0, 0, 1, 1), "Name", "context_name", "Text", "Only this control", false,
            [new("Mapper review", "Suggested by the graph editor", "test")], manual: true);
        MapParameterizationStore.Save(selected.Path,
            MapParameterizationDocument.Empty(workspace.Index.LogicalMapId) with { Proposals = [proposal] }, 0);
        var read = new CatalogRequest("parameter-review-read", root, RevisionRef: revision.MapRevisionRef);
        var review = (await AtlasCatalogCommand.ExecuteAsync(read)).ParameterReview!;
        Assert.Empty(review.Accepted.Rules);
        var candidate = Assert.Single(review.Candidates, c => c.ProposalId == proposal.Id);
        var snapshot = await AtlasCatalogCommand.ExecuteAsync(new("revision-read", root, RevisionRef: revision.MapRevisionRef, IncludeRawWorld: true));
        var confirm = new CatalogRequest("parameter-confirm", root, MapId: review.MapId, Generation: review.Generation,
            RevisionRef: revision.MapRevisionRef, ExpectedReviewRevision: review.Accepted.ReviewRevision,
            ExpectedReviewSha256: review.Accepted.ReviewSha256,
            ParameterConfirmations: [new(candidate.ProposalId, candidate.SourceFingerprint, "Recorded value differs; structure agrees.")]);
        await Assert.ThrowsAsync<IOException>(() => AtlasCatalogCommand.ExecuteAsync(confirm with { Generation = "stale" }));
        await Assert.ThrowsAsync<IOException>(() => AtlasCatalogCommand.ExecuteAsync(confirm with
            { ParameterConfirmations = [new(candidate.ProposalId, new string('b', 64), "")] }));
        Assert.Empty(MapParameterizationStore.Load(selected.Path, workspace.Index.LogicalMapId).Decisions);
        await AtlasCatalogCommand.ExecuteAsync(confirm);
        await Assert.ThrowsAsync<IOException>(() => AtlasCatalogCommand.ExecuteAsync(confirm)); // stale duplicate cannot overwrite
        var stored = MapParameterizationStore.Load(selected.Path, workspace.Index.LogicalMapId);
        var decision = Assert.Single(stored.Decisions);
        Assert.Equal(ParameterReviewState.Accepted, decision.State);
        Assert.Equal("Workflow review", decision.Reviewer); Assert.Contains("structure agrees", decision.Note);
        var later = await AtlasCatalogCommand.ExecuteAsync(new("revision-read", root, RevisionRef: revision.MapRevisionRef, IncludeRawWorld: true));
        Assert.NotEmpty(later.Parameterization!.Rules); Assert.Empty(snapshot.Parameterization!.Rules);
        Assert.Equal(snapshot.RawWorldSources, later.RawWorldSources);
        Assert.Equal(snapshot.ExactRevision!.Payload, later.ExactRevision!.Payload);
        Assert.DoesNotContain((await AtlasCatalogCommand.ExecuteAsync(read)).ParameterReview!.Candidates, c => c.ProposalId == proposal.Id);
    }
}
