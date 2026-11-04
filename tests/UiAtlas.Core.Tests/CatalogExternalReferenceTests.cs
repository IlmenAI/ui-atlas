using UiAtlas.Catalog;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Storage;
using UiAtlas.Core.Mapping.Atlas;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Tests;

public sealed class CatalogExternalReferenceTests
{
    [Fact]
    public async Task Worker_validates_exact_bytes_before_root_and_both_permanent_delete_paths_refuse_root()
    {
        using var temp = new TempDirectory(); var token = CancellationToken.None;
        var source = Path.Combine(temp.Path, "source.mlrec");
        Assert.Equal(0, await Program.Main(["synthetic-record", "--out", source]));
        var root = Path.Combine(temp.Path, "catalog");
        await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: source), token);
        var recording = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root), token)).Recordings);
        await AtlasCatalogCommand.ExecuteAsync(new("build", root, RecordingId: recording.Id), token);
        var map = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps", root), token)).Maps);
        var workspace = AtlasMapWorkspace.Open(map.Path);
        foreach (var draft in workspace.Drafts)
            workspace.Review(new ReviewCommand { RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = draft.MapDraftRef,
                ExpectedEtag = draft.Etag, CommandKind = ReviewCommandKind.ReviewAcceptAllBlocking, ReviewedBy = "qualification", Reason = "Synthetic retention fixture review." });
        var revision = Assert.Single(workspace.Publish("qualification"));
        Assert.True(revision.Immutable); Assert.True(revision.NotAuthorization);
        Assert.Equal(revision.CanonicalPayloadHash, UIMaps.Protocol.UiMapRevisionIntegrity.Hash(revision));
        var consumer = new string('a', 64);
        var request = new CatalogRequest("semantic-reference-retain", root, MapId: map.Id, RevisionRef: revision.MapRevisionRef, ExternalConsumerId: consumer);
        var receipt = (await AtlasCatalogCommand.ExecuteAsync(request, token)).ExternalReference;
        Assert.NotNull(receipt); Assert.Equal(revision.MapRevisionRef, receipt.RevisionRef);
        Assert.Equal(receipt, (await AtlasCatalogCommand.ExecuteAsync(request, token)).ExternalReference);
        var catalog = new LocalArtifactCatalog(root);
        Assert.Throws<InvalidOperationException>(() => catalog.DeleteUiAtlasmanently(map.Id));
        Assert.Throws<InvalidOperationException>(() => catalog.DeleteMapAndUnusedRecordingsPermanently(map.Id));
        Assert.Throws<InvalidOperationException>(() => catalog.ArchiveMap(map.Id));
        if (OperatingSystem.IsWindows())
        {
            var alternate = map.Id.ToUpperInvariant(); Assert.NotEqual(map.Id, alternate);
            Assert.Throws<InvalidOperationException>(() => catalog.DeleteUiAtlasmanently(alternate));
            Assert.Throws<InvalidOperationException>(() => catalog.DeleteMapAndUnusedRecordingsPermanently(alternate));
            Assert.Throws<InvalidOperationException>(() => catalog.ArchiveMap(alternate));
        }
        Assert.True(File.Exists(catalog.MapPath(map.Id)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => AtlasCatalogCommand.ExecuteAsync(request with { ExternalConsumerId = new string('b', 64), RevisionRef = "uimaps:map-revision:sha256:" + new string('f', 64) }, token));
        Assert.Single(CatalogExternalReferenceLease.ReadSnapshot(root));
    }
    [Fact]
    public void Lease_excludes_deletion_until_registration_finishes_and_rejects_changed_consumer()
    {
        using var temp = new TempDirectory(); var catalog = new LocalArtifactCatalog(temp.Path);
        var hash = new string('c', 64); var pin = new CatalogExternalReference(new string('d', 64), "map", "uimaps:map-revision:sha256:" + hash, hash);
        using (var owner = CatalogExternalReferenceLease.Acquire(temp.Path))
        {
            Assert.Throws<IOException>(() => catalog.DeleteUiAtlasmanently("map"));
            owner.RetainValidated(pin); owner.RetainValidated(pin);
            Assert.Throws<InvalidDataException>(() => owner.RetainValidated(pin with { MapId = "different" }));
        }
        Assert.Throws<InvalidOperationException>(() => catalog.DeleteUiAtlasmanently("map"));
        Assert.Equal(pin, Assert.Single(CatalogExternalReferenceLease.ReadSnapshot(temp.Path)));
    }
    [Fact]
    public void Corrupt_reference_inventory_blocks_permanent_deletion_instead_of_treating_it_as_empty()
    {
        using var temp = new TempDirectory(); var catalog = new LocalArtifactCatalog(temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, "semantic-references.json"), "{}");
        Assert.Throws<InvalidDataException>(() => catalog.DeleteUiAtlasmanently("map"));
    }
}
