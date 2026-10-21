using System.Security.Cryptography;
using Google.Protobuf;
using UiAtlas.Catalog;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Storage;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Tests;

public sealed class AtlasParameterMatchingTests
{
    [Theory]
    [InlineData(ParameterReviewState.Suggested, false)]
    [InlineData(ParameterReviewState.Accepted, true)]
    [InlineData(ParameterReviewState.Denied, false)]
    [InlineData(ParameterReviewState.NeedsEvidence, false)]
    public void OnlyCurrentAcceptedScalarDecisionsBecomeRules(ParameterReviewState state, bool eligible)
    {
        var (graph, revision, sources) = Fixture();
        var proposals = ParameterizationAnalysis.Build(new(graph));
        var p = proposals.Single(p => p.TargetId == "sheet");
        var doc = Document(p, state);
        var result = AtlasParameterMatching.Compile(doc, proposals, revision, sources);
        Assert.Equal(eligible, result.Rules.Length > 0);
        Assert.Equal(revision.MapRevisionRef, result.MapRevisionRef);
        Assert.Equal(doc.Revision, result.ReviewRevision);
        if (!eligible) return;
        Assert.Contains(result.Rules, r => r.WorldObjectRef == "sheet-pane" && r.Prefix == "Sheet ");
        Assert.Contains(result.Rules, r => r.WorldObjectRef == "sheet2");
        Assert.DoesNotContain(result.Rules, r => r.WorldObjectRef is "sheet-options" or "sheet-view");
        var stale = doc with { Decisions = [doc.Decisions[0] with { SourceFingerprint = new string('b', 64) }] };
        Assert.Empty(AtlasParameterMatching.Compile(stale, proposals, revision, sources).Rules);
        Assert.Empty(AtlasParameterMatching.Compile(doc, [], revision, sources).Rules);
    }

    [Fact]
    public void CaptionParametersKeepApplicationSuffixAndOnlyExactContainerCopies()
    {
        var (graph, revision, sources) = Fixture();
        var proposals = ParameterizationAnalysis.Build(new(graph));
        var p = proposals.Single(p => p.TargetId == "caption");
        var bytes = Convert.FromBase64String(sources[0].Payload);
        var result = AtlasParameterMatching.Compile(Document(p), proposals, revision, sources);
        Assert.Equal(new[] { "caption", "document", "surface", "title" }, result.Rules.Select(r => r.WorldObjectRef).Order());
        Assert.All(result.Rules.Where(r => r.WorldObjectRef != "document"), r => Assert.Equal(" - Excel", r.Suffix));
        Assert.Empty(result.Rules.Single(r => r.WorldObjectRef == "document").Suffix);
        Assert.Equal(bytes, Convert.FromBase64String(sources[0].Payload));
    }

    [Fact]
    public void CaptionModeIsFixedWhileDocumentNameVaries()
    {
        var (graph, revision, sources) = Fixture(" [Read-Only]");
        var proposals = ParameterizationAnalysis.Build(new(graph));
        var p = proposals.Single(p => p.TargetId == "caption");
        var rules = AtlasParameterMatching.Compile(Document(p), proposals, revision, sources).Rules;
        Assert.Equal(" [Read-Only] - Excel", Assert.Single(rules, r => r.WorldObjectRef == "caption").Suffix);
        Assert.Contains(rules, r => r.WorldObjectRef == "document" && r.ExpectedValue == "Book2");
    }

    [Fact]
    public void FontValueDoesNotTurnTheStableLabelIntoAParameter()
    {
        var (graph, revision, sources) = Fixture();
        var proposals = ParameterizationAnalysis.Build(new(graph));
        var p = proposals.Single(p => p.TargetId == "font");
        var result = AtlasParameterMatching.Compile(Document(p), proposals, revision, sources);
        Assert.Equal("Value", Assert.Single(result.Rules).PropertyName);
        Assert.DoesNotContain(result.Rules, r => r.WorldObjectRef == "font-edit");
    }

