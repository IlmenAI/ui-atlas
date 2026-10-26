using UiAtlas.Catalog;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class RecordingLibraryArchiveTests
{
    [Fact]
    public async Task ArchiveRoundTripPersistsWithoutMovingOrChangingEvidence()
    {
        using var temp = new TempDirectory();
        var catalog = new LocalArtifactCatalog(temp.Path);
        const string id = "recording-archive-test";
        var path = SyntheticBundleFactory.Create(catalog.RecordingsDirectory, id + ".mlrec");
        var bytes = File.ReadAllBytes(path);
        Assert.False(Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", temp.Path))).Recordings).Archived);
        await AtlasCatalogCommand.ExecuteAsync(new("recording-archive", temp.Path, RecordingId: id));
        Assert.True(new LocalArtifactCatalog(temp.Path).IsRecordingArchived(id));
        Assert.True(Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", temp.Path))).Recordings).Archived);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        await AtlasCatalogCommand.ExecuteAsync(new("recording-restore", temp.Path, RecordingId: id));
        Assert.False(Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", temp.Path))).Recordings).Archived);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("missing")]
    public async Task ArchiveRejectsMissingOrInvalidRecordings(string id)
    {
        using var temp = new TempDirectory();
        await Assert.ThrowsAnyAsync<Exception>(() => AtlasCatalogCommand.ExecuteAsync(new("recording-archive", temp.Path, RecordingId: id)));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.archived", SearchOption.AllDirectories));
    }
}
