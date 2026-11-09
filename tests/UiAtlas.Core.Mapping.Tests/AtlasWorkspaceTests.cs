using System.Diagnostics;
using System.Security.Cryptography;
using Google.Protobuf;
using UiAtlas.Core.Build;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Storage;
using UIMaps.Map.Core;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V3;
using Xunit;

namespace UiAtlas.Core.Mapping.Tests;

public sealed class AtlasWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atlas-workspace-tests-" + Guid.NewGuid().ToString("N"));
    public AtlasWorkspaceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private string Map => Path.Combine(_root, "application.db");
    [Fact]
    public async Task CaptureAbove256MiBCanBuildReopenAndRoundTripWithItsEvidence()
    {
        Assert.Equal(CaptureSizeLimits.MaximumEpisodeBytes, RecordingContractLimits.MaxEvidenceEntryBytes);
        var initial = await Record();
        var amended = Path.Combine(_root, "large-capture.mlrec");
        const string nativeEntry = "raw/native/frame-000001.episode.pb";
        byte[] expectedHash;
        string windowId;
        using (var bundle = UiAtlas.Core.Recording.RecordingBundle.Open(initial))
        using (var writer = new UiAtlas.Core.Recording.RecordingBundleWriter(Path.Combine(_root, "staging")))
        {
            foreach (var entry in bundle.Entries.Where(entry => entry is not ("manifest.json" or "hashes.sha256")))
                writer.WriteBytes(entry, bundle.ReadBytes(entry));
            var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
            var frame = bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json");
            var episode = AtlasEpisodeAdapter.Convert(bundle, manifest, frame);
            episode.EffectiveCapturePolicy.MaxBytesPerEpisode = 384L * CaptureSizeLimits.MiB;
            windowId = episode.SensorPackets.Single(p => p.WindowTopology is not null).WindowTopology.Windows[0].WindowEvidenceId;
            // Unknown protobuf fields are retained evidence. Incompressible padding
            // exercises actual byte limits without constructing millions of controls.
            var padding = new byte[257 * CaptureSizeLimits.MiB];
            new Random(42).NextBytes(padding);
            using var bytes = new MemoryStream();
            using (var output = new CodedOutputStream(bytes, leaveOpen: true))
            {
                episode.WriteTo(output);
                output.WriteTag(10000, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(UnsafeByteOperations.UnsafeWrap(padding));
            }
            var native = bytes.ToArray();
            Assert.InRange(native.Length, CaptureSizeLimits.DefaultEpisodeBytes + 1, 384 * CaptureSizeLimits.MiB);
            expectedHash = SHA256.HashData(native);
            writer.WriteBytes(nativeEntry, native);
            writer.WriteJson("raw/observations/frame-000001.json", frame with { NativeEpisodeEntry = nativeEntry });
            writer.WriteJson("manifest.json", manifest with { Files = writer.DescribeEntries() });
            writer.Complete(amended);
        }
        var validation = UiAtlas.Core.Recording.RecordingBundleValidator.Validate(amended);
        Assert.True(validation.IsValid, string.Join("; ", validation.Issues.Select(i => i.Message)));
        var graph = await AtlasMapWorkspace.BuildAsync([amended], Map);
        Assert.NotEmpty(graph.Nodes);
        var workspace = AtlasMapWorkspace.Open(Map);
        Assert.Equal(graph.Nodes.Count, workspace.Project().Nodes.Count);
        var source = workspace.Index.Sources.Single(s => s.Frame == 1);
        var episodePath = workspace.Resolve(source.EpisodeFile);
        using (var saved = File.OpenRead(episodePath)) Assert.Equal(expectedHash, SHA256.HashData(saved));
        var imported = await new UiMapsEpisodeImporter().ImportAsync(episodePath, windowId, Path.Combine(_root, "episode-import"), true);
        Assert.True(imported.RetainsFullEpisode);
        var archive = Path.Combine(_root, "large.atlasmap");
        AtlasMapPackage.Export(Map, archive, true);
        var reopened = AtlasMapPackage.Import(archive, Path.Combine(_root, "reopened"), true);
        Assert.Equal(graph.Nodes.Count, AtlasMapWorkspace.Open(reopened).Project().Nodes.Count);
    }
    [Fact]
    public async Task BuildLimitFailurePreservesThePreviousMapAndWorkspace()
    {
        var bundle = await Record();
        var graph = await AtlasMapWorkspace.BuildAsync([bundle], Map, buildSettings: new());
        var savedLimits = System.Text.Json.JsonSerializer.Deserialize<MapBuildSettings>(
            File.ReadAllText(AtlasMapWorkspace.Open(Map).Resolve("build-settings.json")));
        Assert.Equal(new MapBuildSettings(), savedLimits);
        var pointer = File.ReadAllText(Path.Combine(AtlasMapWorkspace.RootFor(Map), "active.json"));
        var database = SHA256.HashData(File.ReadAllBytes(Map));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => AtlasMapWorkspace.BuildAsync([bundle], Map,
            buildSettings: new(graph.Nodes.Count - 1, graph.Edges.Count)));
        Assert.Contains("Settings > UI Map building", error.Message);
        Assert.Equal(pointer, File.ReadAllText(Path.Combine(AtlasMapWorkspace.RootFor(Map), "active.json")));
        Assert.Equal(database, SHA256.HashData(File.ReadAllBytes(Map)));
        Assert.True(File.Exists(bundle));
        Assert.Equal(graph.Nodes.Count, AtlasMapWorkspace.Open(Map).Project().Nodes.Count);
    }
    [Fact]
    public async Task WorkspaceHandoffRetainsEvidenceIsIdempotentAndRejectsEditsOrTampering()
    {
        await AtlasMapWorkspace.BuildAsync([await Record()], Map);
        var workspace = AtlasMapWorkspace.Open(Map);
        var archivePath = Path.Combine(_root, "handoff.atlasmap");
        AtlasMapPackage.Export(Map, archivePath, true);
        var request = new ImportMappingWorkspaceRequest { RequestId = Guid.NewGuid().ToString("N"), SourceFormat = AtlasMapWorkspace.Format,
            SourceArchivePath = archivePath, SourceArchiveSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant(),
            Snapshots = { workspace.Index.Sessions.Select(value => new MappingDraftSnapshot
                { Session = workspace.Service.GetSession(value.SessionRef), Draft = workspace.Service.GetDraft(value.DraftRef) }) } };
        var destination = new UiMapService(Path.Combine(_root, "maplock"));
        var changed = request.Clone(); changed.Snapshots[0].Draft.Entities[0].DisplayName = "unretained edit";
        Assert.Equal("MAP_WORKSPACE_INTEGRITY", Assert.Throws<UiMapException>(() => destination.ImportWorkspace(changed)).Code);
        var imported = destination.ImportWorkspace(request);
        Assert.False(imported.AlreadyPresent);
        Assert.True(destination.ImportWorkspace(request).AlreadyPresent);
        var draft = destination.GetDraft(imported.MapDraftRefs[0]);
        var entity = draft.Entities.First(value => value.EntityKind == MapEntityKind.MapControl);
        destination.ApplyReviewCommand(Command(draft, ReviewCommandKind.ReviewRenameEntity, entity.MapEntityRef, "private edit"));
        Assert.Equal("MAP_WORKSPACE_CONFLICT", Assert.Throws<UiMapException>(() => destination.ImportWorkspace(request)).Code);
        Assert.Equal("private edit", destination.GetDraft(draft.MapDraftRef).Entities.Single(value => value.MapEntityRef == entity.MapEntityRef).DisplayName);
        var corrupt = request.Clone(); corrupt.SourceArchiveSha256 = new string('0', 64);
        Assert.Equal("MAP_WORKSPACE_INTEGRITY", Assert.Throws<UiMapException>(() => destination.ImportWorkspace(corrupt)).Code);
    }
    private async Task<string> Record(string name = "recording")
    {
        var path = Path.Combine(_root, name + ".mlrec");
        var result = await Cli("synthetic-record", "--out", path);
        Assert.True(result.Code == 0, result.Error);
        return path;
    }
    [Fact]
    public async Task DefaultCliBuildUsesMapsAndRetainsOriginalEvidenceAcrossAllThreeEditorLayers()
    {
        var bundle = await Record();
        var before = SHA256.HashData(File.ReadAllBytes(bundle));
        var result = await Cli("build", bundle, "--out", Map);
        Assert.True(result.Code == 0, result.Error);
        var workspace = AtlasMapWorkspace.Open(Map);
        var graph = workspace.Project();
        Assert.True(GraphValidator.Validate(graph).IsValid);
        Assert.Contains(graph.Nodes, node => node.Properties.Any(property => property is { Name: "mappingEngine", Value: "UIMaps" }));
        foreach (var layer in new[] { "raw-data-streams", "raw-world", "semantic-world" })
            Assert.Contains(graph.Nodes, node => node.Properties.Any(property => property.Name == "layer" && property.Value == layer));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(bundle)));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(workspace.Resolve(Assert.Single(workspace.Index.BundleFiles)))));
        Assert.Equal(2, workspace.Index.Sources.Count);
        foreach (var source in workspace.Index.Sources)
        {
            var episode = SensorEpisodeBundle.Parser.ParseFrom(File.ReadAllBytes(workspace.Resolve(source.EpisodeFile)));
            Assert.Equal(CaptureSemantics.Survey, episode.EffectiveCapturePolicy.CaptureSemantics);
            Assert.Equal(HealthState.Partial, episode.Health.State);
            Assert.All(episode.SensorPackets.Where(packet => packet.Accessibility is not null),
                packet => Assert.All(packet.Accessibility.Nodes, node => Assert.Equal("atlas:legacy-normalized", node.ProviderId)));
            var publication = GetPublicationResponse.Parser.ParseFrom(File.ReadAllBytes(workspace.Resolve(source.PublicationFile)));
            Assert.All(publication.Survey.WorldObjects.Where(record => record.WorldObject.ObjectKind == WorldObjectKind.WindowObject),
                record => Assert.Equal(UIMaps.Perception.Core.SurfaceSceneReconstructor.Version, UIMaps.Perception.Core.SurfaceSceneReconstructor.Property(record.WorldObject, "sceneReconstruction")));
        }
        Assert.False(Assert.Single(workspace.Validate()).PublicationEligible);
        var editor = new UiMappingReadModel(graph);
        Assert.NotNull(editor);
    }
    [Fact]
    public async Task CliInspectionUsesReconstructedWorkspaceInsteadOfStaleProjection()
    {
        var graph = await AtlasMapWorkspace.BuildAsync([await Record()], Map);
        var nodes = graph.Nodes.Select(node => node.Kind == GraphNodeKind.Surface && node.Properties.Any(property => property is { Name: "layer", Value: "raw-world" })
            ? node with { Label = "STALE_CACHED_SURFACE" } : node).ToArray();
        var stale = graph with { Nodes = nodes, Metadata = graph.Metadata with { SemanticHash = GraphSemantics.ComputeHash(nodes, graph.Edges) } };
        AtlasMapWorkspace.SaveProjection(stale, Map);
        var bytes = SHA256.HashData(File.ReadAllBytes(Map));
        var inspected = await Cli("inspect", Map, "--world", "raw");
        Assert.True(inspected.Code == 0, inspected.Error);
        Assert.DoesNotContain("STALE_CACHED_SURFACE", inspected.Output);
        Assert.Contains("world-surface:", inspected.Output);
        Assert.Equal(bytes, SHA256.HashData(File.ReadAllBytes(Map)));
    }

    [Fact]
    public async Task ReviewPersistsRejectsStaleEditsPublishesAndRoundTripsRevision()
    {
        await AtlasMapWorkspace.BuildAsync([await Record()], Map);
        var workspace = AtlasMapWorkspace.Open(Map);
        var draft = Assert.Single(workspace.Drafts);
        var entity = draft.Entities.First(entity => entity.EntityKind == MapEntityKind.MapControl);
        var renamed = workspace.Review(Command(draft, ReviewCommandKind.ReviewRenameEntity, entity.MapEntityRef, "Reviewed label"));
        Assert.Throws<UiMapException>(() => workspace.Review(Command(draft, ReviewCommandKind.ReviewRejectEntity, entity.MapEntityRef)));
        var current = Assert.Single(AtlasMapWorkspace.Open(Map).Drafts);
        Assert.Equal("Reviewed label", current.Entities.Single(value => value.MapEntityRef == entity.MapEntityRef).DisplayName);
        var cycle = Command(current, ReviewCommandKind.ReviewSetParent, entity.MapEntityRef);
        cycle.SecondaryRef = entity.MapEntityRef;
        Assert.Throws<ArgumentException>(() => workspace.Review(cycle));
        Assert.Equal(current.Etag, Assert.Single(workspace.Drafts).Etag);
        workspace.Review(Command(current, ReviewCommandKind.ReviewAcceptAllBlocking));
        var revision = Assert.Single(workspace.Publish("test-reviewer"));
        Assert.True(revision.Immutable && revision.NotAuthorization);
        var package = workspace.Service.Export(revision.MapRevisionRef, false);
        var independent = new UiMapService(Path.Combine(_root, "independent"));
        var imported = independent.Import(package.PackageBytes, true);
        Assert.True(imported.Valid && imported.Committed);
        Assert.Equal(revision.ToByteArray(), independent.GetRevision(revision.MapRevisionRef).ToByteArray());
    }
    [Fact]
    public async Task SemanticVariantsLineageAndReviewSurviveProjectionWithoutChangingAuthority()
    {
        await AtlasMapWorkspace.BuildAsync([await Record()], Map);
        var workspace = AtlasMapWorkspace.Open(Map);
        var draft = Assert.Single(workspace.Drafts);
        var variant = draft.Entities.First(e => e.EntityKind == MapEntityKind.MapSurfaceVariant && e.EvidenceLinks.Count > 0);
        var originalEtag = draft.Etag;
        var graph = workspace.Project();
        Assert.Equal(originalEtag, Assert.Single(workspace.Drafts).Etag);
        Assert.Contains(graph.Nodes, n => n.Id == variant.MapEntityRef && n.Kind == GraphNodeKind.State);
        Assert.Contains(graph.Edges, e => e.Kind == "semantic-interpreted-from" && e.ToId == variant.MapEntityRef);
        Assert.True(GraphValidator.Validate(GraphExport.ApplyProfile(graph, false)).IsValid);
        var edge = graph.Edges.First(e => e.Kind == "semantic-interpreted-from");
        var badEdges = graph.Edges.Select(e => e.Id == edge.Id ? e with { FromId = graph.Nodes.First(n => n.Kind == GraphNodeKind.Application).Id } : e).ToArray();
        var bad = graph with { Edges = badEdges, Metadata = graph.Metadata with { SemanticHash = GraphSemantics.ComputeHash(graph.Nodes, badEdges) } };
        Assert.Contains(GraphValidator.Validate(bad).Issues, i => i.Code == "graph.semantic-lineage");
        workspace.Review(Command(draft, ReviewCommandKind.ReviewRejectEntity, variant.MapEntityRef));
        var rejected = AtlasMapWorkspace.Open(Map);
        var projected = rejected.Project();
        Assert.True(GraphValidator.Validate(projected).IsValid);
        var reader = new UiMappingReadModel(projected).LayerFor(UiUnderstandingLevel.SemanticWorld);
        Assert.DoesNotContain(reader.Controls, c => c.Source.Properties.Any(p => p.Name == "semanticVariantRef" && p.Value == variant.MapEntityRef));
        Assert.All(reader.Surfaces.SelectMany(s => s.Variants).Where(v => v.SemanticVariantId == variant.MapEntityRef), v => Assert.False(v.IsVisibleByDefault));
        Assert.Contains(rejected.Drafts.SelectMany(d => d.Entities), e => e.MapEntityRef == variant.MapEntityRef && e.ReviewState == ReviewState.ReviewRejected);
    }

    [Fact]
    public async Task RebuildDoesNotReingestOrLoseReviewAndCancelledAppendKeepsLastAuthority()
    {
        var bundle = await Record();
        await AtlasMapWorkspace.BuildAsync([bundle], Map);
        var workspace = AtlasMapWorkspace.Open(Map);
        var draft = Assert.Single(workspace.Drafts);
        var entity = draft.Entities.First(entity => entity.EntityKind == MapEntityKind.MapControl);
        workspace.Review(Command(draft, ReviewCommandKind.ReviewRenameEntity, entity.MapEntityRef, "Kept label"));
        await AtlasMapWorkspace.BuildAsync([bundle], Map);
        var rebuilt = AtlasMapWorkspace.Open(Map);
        Assert.Equal(2, rebuilt.Index.Sources.Count);
        Assert.Equal("Kept label", Assert.Single(rebuilt.Drafts).Entities.Single(value => value.MapEntityRef == entity.MapEntityRef).DisplayName);
        var pointer = File.ReadAllBytes(Path.Combine(AtlasMapWorkspace.RootFor(Map), "active.json"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtlasMapWorkspace.BuildAsync([bundle], Map, cancellationToken: cancellation.Token));
        Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(AtlasMapWorkspace.RootFor(Map), "active.json")));
        Assert.Throws<InvalidOperationException>(() => workspace.Review(Command(draft, ReviewCommandKind.ReviewAcceptAllBlocking)));
    }

    [Fact]
    public async Task RestartedApplicationRecordingExtendsOneDraftAndPreservesPriorReview()
    {
        var first = await Record();
        await AtlasMapWorkspace.BuildAsync([first], Map);
        var original = AtlasMapWorkspace.Open(Map);
        var draft = Assert.Single(original.Drafts);
        var entity = draft.Entities.First(e => e.EntityKind == MapEntityKind.MapControl);
        original.Review(Command(draft, ReviewCommandKind.ReviewRenameEntity, entity.MapEntityRef, "Retained review"));
        var firstRevision = original.MarkReady(draft.MapDraftRef, "test", requireReview: false);
        Assert.True(original.Service.IsPublishedDraftCurrent(draft.MapDraftRef));
        var second = Path.Combine(_root, "restarted.mlrec");
        using (var bundle = UiAtlas.Core.Recording.RecordingBundle.Open(first))
        using (var writer = new UiAtlas.Core.Recording.RecordingBundleWriter(Path.Combine(_root, "restart-staging")))
        {
            foreach (var entry in bundle.Entries.Where(e => e is not ("manifest.json" or "hashes.sha256")))
                writer.WriteBytes(entry, bundle.ReadBytes(entry));
            var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
            writer.WriteJson("manifest.json", manifest with
            {
                SessionId = "second-process-recording",
                Target = manifest.Target with { ProcessStartedUtc = manifest.Target.ProcessStartedUtc.AddHours(1) },
                Files = writer.DescribeEntries(),
            });
            writer.Complete(second);
        }
        var bytes = File.ReadAllBytes(second);
        await AtlasMapWorkspace.BuildAsync([first, second], Map);
        var resumed = AtlasMapWorkspace.Open(Map);
        Assert.Single(resumed.Index.Sessions);
        var combined = Assert.Single(resumed.Drafts);
        Assert.Equal(draft.MapDraftRef, combined.MapDraftRef);
        Assert.Equal(4, combined.Sources.Count);
        Assert.Equal("Retained review", combined.Entities.Single(e => e.MapEntityRef == entity.MapEntityRef).DisplayName);
        Assert.Equal(bytes, File.ReadAllBytes(second));
        Assert.Equal(2, resumed.Index.BundleFiles.Count);
        Assert.Single(resumed.Service.ListLibrary(new ListMapLibraryRequest()).Entries);
        Assert.False(resumed.Service.IsPublishedDraftCurrent(draft.MapDraftRef));
        var nextRevision = resumed.MarkReady(draft.MapDraftRef, "test", requireReview: false);
        Assert.NotEqual(firstRevision.MapRevisionRef, nextRevision.MapRevisionRef);
        Assert.Equal(4, nextRevision.Sources.Count);
        Assert.Equal(2, resumed.Service.GetRevision(firstRevision.MapRevisionRef).Sources.Count);
        Assert.True(resumed.Service.IsPublishedDraftCurrent(draft.MapDraftRef));
        Assert.Equal(nextRevision.MapRevisionRef, resumed.MarkReady(draft.MapDraftRef, "test").MapRevisionRef);
    }
    [Fact]
    public async Task ConversionCannotOverwriteAndCliRequiresCurrentEtagAndExplicitPublisher()
    {
        var bundle = await Record();
        var result = await Cli("map", "convert-recording", bundle, "--out", Map);
        Assert.True(result.Code == 0, result.Error);
        Assert.NotEqual(0, (await Cli("map", "convert-recording", bundle, "--out", Map)).Code);
        Assert.Equal(4, (await Cli("map", "validate-drafts", Map)).Code);
        Assert.Equal(0, (await Cli("MAP", "DRAFTS", Map)).Code);
        Assert.NotEqual(0, (await Cli("map", "publish", Map, "--reviewer", "test")).Code);
        var draft = Assert.Single(AtlasMapWorkspace.Open(Map).Drafts);
        var reviewed = await Cli("map", "review", Map, draft.MapDraftRef, "accept-all", "-", "--etag", draft.Etag, "--reviewer", "test");
        Assert.True(reviewed.Code == 0, reviewed.Error);
        var published = await Cli("map", "publish", Map, "--reviewer", "test");
        Assert.True(published.Code == 0, published.Error);
    }
    private static ReviewCommand Command(UiMapDraft draft, ReviewCommandKind kind, string subject = "", string value = "") => new()
    {
        RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = draft.MapDraftRef, ExpectedEtag = draft.Etag,
        CommandKind = kind, SubjectRef = subject, Value = value, ReviewedBy = "test", Reason = "Integration test review.",
    };
    [Fact]
    public async Task AttendedInteractionKeepsBeforeAndAfterWithoutInventingMouseButtonOrProviderIdentity()
    {
        var initial = await Record();
        var amended = Path.Combine(_root, "with-interaction.mlrec");
        using (var bundle = UiAtlas.Core.Recording.RecordingBundle.Open(initial))
        using (var writer = new UiAtlas.Core.Recording.RecordingBundleWriter(Path.Combine(_root, "staging")))
        {
            foreach (var entry in bundle.Entries.Where(entry => entry is not ("manifest.json" or "hashes.sha256")))
                writer.WriteBytes(entry, bundle.ReadBytes(entry));
            var frame = bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json");
            var interaction = new InteractionObservation("click-1", "click", 1, 1, InteractionActor.User,
                InteractionGestureKind.Click, InteractionActionKind.Invoke, 1, frame.Automation[1], [1], [2],
                frame.TimestampUtc.AddSeconds(1), frame.TimestampUtc.AddSeconds(2), InteractionOutcome.Succeeded);
            writer.WriteText("raw/interactions.jsonl", System.Text.Json.JsonSerializer.Serialize(interaction,
                new System.Text.Json.JsonSerializerOptions(UiAtlas.Core.Recording.JsonDefaults.Options) { WriteIndented = false }) + "\n");
            var manifest = bundle.ReadJson<RecordingManifest>("manifest.json") with { SessionId = "with-interaction", Files = writer.DescribeEntries() };
            writer.WriteJson("manifest.json", manifest); writer.Complete(amended);
        }
        await AtlasMapWorkspace.BuildAsync([amended], Map);
        var workspace = AtlasMapWorkspace.Open(Map);
        var draft = Assert.Single(workspace.Drafts);
        var observed = Assert.Single(draft.Interactions);
        Assert.True(observed.UserPerformed && observed.NotAuthorization);
        Assert.Equal(MapInteractionKind.Unspecified, observed.Kind);
        Assert.Equal("", observed.SourceWorldObjectRef);
        Assert.Equal(workspace.Index.Sources[0].SourceRef, observed.SourceObservationSetRef);
        Assert.Equal(workspace.Index.Sources[1].SourceRef, observed.ResultObservationSetRef);
        Assert.Equal(120, observed.ScreenX); Assert.Equal(140, observed.ScreenY);
        Assert.NotEmpty(observed.Limitations);
        workspace.Review(Command(draft, ReviewCommandKind.ReviewAcceptInteraction, observed.InteractionRef));
        Assert.Equal(ReviewState.ReviewAccepted, Assert.Single(Assert.Single(workspace.Drafts).Interactions).ReviewState);
    }

    [Fact]
    public async Task MissingAuthorityDatabaseIsRejectedWithoutCreatingAnEmptyDatabase()
    {
        await AtlasMapWorkspace.BuildAsync([await Record()], Map);
        var database = Path.Combine(AtlasMapWorkspace.Open(Map).DirectoryPath, "map", "map.db");
        File.Delete(database);
        Assert.Throws<InvalidDataException>(() => AtlasMapWorkspace.Open(Map));
        Assert.False(File.Exists(database));
    }
    [Fact]
    public async Task WorkspaceArchiveRoundTripsEvidenceReviewAndProjectionWithoutReplacingDestination()
    {
        await AtlasMapWorkspace.BuildAsync([await Record()], Map);
        var archive = Path.Combine(_root, "application.atlas-map");
        Assert.Throws<ArgumentException>(() => AtlasMapPackage.Export(Map, archive, false));
        AtlasMapPackage.Export(Map, archive, true);
        var output = Path.Combine(_root, "restored");
        var restored = AtlasMapWorkspace.Open(AtlasMapPackage.Import(archive, output, true));
        var original = AtlasMapWorkspace.Open(Map);
        Assert.Equal(original.Project().Metadata.SemanticHash, restored.Project().Metadata.SemanticHash);
        Assert.Equal(Assert.Single(original.Drafts).ToByteArray(), Assert.Single(restored.Drafts).ToByteArray());
        Assert.Throws<IOException>(() => AtlasMapPackage.Import(archive, output, true));
    }
    [Fact]
    public void TraversalPackageIsRejectedBeforePublishingAnyOutput()
    {
        var archive = Path.Combine(_root, "bad.atlas-map");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("map.db.uimaps/../../escaped.txt").Open())) writer.Write("bad");
        var output = Path.Combine(_root, "rejected");
        Assert.Throws<InvalidDataException>(() => AtlasMapPackage.Import(archive, output, true));
        Assert.False(Directory.Exists(output));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }
    [Fact]
    public async Task CatalogDuplicateArchiveAndDeleteCarryTheBackendAuthority()
    {
        var catalog = new LocalArtifactCatalog(Path.Combine(_root, "catalog"));
        var path = catalog.MapPath("application");
        await AtlasMapWorkspace.BuildAsync([await Record()], path, "application");
        var duplicate = catalog.DuplicateMap("application");
        Assert.Equal(duplicate.Id, AtlasMapWorkspace.Open(duplicate.MapPath).Index.LogicalMapId);
        var archived = catalog.ArchiveMap(duplicate.Id);
        Assert.True(AtlasMapWorkspace.Exists(archived));
        Assert.False(AtlasMapWorkspace.Exists(duplicate.MapPath));
        Assert.True(catalog.DeleteUiAtlasmanently("application"));
        Assert.False(Directory.Exists(AtlasMapWorkspace.RootFor(path)));
    }
    private async Task<(int Code, string Output, string Error)> Cli(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root };
        start.ArgumentList.Add(Environment.GetEnvironmentVariable("UI_ATLAS_MAPPING_CLI_PATH") ?? typeof(UiMapsImportCommand).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["UI-ATLAS_DATA_HOME"] = Path.Combine(_root, "catalog");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        return (process.ExitCode, await stdout, await stderr);
    }
}
