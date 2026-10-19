using Google.Protobuf;
using UiAtlas.Catalog;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Mapping.Atlas;
using UIMaps.Protocol;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Tests;

public sealed class AtlasExactRevisionTests
{
    [Fact]
    public async Task AtlasProducedRevisionMatchesCanonicalHashAndRemainsExactAfterArchivalAndNewPublication()
    {
        using var temp = new TempDirectory(); var root = Path.Combine(temp.Path, "catalog"); var source = Path.Combine(temp.Path, "source.mlrec");
        Assert.Equal(0, await Program.Main(["synthetic-record", "--out", source]));
        await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: source));
        var recording = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings);
        await AtlasCatalogCommand.ExecuteAsync(new("build", root, RecordingId: recording.Id));
        var selected = (await AtlasCatalogCommand.ExecuteAsync(new("maps", root))).Maps[0];
        var workspace = AtlasMapWorkspace.Open(selected.Path);
        foreach (var draft in workspace.Drafts)
            workspace.Review(new ReviewCommand { RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = draft.MapDraftRef,
                ExpectedEtag = draft.Etag, CommandKind = ReviewCommandKind.ReviewAcceptAllBlocking, ReviewedBy = "qualification", Reason = "Synthetic fixture review." });
        var revision = workspace.Publish("qualification")[0];
        var before = revision.ToByteArray();
        Assert.Equal(revision.CanonicalPayloadHash, UiMapRevisionIntegrity.Hash(revision));
        Assert.Equal("uimaps:map-revision:sha256:" + revision.CanonicalPayloadHash, revision.MapRevisionRef);
        selected = (await AtlasCatalogCommand.ExecuteAsync(new("maps", root))).Maps.Single(value => value.Revision is not null
            && UiMapRevision.Parser.ParseFrom(Convert.FromBase64String(value.Revision)).MapRevisionRef == revision.MapRevisionRef);
        var entry = MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(selected.Entry));
        await AtlasCatalogCommand.ExecuteAsync(new("ready", root, MapId: selected.Id, DraftRef: entry.MapDraftRef, Entry: selected.Entry, Generation: selected.Generation));
        var read = new CatalogRequest("revision-read", root, RevisionRef: revision.MapRevisionRef, RequireReady: true);
        var exact = (await AtlasCatalogCommand.ExecuteAsync(read)).ExactRevision!;
        Assert.True(exact.Ready); Assert.Equal(before, Convert.FromBase64String(exact.Payload));
        selected = (await AtlasCatalogCommand.ExecuteAsync(new("maps", root))).Maps.Single(value => value.Id == selected.Id && MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(value.Entry)).MapDraftRef == entry.MapDraftRef);
        await AtlasCatalogCommand.ExecuteAsync(new("archive", root, MapId: selected.Id, DraftRef: entry.MapDraftRef, Entry: selected.Entry, Generation: selected.Generation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AtlasCatalogCommand.ExecuteAsync(read));
        var archived = (await AtlasCatalogCommand.ExecuteAsync(read with { RequireReady = false })).ExactRevision!;
        Assert.True(archived.Archived); Assert.False(archived.Ready); Assert.Equal(before, Convert.FromBase64String(archived.Payload));
        // Publication changes the current reference, never the old immutable bytes.
        workspace = AtlasMapWorkspace.Open(selected.Path);
        var changed = workspace.Drafts.First(value => value.MapDraftRef == entry.MapDraftRef);
        workspace.Review(new ReviewCommand { RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = changed.MapDraftRef, ExpectedEtag = changed.Etag,
            CommandKind = ReviewCommandKind.ReviewRenameEntity, SubjectRef = changed.Entities[0].MapEntityRef, Value = "New reviewed label", ReviewedBy = "qualification", Reason = "Fixture second revision." });
        _ = workspace.Publish("qualification");
        var old = (await AtlasCatalogCommand.ExecuteAsync(read with { RequireReady = false })).ExactRevision!;
        Assert.Equal(before, Convert.FromBase64String(old.Payload));
    }
}
