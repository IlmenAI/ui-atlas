using System.Security.Cryptography;
using Google.Protobuf;
using UiAtlas.Catalog;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;
using UIMaps.Protocol;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Tests;

public sealed class RecordingAppendTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendRetainsReviewedEntitiesReadyRevisionOtherMapAndEvidenceAndRetryIsNoOp(bool experimental)
    {
        using var temp = new TempDirectory();
        var (root, recording, map) = await Setup(temp.Path, experimental);
        var workspace = AtlasMapWorkspace.Open(map.Path);
        var draft = workspace.Drafts.Single();
        var entity = draft.Entities.First(e => e.EntityKind == MapEntityKind.MapControl);
        workspace.Review(new ReviewCommand { RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = draft.MapDraftRef,
            ExpectedEtag = draft.Etag, CommandKind = ReviewCommandKind.ReviewRenameEntity, SubjectRef = entity.MapEntityRef, Value = "Retained label", ReviewedBy = "test", Reason = "Preservation test" });
        var ready = workspace.MarkReady(draft.MapDraftRef, "test", false);
        map = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root))).Maps);
        var oldRevision = ready.ToByteArray();
        var oldEntities = workspace.Drafts.Single().Entities.Select(e => e.MapEntityRef).ToHashSet();
        var beforeSource = File.ReadAllBytes(new LocalArtifactCatalog(root).RecordingPath(recording.Id));
        var second = await ImportSecond(root, recording, temp.Path);
        var unrelated = await AtlasCatalogCommand.ExecuteAsync(new("recording-build", root, RecordingId: second.Id,
            ExpectedArtifactStamp: second.Stamp, BuildSettings: new(2_000_000, 2_000_000, !experimental)));
        var unrelatedMap = Assert.Single(unrelated.Maps);
        var unrelatedHash = SHA256.HashData(File.ReadAllBytes(unrelatedMap.Path));
        var request = Request(root, second, map);
        var result = await AtlasCatalogCommand.ExecuteAsync(request);
        Assert.Equal(map.Id, result.MapId);
        Assert.True(result.RecordingAppend!.AddedObservations > 0);
        workspace = AtlasMapWorkspace.Open(map.Path);
        Assert.True(oldEntities.IsSubsetOf(workspace.Drafts.Single().Entities.Select(e => e.MapEntityRef)));
        Assert.Equal("Retained label", workspace.Drafts.Single().Entities.Single(e => e.MapEntityRef == entity.MapEntityRef).DisplayName);
        Assert.Equal(experimental ? LogicalControlIdentity.Policy : "", workspace.Drafts.Single().ControlIdentityPolicy);
        Assert.Equal(oldRevision, workspace.Service.GetRevision(ready.MapRevisionRef).ToByteArray());
        Assert.Equal(ready.MapRevisionRef, workspace.Service.GetSession(workspace.Index.Sessions.Single().SessionRef).PublishedMapRevisionRef);
        Assert.Equal(beforeSource, File.ReadAllBytes(new LocalArtifactCatalog(root).RecordingPath(recording.Id)));
        Assert.Equal(unrelatedHash, SHA256.HashData(File.ReadAllBytes(unrelatedMap.Path)));
        var generation = workspace.DirectoryPath;
        var retry = await AtlasCatalogCommand.ExecuteAsync(request);
        Assert.True(retry.RecordingAppend!.AlreadyAttached);
        Assert.Equal(0, retry.RecordingAppend.AddedObservations);
        Assert.Equal(generation, AtlasMapWorkspace.Open(map.Path).DirectoryPath);
        var next = workspace.MarkReady(draft.MapDraftRef, "test", false);
        Assert.NotEqual(ready.MapRevisionRef, next.MapRevisionRef);
        Assert.Equal(oldRevision, workspace.Service.GetRevision(ready.MapRevisionRef).ToByteArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => AtlasCatalogCommand.ExecuteAsync(
            new("revision-read", root, MapId: map.Id, RevisionRef: ready.MapRevisionRef, RequireReady: true)));
        var replay = await AtlasCatalogCommand.ExecuteAsync(new("revision-read", root, MapId: map.Id,
            RevisionRef: ready.MapRevisionRef, RequireReady: true, AllowRetainedForReplay: true));
        Assert.Equal(oldRevision, Convert.FromBase64String(replay.ExactRevision!.Payload));
        var guard = await AtlasCatalogCommand.ExecuteAsync(new("revision-eligibility", root, MapId: map.Id,
            RevisionRef: ready.MapRevisionRef, RequireReady: true, AllowRetainedForReplay: true));
        Assert.True(guard.RevisionEligibility!.Ready);
        var latest = (await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root))).Maps.Single(m => m.Id == map.Id
            && MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(m.Entry)).MapDraftRef == draft.MapDraftRef);
        await AtlasCatalogCommand.ExecuteAsync(new("draft", root, MapId: map.Id, Generation: latest.Generation, Entry: latest.Entry, DraftRef: draft.MapDraftRef));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AtlasCatalogCommand.ExecuteAsync(new("revision-eligibility", root,
            MapId: map.Id, RevisionRef: ready.MapRevisionRef, RequireReady: true, AllowRetainedForReplay: true)));
        AtlasMapWorkspace.Open(map.Path).MarkReady(draft.MapDraftRef, "test", false);
        latest = (await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root))).Maps.Single(m => m.Id == map.Id
            && MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(m.Entry)).MapDraftRef == draft.MapDraftRef);
        await AtlasCatalogCommand.ExecuteAsync(new("archive", root, MapId: map.Id, Generation: latest.Generation, Entry: latest.Entry, DraftRef: draft.MapDraftRef));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AtlasCatalogCommand.ExecuteAsync(new("revision-eligibility", root,
            MapId: map.Id, RevisionRef: ready.MapRevisionRef, RequireReady: true, AllowRetainedForReplay: true)));
    }

    [Fact]
    public async Task ReusedRecordingIdentityWithDifferentValidContentIsRejected()
    {
        using var temp = new TempDirectory(); var (root, recording, map) = await Setup(temp.Path, false);
        var second = await ImportSecond(root, recording, temp.Path);
        var request = Request(root, second, map);
        await AtlasCatalogCommand.ExecuteAsync(request);
        var generation = AtlasMapWorkspace.Open(map.Path).DirectoryPath;
        var catalog = new LocalArtifactCatalog(root);
        var source = catalog.RecordingPath(second.Id);
        var replacement = Path.Combine(temp.Path, "changed.mlrec");
        using (var bundle = RecordingBundle.Open(source))
        using (var writer = new RecordingBundleWriter(Path.Combine(temp.Path, "changed-staging")))
        {
            foreach (var entry in bundle.Entries.Where(e => e is not ("manifest.json" or "hashes.sha256"))) writer.WriteBytes(entry, bundle.ReadBytes(entry));
            var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
            writer.WriteJson("manifest.json", manifest with { EndedUtc = manifest.EndedUtc.AddMinutes(1), Files = writer.DescribeEntries() });
            writer.Complete(replacement);
        }
        File.Copy(replacement, source, true);
        await Assert.ThrowsAsync<InvalidDataException>(() => AtlasCatalogCommand.ExecuteAsync(request with { ExpectedArtifactStamp = catalog.RecordingStamp(second.Id) }));
        Assert.Equal(generation, AtlasMapWorkspace.Open(map.Path).DirectoryPath);
    }

    [Fact]
    public async Task RejectsStaleArchivedForeignChangedAndCancelledWithoutChangingAuthority()
    {
        using var temp = new TempDirectory(); var (root, recording, map) = await Setup(temp.Path, false);
        var second = await ImportSecond(root, recording, temp.Path);
        var request = Request(root, second, map);
        var generation = AtlasMapWorkspace.Open(map.Path).DirectoryPath;
        await Assert.ThrowsAsync<IOException>(() => AtlasCatalogCommand.ExecuteAsync(request with { Generation = "stale" }));
        await Assert.ThrowsAsync<IOException>(() => AtlasCatalogCommand.ExecuteAsync(request with { ExpectedArtifactStamp = "stale" }));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtlasCatalogCommand.ExecuteAsync(request, cts.Token));
        var foreign = await ImportSecond(root, recording, temp.Path, "foreign", "other-app");
        await Assert.ThrowsAsync<InvalidDataException>(() => AtlasCatalogCommand.ExecuteAsync(Request(root, foreign, map)));
        var entry = MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(map.Entry));
        await AtlasCatalogCommand.ExecuteAsync(new("archive", root, MapId: map.Id, Generation: map.Generation, Entry: map.Entry, DraftRef: entry.MapDraftRef));
        await Assert.ThrowsAsync<InvalidDataException>(() => AtlasCatalogCommand.ExecuteAsync(request));
        Assert.Equal(generation, AtlasMapWorkspace.Open(map.Path).DirectoryPath);
    }

    [Fact]
    public async Task AppendIntentIsNotRecoveredIntoAnUnwantedStandaloneMap()
    {
        using var temp = new TempDirectory(); var (root, recording, map) = await Setup(temp.Path, false);
        var second = await ImportSecond(root, recording, temp.Path);
        var catalog = new LocalArtifactCatalog(root);
        File.WriteAllText(catalog.RecordingPath(second.Id) + ".append.json", "{}");
        CatalogMapRecovery.RecoverCompletedMaps(catalog);
        Assert.False(File.Exists(catalog.MapPath(second.Id)));
        Assert.True(File.Exists(catalog.RecordingPath(second.Id)));
    }

    private static CatalogRequest Request(string root, CatalogRecording recording, CatalogMap map) =>
        new("recording-append", root, RecordingId: recording.Id, MapId: map.Id, Generation: map.Generation,
            Entry: map.Entry, ExpectedArtifactStamp: recording.Stamp);

    private static async Task<(string Root, CatalogRecording Recording, CatalogMap Map)> Setup(string directory, bool experimental)
    {
        var source = Path.Combine(directory, "source.mlrec");
        Assert.Equal(0, await Program.Main(["synthetic-record", "--out", source]));
        var root = Path.Combine(directory, "catalog");
        await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: source));
        var recording = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings);
        var built = await AtlasCatalogCommand.ExecuteAsync(new("recording-build", root, RecordingId: recording.Id,
            ExpectedArtifactStamp: recording.Stamp, BuildSettings: new(2_000_000, 2_000_000, experimental)));
        return (root, recording, Assert.Single(built.Maps));
    }

    private static async Task<CatalogRecording> ImportSecond(string root, CatalogRecording original, string directory,
        string id = "second-recording", string? processName = null)
    {
        var source = new LocalArtifactCatalog(root).RecordingPath(original.Id);
        var output = Path.Combine(directory, id + ".mlrec");
        using (var bundle = RecordingBundle.Open(source))
        using (var writer = new RecordingBundleWriter(Path.Combine(directory, id + "-staging")))
        {
            foreach (var entry in bundle.Entries.Where(e => e is not ("manifest.json" or "hashes.sha256"))) writer.WriteBytes(entry, bundle.ReadBytes(entry));
            var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
            writer.WriteJson("manifest.json", manifest with { SessionId = id,
                Target = manifest.Target with { ProcessName = processName ?? manifest.Target.ProcessName,
                    ProcessStartedUtc = manifest.Target.ProcessStartedUtc.AddHours(1) }, Files = writer.DescribeEntries() });
            writer.Complete(output);
        }
        await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: output));
        return (await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings.Single(r => r.Id == id);
    }
}
