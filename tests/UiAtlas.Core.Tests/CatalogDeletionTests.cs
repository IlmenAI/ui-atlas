using UiAtlas.Catalog;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class CatalogDeletionTests
{
    private static async Task<CatalogRecording> BuildRecording(string root)
    {
        var source = Path.Combine(root, "source.mlrec"); Assert.Equal(0, await Program.Main(["synthetic-record", "--out", source]));
        await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: source));
        return Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings);
    }
    [Fact]
    public async Task DeletesOnlyReviewedUnusedRecordingAndItsArchiveMarker()
    {
        using var temp = new TempDirectory(); var catalog = new LocalArtifactCatalog(temp.Path);
        var target = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, "one.mlrec", includeScreenshot: true, sessionId: "one");
        var other = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, "two.mlrec", sessionId: "two"); var otherBytes = File.ReadAllBytes(other);
        catalog.SetRecordingArchived("one", true);
        var row = (await AtlasCatalogCommand.ExecuteAsync(new("recordings", temp.Path))).Recordings.Single(r => r.Id == "one");
        await AtlasCatalogCommand.ExecuteAsync(new("recording-delete", temp.Path, RecordingId: row.Id, ExpectedArtifactStamp: row.Stamp));
        Assert.False(File.Exists(target)); Assert.False(catalog.IsRecordingArchived("one")); Assert.Equal(otherBytes, File.ReadAllBytes(other));
        Assert.Equal("two", Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", temp.Path))).Recordings).Id);
    }
    [Fact]
    public void ChangedUnreviewedOrBusyRecordingCannotBeDeleted()
    {
        using var temp = new TempDirectory(); var catalog = new LocalArtifactCatalog(temp.Path);
        var path = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, "one.mlrec", includeScreenshot: true, sessionId: "one");
        var stamp = catalog.RecordingStamp("one"); File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
        Assert.Throws<IOException>(() => catalog.DeleteRecordingPermanently("one", stamp));
        Assert.Throws<IOException>(() => catalog.DeleteRecordingPermanently("one", null));
        var pending = Path.Combine(catalog.MapsDirectory, "building.db.uimaps"); Directory.CreateDirectory(pending);
        using var lease = new FileStream(Path.Combine(pending, "write.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => catalog.DeleteRecordingPermanently("one", catalog.RecordingStamp("one")));
        Assert.True(File.Exists(path));
    }
    [Fact]
    public async Task MapDeletionPersistsInvalidatesSummaryAndPreservesItsEvidence()
    {
        using var temp = new TempDirectory(); var catalog = new LocalArtifactCatalog(temp.Path);
        var recording = await BuildRecording(temp.Path); var path = catalog.RecordingPath(recording.Id);
        await AtlasCatalogCommand.ExecuteAsync(new("build", temp.Path, RecordingId: recording.Id));
        var map = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps", temp.Path))).Maps);
        var before = await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", temp.Path)); var bytes = File.ReadAllBytes(map.Path);
        await AtlasCatalogCommand.ExecuteAsync(new("map-delete", temp.Path, MapId: map.Id, Entry: map.Entry, Generation: map.Generation));
        var after = await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", temp.Path, KnownCatalogVersion: before.CatalogVersion));
        Assert.False(after.NotModified); Assert.Empty(after.Maps);
        Assert.Empty((await AtlasCatalogCommand.ExecuteAsync(new("maps", temp.Path))).Maps);
        Assert.Equal(bytes, File.ReadAllBytes(map.Path)); Assert.True(File.Exists(path));
        // A removed library entry does not make its retained source evidence disposable.
        Assert.Throws<InvalidOperationException>(() => catalog.DeleteRecordingPermanently(recording.Id, catalog.RecordingStamp(recording.Id)));
    }
    [Fact]
    public async Task StaleMapAndBusyMapCannotBeRemoved()
    {
        using var temp = new TempDirectory(); var catalog = new LocalArtifactCatalog(temp.Path);
        var recording = await BuildRecording(temp.Path);
        await AtlasCatalogCommand.ExecuteAsync(new("build", temp.Path, RecordingId: recording.Id));
        var old = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps", temp.Path))).Maps);
        await AtlasCatalogCommand.ExecuteAsync(new("build", temp.Path, RecordingId: recording.Id));
        await Assert.ThrowsAsync<IOException>(() => AtlasCatalogCommand.ExecuteAsync(new("map-delete", temp.Path, MapId: old.Id, Entry: old.Entry, Generation: old.Generation)));
        var current = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps", temp.Path))).Maps);
        using var lease = new FileStream(Path.Combine(AtlasMapWorkspace.RootFor(current.Path), "write.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => AtlasCatalogCommand.ExecuteAsync(new("map-delete", temp.Path, MapId: current.Id, Entry: current.Entry, Generation: current.Generation)));
        Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps", temp.Path))).Maps);
    }
    [Fact]
    public void CorruptReferenceInventoryBlocksRecordingDeletion()
    {
        using var temp = new TempDirectory(); var catalog = new LocalArtifactCatalog(temp.Path);
        var recording = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, "one.mlrec", includeScreenshot: true, sessionId: "one");
        File.WriteAllText(catalog.MapPath("corrupt"), "not a map");
        Assert.Throws<InvalidOperationException>(() => catalog.DeleteRecordingPermanently("one", catalog.RecordingStamp("one")));
        Assert.True(File.Exists(recording));
    }
}
