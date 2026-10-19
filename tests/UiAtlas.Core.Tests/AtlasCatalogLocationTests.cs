using UiAtlas.Core.Cli;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

[CollectionDefinition("Atlas catalog environment", DisableParallelization = true)]
public sealed class AtlasCatalogEnvironmentCollection;

[Collection("Atlas catalog environment")]
public sealed class AtlasCatalogLocationTests
{
    [Fact]
    public async Task RecorderCatalogUsesTheSameConfiguredLibraryAsTheShell()
    {
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("UI-ATLAS_DATA_HOME");
        try
        {
            var root = Path.Combine(temp.Path, "shared");
            var recording = Path.Combine(temp.Path, "source.mlrec");
            Assert.Equal(0, await Program.Main(["synthetic-record", "--out", recording]));
            await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: recording));
            Environment.SetEnvironmentVariable("UI-ATLAS_DATA_HOME", root);
            // The recorder HUD and editor create a default LocalArtifactCatalog.
            Assert.Single(new LocalArtifactCatalog().ListRecordings());
            Assert.Empty(new LocalArtifactCatalog(Path.Combine(temp.Path, "explicit")).ListRecordings());
        }
        finally { Environment.SetEnvironmentVariable("UI-ATLAS_DATA_HOME", previous); }
    }
}
