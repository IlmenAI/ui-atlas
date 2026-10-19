using System.Security.Cryptography;
using UiAtlas.Catalog;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class AtlasCatalogTests
{
    [Fact]
    public async Task CatalogProtocolRespondsWhileCancellationInputRemainsOpen()
    {
        using var temp = new TempDirectory();
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add("catalog");
        using var process = System.Diagnostics.Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new CatalogRequest("recordings", temp.Path)));
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, process.ExitCode);
            var reply = System.Text.Json.JsonSerializer.Deserialize<CatalogReply>(await output)!;
            Assert.True(reply.Success, await errors); Assert.Empty(reply.Recordings);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    private static async Task<string> Recording(string directory)
    {
        var path = Path.Combine(directory, "source.mlrec");
        Assert.Equal(0, await Program.Main(["synthetic-record", "--out", path]));
        return path;
    }
    [Fact]
    public async Task SharedCatalogBuildsImportsAndReadsTheSameAuthorityWithoutAMapService()
    {
        using var temp = new TempDirectory();
        var source = await Recording(temp.Path);
        var root = Path.Combine(temp.Path, "shared");
        await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: source));
        var recording = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings);
        Assert.Null(recording.MapId);
        var build = await AtlasCatalogCommand.ExecuteAsync(new("build", root, RecordingId: recording.Id));
        Assert.True(build.Nodes > 0); Assert.True(build.Edges > 0);
        var map = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps", root))).Maps);
        Assert.Equal(recording.Id, map.Id);
        Assert.Equal(recording.Id, Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings).MapId);
        Assert.True(AtlasMapWorkspace.Exists(map.Path));
        var archive = Path.Combine(temp.Path, "workspace.atlasmap");
        AtlasMapPackage.Export(map.Path, archive, true);
        var importedRoot = Path.Combine(temp.Path, "imported");
        var imported = await AtlasCatalogCommand.ExecuteAsync(new("import", importedRoot, SourcePath: archive));
        Assert.Equal(imported.MapId, (await AtlasCatalogCommand.ExecuteAsync(new("import", importedRoot, SourcePath: archive))).MapId);
        var restored = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps", importedRoot))).Maps);
        Assert.Equal(map.Entry, restored.Entry);
        var restoredRecording = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", importedRoot))).Recordings);
        Assert.Equal(imported.MapId, restoredRecording.MapId);
        var session = LogicalMapSessionStore.Load(new LocalArtifactCatalog(importedRoot).MapSessionPath(imported.MapId!));
        Assert.All(session.Recordings, item => Assert.True(File.Exists(item.RecordingPath)));
        await AtlasCatalogCommand.ExecuteAsync(new("build", importedRoot, RecordingId: restoredRecording.Id, MapId: restoredRecording.MapId));
        Assert.True(File.Exists(source)); Assert.True(File.Exists(archive));
    }
    [Fact]
    public async Task DuplicateRecordingImportAndCancellationPreserveData()
    {
        using var temp = new TempDirectory();
        var source = await Recording(temp.Path); var root = Path.Combine(temp.Path, "shared");
        await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: source));
        await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: source));
        var recording = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings);
        await AtlasCatalogCommand.ExecuteAsync(new("build", root, RecordingId: recording.Id));
        var map = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps", root))).Maps);
        var before = SHA256.HashData(File.ReadAllBytes(map.Path));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtlasCatalogCommand.ExecuteAsync(new("build", root, RecordingId: recording.Id), new CancellationToken(true)));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(map.Path)));
        using var lease = new FileStream(Path.Combine(AtlasMapWorkspace.RootFor(map.Path), "write.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => AtlasCatalogCommand.ExecuteAsync(new("build", root, RecordingId: recording.Id)));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(map.Path)));
    }
    [Fact]
    public async Task StaleMapActionAndIncompatibleVersionAreRejected()
    {
        using var temp = new TempDirectory();
        var source = await Recording(temp.Path); var root = Path.Combine(temp.Path, "shared");
        await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: source));
        var recording = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings);
        await AtlasCatalogCommand.ExecuteAsync(new("build", root, RecordingId: recording.Id));
        var stale = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps", root))).Maps);
        await AtlasCatalogCommand.ExecuteAsync(new("build", root, RecordingId: recording.Id));
        await Assert.ThrowsAsync<IOException>(() => AtlasCatalogCommand.ExecuteAsync(new("archive", root, MapId: stale.Id, Entry: stale.Entry, Generation: stale.Generation)));
        await Assert.ThrowsAsync<InvalidDataException>(() => AtlasCatalogCommand.ExecuteAsync(new("maps", root, Version: 99)));
    }
    [Fact]
    public async Task LegacyInventoryIsReadOnlyAndOnlyOffersAtlasArtifacts()
    {
        using var temp = new TempDirectory();
        var source = await Recording(temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, "legacy-map.db"), "old private map");
        var root = Path.Combine(temp.Path, "shared");
        var inventory = await AtlasCatalogCommand.ExecuteAsync(new("legacy-inventory", root, SourcePath: temp.Path));
        Assert.Equal(source, Assert.Single(inventory.Imports).Path);
        Assert.Empty((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings);
        Assert.True(File.Exists(Path.Combine(temp.Path, "legacy-map.db")));
    }
}
