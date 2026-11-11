using System.Security.Cryptography;
using UiAtlas.Catalog;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Storage;
using UIMaps.Protocol;

namespace UiAtlas.Core.Tests;

public sealed class RecordingRebuildTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedRecordingBuildPinsModePublishesAndPreservesEarlierMap(bool experimental)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source.mlrec");
        Assert.Equal(0, await Program.Main(["synthetic-record", "--out", source]));
        var root = Path.Combine(temp.Path, "catalog");
        await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: source));
        var recording = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings);
        await AtlasCatalogCommand.ExecuteAsync(new("build", root, RecordingId: recording.Id));
        var old = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root))).Maps);
        var before = SHA256.HashData(File.ReadAllBytes(old.Path));
        new MapReadinessSettingsStore(root).Save(new(false));
        var built = await AtlasCatalogCommand.ExecuteAsync(new("recording-build", root, RecordingId: recording.Id,
            ExpectedArtifactStamp: recording.Stamp, BuildSettings: new(2_000_000, 2_000_000, experimental)));
        Assert.NotEqual(old.Id, built.MapId);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(old.Path)));
        var result = Assert.IsType<CatalogRecordingBuild>(built.RecordingBuild);
        Assert.Equal(recording.Id, result.RecordingId); Assert.Empty(result.PublicationProblem);
        Assert.NotEmpty(result.PublishedRevisions);
        Assert.Equal(experimental ? LogicalControlIdentity.Policy : "", result.IdentityPolicy);
        var map = Assert.Single(built.Maps); var workspace = AtlasMapWorkspace.Open(map.Path);
        Assert.All(workspace.Index.Sources, s => Assert.Equal(recording.Id, s.BundleId));
        Assert.All(workspace.Drafts, d => Assert.Equal(result.IdentityPolicy, d.ControlIdentityPolicy));
        Assert.Equal(built.MapId, Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings).MapId);
        await Assert.ThrowsAsync<IOException>(() => AtlasCatalogCommand.ExecuteAsync(new("recording-build", root,
            RecordingId: recording.Id, ExpectedArtifactStamp: "stale", BuildSettings: new(2_000_000, 2_000_000, experimental))));
    }

    [Fact]
    public async Task RequiredReviewProducesExplicitDraftResultInsteadOfFalseReady()
    {
        using var temp = new TempDirectory(); var source = Path.Combine(temp.Path, "source.mlrec");
        Assert.Equal(0, await Program.Main(["synthetic-record", "--out", source]));
        await AtlasCatalogCommand.ExecuteAsync(new("import", temp.Path, SourcePath: source));
        var recording = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", temp.Path))).Recordings);
        var built = await AtlasCatalogCommand.ExecuteAsync(new("recording-build", temp.Path, RecordingId: recording.Id,
            ExpectedArtifactStamp: recording.Stamp, BuildSettings: new(2_000_000, 2_000_000, true)));
        Assert.NotEmpty(built.RecordingBuild!.PublicationProblem);
        Assert.Empty(built.RecordingBuild.PublishedRevisions);
        Assert.True(AtlasMapWorkspace.Exists(Assert.Single(built.Maps).Path));
    }
}