    [Theory]
    [InlineData("Sheet Sheet1", "Sheet ")]
    [InlineData("Sheet1", "")]
    public void OnlyExactNamedParameterCopiesCanVaryProviderIdentifiers(string identifier, string expectedPrefix)
    {
        var (graph, revision, sources) = Fixture();
        var snapshot = WorldStateSnapshot.Parser.ParseFrom(Convert.FromBase64String(sources[0].Payload));
        snapshot.WorldObjects.Single(o => o.WorldObjectId == "sheet-pane").Properties.Add(new StringProperty { Key = "automationId", Value = identifier });
        snapshot.WorldObjects.Single(o => o.WorldObjectId == "sheet").Properties.Add(new StringProperty { Key = "automationId", Value = "SheetTab" });
        var bytes = snapshot.ToByteArray(); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        sources[0] = sources[0] with { Payload = Convert.ToBase64String(bytes), PayloadSha256 = hash };
        revision.Sources[0].SourcePayloadHash = hash;
        var proposals = ParameterizationAnalysis.Build(new(graph));
        var policy = AtlasParameterMatching.Compile(Document(proposals.Single(p => p.TargetId == "sheet")), proposals, revision, sources);
        var alias = Assert.Single(policy.Rules, r => r.PropertyName == "AutomationId");
        Assert.Equal("sheet-pane", alias.WorldObjectRef); Assert.Equal(expectedPrefix, alias.Prefix);
        Assert.DoesNotContain(policy.Rules, r => r.PropertyName == "AutomationId" && r.WorldObjectRef == "sheet");
    }

