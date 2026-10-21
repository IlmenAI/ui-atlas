using System.Security.Cryptography;
using System.Text.Json;
using UiAtlas.Catalog;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class ParameterDecisionReuseTests
{
    [Fact]
    public void OneSheetConfirmationCoversEquivalentControlsAndRetainsTheOriginalApproval()
    {
        var (graph, revision, sources) = AtlasParameterMatchingTests.Fixture();
        var proposals = ParameterizationAnalysis.Build(new(graph)).ToArray();
        var first = proposals.Single(p => p.TargetId == "sheet");
        var second = proposals.Single(p => p.TargetId == "sheet2");
        Assert.NotEmpty(first.ReuseKey); Assert.Equal(first.ReuseKey, second.ReuseKey);
        var doc = MapParameterizationDocument.Empty("map") with { Proposals = proposals, Decisions = [Accept(first)] };
        var review = AtlasWorkflowParameterReview.Build(doc, proposals, revision, sources, "map", "generation");
        var inherited = Assert.Single(review.Accepted.Rules, r => r.WorldObjectRef == "sheet2");
        Assert.Equal(first.Id, inherited.ProposalId); Assert.Equal(first.Fingerprint, inherited.SourceFingerprint);
        Assert.Equal(second.TargetId, inherited.TargetEntityRef); Assert.Equal(1, review.ReusedParameterCount);
        Assert.DoesNotContain(review.Candidates, c => c.ProposalId == second.Id);
        Assert.Single(doc.Decisions); Assert.Equal(0, doc.Revision);
    }

    [Theory]
    [InlineData(ParameterReviewState.Denied, false)]
    [InlineData(ParameterReviewState.NeedsEvidence, false)]
    [InlineData(ParameterReviewState.Suggested, false)]
    [InlineData(ParameterReviewState.Accepted, true)]
    public void IndividualDecisionOrChangedEvidenceCannotBeOverridden(ParameterReviewState state, bool stale)
    {
        var (graph, revision, sources) = AtlasParameterMatchingTests.Fixture();
        var proposals = ParameterizationAnalysis.Build(new(graph)).ToArray();
        var first = proposals.Single(p => p.TargetId == "sheet");
        var second = proposals.Single(p => p.TargetId == "sheet2");
        var local = Accept(second) with { State = state, SourceFingerprint = stale ? "changed" : second.Fingerprint };
        var doc = MapParameterizationDocument.Empty("map") with { Proposals = proposals, Decisions = [Accept(first), local] };
        var effective = ParameterDecisionReuse.Resolve(proposals, doc.Decisions)[second.Id];
        Assert.False(effective.IsInherited); Assert.Equal(local, effective.Decision);
        Assert.DoesNotContain(AtlasParameterMatching.Compile(doc, proposals, revision, sources).Rules,
            r => r.WorldObjectRef == "sheet2");
    }

    [Fact]
    public void StaleOrNarrowedConfirmationDoesNotAuthorizePeers()
    {
        var proposals = ParameterizationAnalysis.Build(new(ParameterizationTests.Graph())).ToArray();
        var first = proposals.Single(p => p.TargetId == "sheet");
        var second = proposals.Single(p => p.TargetId == "sheet2");
        foreach (var decision in new[] {
            Accept(first) with { SourceFingerprint = "old" }, Accept(first) with { Scope = "Only this control" },
            Accept(first) with { ParameterName = "specific_sheet" }, Accept(first) with { ValueType = "Number" } })
            Assert.False(ParameterDecisionReuse.Resolve(proposals, [decision]).ContainsKey(second.Id));
        Assert.False(ParameterDecisionReuse.Resolve(proposals.Where(p => p.Id != first.Id).ToArray(),
            [Accept(first)]).ContainsKey(second.Id));
    }

    [Fact]
    public void StaleLocalReviewAlsoBlocksAnAliasThroughAnotherMapEntity()
    {
        var (graph, revision, sources) = AtlasParameterMatchingTests.Fixture();
        var proposals = ParameterizationAnalysis.Build(new(graph)).ToArray();
        var first = proposals.Single(p => p.TargetId == "sheet");
        var second = proposals.Single(p => p.TargetId == "sheet2");
        revision.Entities.Single(e => e.MapEntityRef == second.TargetId).EvidenceLinks[0].SourceObjectRef = "sheet";
        var doc = MapParameterizationDocument.Empty("map") with { Proposals = proposals,
            Decisions = [Accept(first), Accept(second) with { SourceFingerprint = "old" }] };
        Assert.Empty(AtlasParameterMatching.Compile(doc, proposals, revision, sources).Rules);
    }

    [Theory]
    [InlineData("Font Size", "ribbon", true)]
    [InlineData("Font Name", "ribbon", false)]
    [InlineData("Font Size", "dialog", false)]
    public void ValueReuseRequiresTheSameSettingAndStructuralContext(string label, string parent, bool reusable)
    {
        var graph = ParameterizationTests.Graph();
        var font = graph.Nodes.Single(n => n.Id == "font");
        var container = graph.Nodes.Single(n => n.Id == "sheet-view");
        var nodes = graph.Nodes.Where(n => n.Id != "font").Concat([
            container with { Id = "ribbon", StableKey = "ribbon", Label = "Ribbon" },
            container with { Id = "dialog", StableKey = "dialog", Label = "Format dialog" },
            font with { ParentId = "ribbon" },
            font with { Id = "font-copy", StableKey = "font-copy", ParentId = parent, Label = label }
        ]).ToArray();
        var proposals = ParameterizationAnalysis.Build(new(graph with { Nodes = nodes })).ToArray();
        var first = proposals.Single(p => p.TargetId == "font");
        var copy = proposals.Single(p => p.TargetId == "font-copy");
        Assert.Equal(reusable, ParameterDecisionReuse.Resolve(proposals, [Accept(first)]).ContainsKey(copy.Id));
    }

    [Theory]
    [InlineData("className")]
    [InlineData("automationId")]
    [InlineData("semanticSelector")]
    public void DifferentStableControlIdentityPreventsReuse(string property)
    {
        var graph = ParameterizationTests.Graph();
        graph = graph with { Nodes = graph.Nodes.Select(n => n.Id == "sheet2"
            ? n with { Properties = n.Properties.Append(new GraphProperty(property,
                property == "semanticSelector" ? "automationId=different" : "different")).ToArray() } : n).ToArray() };
        var proposals = ParameterizationAnalysis.Build(new(graph)).ToArray();
        var first = proposals.Single(p => p.TargetId == "sheet");
        Assert.False(ParameterDecisionReuse.Resolve(proposals, [Accept(first)]).ContainsKey(
            proposals.Single(p => p.TargetId == "sheet2").Id));
    }

    [Theory]
    [InlineData("EXCEL", "Excel")]
    [InlineData("WINWORD", "Word")]
    [InlineData("POWERPNT", "PowerPoint")]
    public void DocumentNamesCanDifferAcrossEquivalentSurfaceCopies(string app, string suffix)
    {
        var graph = ParameterizationTests.Graph();
        graph = graph with { Nodes = graph.Nodes.Select(n => n.Id == "app" ? n with { Label = app }
            : n with { Label = n.Label.Replace("Excel", suffix) }).ToArray() };
        var copies = graph.Nodes.Where(n => n.Id != "app").Select(n => n with
        {
            Id = n.Id + "-copy", StableKey = n.StableKey + "-copy",
            ParentId = n.ParentId == "app" ? "app" : n.ParentId + "-copy",
            Label = n.Label.Replace("Book2", "Budget").Replace("Sheet1", "Overview"),
            Properties = n.Properties.Select(p => p.Name == "semanticSurfaceId"
                ? p with { Value = "surface-copy" } : p).ToArray()
        });
        var proposals = ParameterizationAnalysis.Build(new(graph with { Nodes = graph.Nodes.Concat(copies).ToArray() })).ToArray();
        foreach (var id in app == "EXCEL" ? new[] { "caption", "sheet", "font" } : new[] { "caption", "font" })
        {
            var first = proposals.Single(p => p.TargetId == id);
            var copy = proposals.Single(p => p.TargetId == id + "-copy");
            Assert.NotEmpty(first.ReuseKey); Assert.Equal(first.ReuseKey, copy.ReuseKey);
            Assert.True(ParameterDecisionReuse.Resolve(proposals, [Accept(first)])[copy.Id].IsInherited);
        }
        // Manual or evidence-only variation does not create a broad role.
        Assert.Empty(proposals.Single(p => p.TargetId == "changing").ReuseKey);
        var caption = proposals.Single(p => p.TargetId == "caption");
        Assert.False(ParameterDecisionReuse.Resolve(proposals.Select(p => p.Id == caption.Id
            ? p with { IsManual = true } : p).ToArray(), [Accept(caption)]).ContainsKey(
            proposals.Single(p => p.TargetId == "caption-copy").Id));
    }

    [Fact]
    public async Task ExistingRetainedExcelConfirmationsAreReusedWithoutWritingTheReview()
    {
        var path = Environment.GetEnvironmentVariable("UIATLAS_PARAMETER_REUSE_MAP");
        var reference = Environment.GetEnvironmentVariable("UIATLAS_PARAMETER_REUSE_REVISION");
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(reference)) return;
        var workspace = AtlasMapWorkspace.Open(path);
        var reviewPath = MapParameterizationStore.PathFor(path);
        var before = File.ReadAllBytes(reviewPath);
        var document = MapParameterizationStore.Load(path, workspace.Index.LogicalMapId);
        var response = await AtlasCatalogCommand.ExecuteAsync(new("parameter-review-read",
            Path.GetDirectoryName(Path.GetDirectoryName(path))!, RevisionRef: reference));
        var review = response.ParameterReview!;
        Assert.True(review.ReusedParameterCount > 0);
        Assert.All(review.Accepted.Rules, rule => Assert.Contains(document.Decisions,
            d => d.ProposalId == rule.ProposalId && d.SourceFingerprint == rule.SourceFingerprint &&
                 d.State == ParameterReviewState.Accepted));
        Assert.Equal(before, File.ReadAllBytes(reviewPath));
        if (Environment.GetEnvironmentVariable("UIATLAS_PARAMETER_REUSE_OUTPUT") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "retained-candidates.json"), JsonSerializer.Serialize(response));
            File.WriteAllText(Path.Combine(output, "retained-reuse.json"), JsonSerializer.Serialize(new {
                reference, ReviewHash = Convert.ToHexString(SHA256.HashData(before)),
                Decisions = document.Decisions.Count, ReusedControls = review.ReusedParameterCount,
                ExactPropertyBindings = review.Accepted.Rules.Length, UnconfirmedCandidates = review.Candidates.Length,
                ReviewUnchanged = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static ParameterDecision Accept(ParameterProposal p) => new(p.Id, p.Fingerprint,
        ParameterReviewState.Accepted, p.ParameterName, p.ValueType, p.Scope, null, [], "test", DateTimeOffset.UnixEpoch);
}