    [Fact]
    public async Task RetainedOfficeParameterQualificationDoesNotWriteReviewDecisions()
    {
        var path = Environment.GetEnvironmentVariable("UIATLAS_PARAMETER_MATCHING_MAP");
        var reference = Environment.GetEnvironmentVariable("UIATLAS_PARAMETER_MATCHING_REVISION");
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(reference)) return;
        var workspace = AtlasMapWorkspace.Open(path);
        var graph = workspace.Project();
        var proposals = ParameterizationAnalysis.Build(new(graph));
        var selected = proposals.Where(p => p.ParameterName is "workbook_name" or "sheet_name"
            || p.PropertyName == "Value" && p.DisplayName.Contains("Font", StringComparison.OrdinalIgnoreCase)).ToArray();
        var document = new MapParameterizationDocument(MapParameterizationDocument.CurrentFormat, workspace.Index.LogicalMapId, 1, selected,
            selected.Select(p => Document(p).Decisions[0]).ToArray());
        var response = await AtlasCatalogCommand.ExecuteAsync(new("revision-read", Path.GetDirectoryName(Path.GetDirectoryName(path))!,
            MapId: Path.GetFileNameWithoutExtension(path), RevisionRef: reference, IncludeRawWorld: true));
        var revision = UiMapRevision.Parser.ParseFrom(Convert.FromBase64String(response.ExactRevision!.Payload));
        var policy = AtlasParameterMatching.Compile(document, selected, revision, response.RawWorldSources);
        Assert.Contains(policy.Rules, r => r.ParameterName == "workbook_name");
        Assert.Contains(policy.Rules, r => r.ParameterName == "sheet_name");
        Assert.Contains(policy.Rules, r => r.PropertyName == "Value");
        if (Environment.GetEnvironmentVariable("UIATLAS_PARAMETER_MATCHING_QA_DIR") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "retained-excel-parameter-policy.json"),
                System.Text.Json.JsonSerializer.Serialize(policy, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "retained-excel-qualification.txt"),
                $"Read-only source: {path}\nRevision: {reference}\nSynthetic accepted decisions: {selected.Length}\nCompiled exact-property rules: {policy.Rules.Length}\nOriginal saved review decisions unchanged.\n");
        }
    }

    [Theory]
    [InlineData("WINWORD", "Proposal - Word", "document_name")]
    [InlineData("POWERPNT", "Budget - PowerPoint", "presentation_name")]
    public void OfficeDocumentCaptionDiscoveryUsesExactTitleBarControl(string app, string caption, string parameter)
    {
        var graph = ParameterizationTests.Graph();
        graph = graph with { Nodes = graph.Nodes.Select(n => n.Id == "app" ? n with { Label = app }
            : n.Id is "surface" or "caption" or "title" ? n with { Label = caption } : n).ToArray() };
        var proposals = ParameterizationAnalysis.Build(new(graph));
        Assert.Equal(parameter, Assert.Single(proposals, p => p.TargetId == "caption").ParameterName);
        Assert.DoesNotContain(proposals, p => p.TargetId is "document" or "title");
    }

    [Fact]
    public async Task CatalogAdmissionSnapshotsAcceptedRulesWithoutChangingPublishedRevisionOrEvidence()
    {
        using var temp = new TempDirectory(); var source = Path.Combine(temp.Path, "source.mlrec");
        var root = Path.Combine(temp.Path, "catalog");
        Assert.Equal(0, await Program.Main(["synthetic-record", "--out", source]));
        await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: source));
        var recording = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings);
        await AtlasCatalogCommand.ExecuteAsync(new("build", root, RecordingId: recording.Id));
        var selected = (await AtlasCatalogCommand.ExecuteAsync(new("maps", root))).Maps[0];
        var workspace = AtlasMapWorkspace.Open(selected.Path);
        var revision = workspace.MarkReady(workspace.Index.Sessions[0].DraftRef, "test", requireReview: false);
        var graph = workspace.Project();
        var node = graph.Nodes.First(n => n.Kind == GraphNodeKind.Control && revision.Entities.Any(e =>
            e.MapEntityRef == n.Id && e.EvidenceLinks.Count > 0));
        var proposal = ParameterizationAnalysis.Create(node,
            UiMappingReadModel.Property(node, "semanticSurfaceId") ?? "", node.Evidence[0].Bounds ?? new(0, 0, 1, 1),
            "Name", "test_name", "Text", "Only this exact control", false,
            [new("Mapper review", "Synthetic scalar fixture", "test")], manual: true);
        var document = Document(proposal) with { LogicalMapId = workspace.Index.LogicalMapId, Revision = 0 };
        var saved = MapParameterizationStore.Save(selected.Path, document, 0);
        var request = new CatalogRequest("revision-read", root, RevisionRef: revision.MapRevisionRef, RequireReady: true, IncludeRawWorld: true);
        var admitted = await AtlasCatalogCommand.ExecuteAsync(request);
        Assert.NotEmpty(admitted.Parameterization!.Rules);
        Assert.Equal(revision.ToByteArray(), Convert.FromBase64String(admitted.ExactRevision!.Payload));
        MapParameterizationStore.Save(selected.Path, saved with { Decisions =
            [saved.Decisions[0] with { State = ParameterReviewState.Denied }] }, saved.Revision);
        var later = await AtlasCatalogCommand.ExecuteAsync(request);
        Assert.Empty(later.Parameterization!.Rules);
        Assert.NotEmpty(admitted.Parameterization.Rules);
        Assert.Equal(admitted.RawWorldSources, later.RawWorldSources);
        Assert.Equal(admitted.ExactRevision.Payload, later.ExactRevision!.Payload);
    }

    private static MapParameterizationDocument Document(ParameterProposal p, ParameterReviewState state = ParameterReviewState.Accepted) =>
        new(MapParameterizationDocument.CurrentFormat, "map", 3, [p],
            [new(p.Id, p.Fingerprint, state, p.ParameterName, p.ValueType, p.Scope, null, [], "test", DateTimeOffset.UnixEpoch)]);

    internal static (UiKnowledgeGraph, UiMapRevision, CatalogRawWorldSource[]) Fixture(string captionState = "")
    {
        var graph = ParameterizationTests.Graph();
        if (captionState.Length > 0) graph = graph with { Nodes = graph.Nodes.Select(n =>
            n.Id is "caption" or "title" or "surface" ? n with { Label = "Book2" + captionState + " - Excel" } : n).ToArray() };
        var snapshot = new WorldStateSnapshot();
        var revision = new UiMapRevision { MapRevisionRef = "revision", CanonicalPayloadHash = new string('a', 64),
            Target = new TargetApplicationIdentity { ExecutableName = "EXCEL.EXE" } };
        foreach (var node in graph.Nodes.Where(n => n.Kind != GraphNodeKind.Application))
        {
            snapshot.WorldObjects.Add(new WorldObject { WorldObjectId = node.Id, ParentWorldObjectId = node.ParentId, Name = node.Label,
                Role = UiMappingReadModel.Property(node, "controlType") ?? "window",
                ObjectKind = node.Kind == GraphNodeKind.Surface ? WorldObjectKind.WindowObject : WorldObjectKind.AccessibilityElement });
            revision.Entities.Add(new MapEntity { MapEntityRef = node.Id,
                EvidenceLinks = { new MapEvidenceLink { SourceObservationSetRef = "source", SourceObjectRef = node.Id } } });
        }
        var bytes = snapshot.ToByteArray(); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        revision.Sources.Add(new SourceObservationSet { SourceObservationSetRef = "source", SourcePayloadHash = hash });
        return (graph, revision, [new("source", "snapshot", hash, Convert.ToBase64String(bytes))]);
    }
}
