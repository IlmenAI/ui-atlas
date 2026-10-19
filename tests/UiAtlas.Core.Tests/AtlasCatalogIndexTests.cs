using Google.Protobuf;
using Microsoft.Data.Sqlite;
using UiAtlas.Catalog;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Storage;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Tests;

public sealed class AtlasCatalogIndexTests
{
    private static async Task<(string Root, CatalogMap Map)> Build(string directory)
    {
        var source = Path.Combine(directory, "source.mlrec");
        Assert.Equal(0, await Program.Main(["synthetic-record", "--out", source]));
        var root = Path.Combine(directory, "catalog");
        await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: source));
        var recording = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings);
        await AtlasCatalogCommand.ExecuteAsync(new("build", root, RecordingId: recording.Id));
        return (root, Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps", root))).Maps));
    }

    [Fact]
    public async Task SharedSummaryCachePreservesDisplayKnowledgeAndSkipsUnchangedResponses()
    {
        using var temp = new TempDirectory();
        var (root, full) = await Build(temp.Path);
        var reply = await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root));
        var summary = Assert.Single(reply.Maps);
        Assert.True(summary.SummaryOnly); Assert.False(full.SummaryOnly);
        Assert.Equal(full.Entry, summary.Entry); Assert.Equal(full.Session, summary.Session);
        var original = UiMapDraft.Parser.ParseFrom(Convert.FromBase64String(full.Draft!));
        var projected = UiMapDraft.Parser.ParseFrom(Convert.FromBase64String(summary.Draft!));
        Assert.NotEmpty(original.Dispositions); Assert.Empty(projected.Dispositions);
        Assert.Empty(projected.Relationships); Assert.Empty(projected.Interactions);
        Assert.Equal(original.Etag, projected.Etag);
        Assert.Equal(original.Entities.Select(e => (e.MapEntityRef, e.EntityKind, e.ParentEntityRef, e.Applicable, e.ReviewState)),
            projected.Entities.Select(e => (e.MapEntityRef, e.EntityKind, e.ParentEntityRef, e.Applicable, e.ReviewState)));
        Assert.All(projected.Entities, e => { Assert.Empty(e.Assertions); Assert.Empty(e.Matchers); Assert.Empty(e.EvidenceLinks); });
        Assert.Equal(original.Sources.Select(s => s.SourceObservationSetRef), projected.Sources.Select(s => s.SourceObservationSetRef));
        Assert.Equal(original.ReviewItems.Count(i => i.Blocking && i.ReviewState == ReviewState.ReviewUnreviewed), projected.ReviewItems.Count);
        Assert.Equal(original.Limitations, projected.Limitations);
        Assert.True(summary.Draft!.Length < full.Draft!.Length / 2);
        var cache = Path.Combine(root, AtlasCatalogIndex.CacheFileName);
        var written = File.GetLastWriteTimeUtc(cache);
        var otherClient = await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root));
        Assert.Equal(summary, Assert.Single(otherClient.Maps));
        Assert.Equal(written, File.GetLastWriteTimeUtc(cache));
        var unchanged = await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root, KnownCatalogVersion: reply.CatalogVersion));
        Assert.True(unchanged.NotModified); Assert.Empty(unchanged.Maps);
        Assert.Equal(reply.CatalogVersion, unchanged.CatalogVersion);
        var details = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("map-detail", root,
            MapId: summary.Id, Generation: summary.Generation, Entry: summary.Entry))).Maps);
        Assert.False(details.SummaryOnly); Assert.Equal(full.Draft, details.Draft);
        File.WriteAllText(cache, "{broken cache");
        Assert.Equal(summary, Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root))).Maps));
    }

    [Fact]
    public async Task CrossClientArchiveReviewPublishRebuildAndDeletionInvalidateTheIndex()
    {
        using var temp = new TempDirectory();
        var (root, _) = await Build(temp.Path);
        var first = await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root));
        var row = Assert.Single(first.Maps);
        var entry = MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(row.Entry));
        await AtlasCatalogCommand.ExecuteAsync(new("archive", root, MapId: row.Id, DraftRef: entry.MapDraftRef, Generation: row.Generation, Entry: row.Entry));
        var archived = await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root, KnownCatalogVersion: first.CatalogVersion));
        Assert.False(archived.NotModified); Assert.NotEqual(first.CatalogVersion, archived.CatalogVersion);
        var archivedRow = Assert.Single(archived.Maps);
        Assert.True(MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(archivedRow.Entry)).Archived);
        await Assert.ThrowsAsync<IOException>(() => AtlasCatalogCommand.ExecuteAsync(new("map-detail", root, MapId: row.Id, Generation: row.Generation, Entry: row.Entry)));
        await AtlasCatalogCommand.ExecuteAsync(new("restore", root, MapId: row.Id, DraftRef: entry.MapDraftRef, Generation: row.Generation, Entry: archivedRow.Entry));

        var workspace = AtlasMapWorkspace.Open(row.Path);
        var draft = Assert.Single(workspace.Drafts);
        workspace.Review(new ReviewCommand { RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = draft.MapDraftRef,
            ExpectedEtag = draft.Etag, CommandKind = ReviewCommandKind.ReviewAcceptAllBlocking, ReviewedBy = "test", Reason = "Test review." });
        var reviewed = await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root, KnownCatalogVersion: archived.CatalogVersion));
        Assert.False(reviewed.NotModified);
        var reviewedRow = Assert.Single(reviewed.Maps);
        Assert.Empty(UiMapDraft.Parser.ParseFrom(Convert.FromBase64String(reviewedRow.Draft!)).ReviewItems);
        await AtlasCatalogCommand.ExecuteAsync(new("ready", root, MapId: row.Id, DraftRef: entry.MapDraftRef, Generation: row.Generation, Entry: reviewedRow.Entry));
        var ready = await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root, KnownCatalogVersion: reviewed.CatalogVersion));
        var readyRow = Assert.Single(ready.Maps);
        Assert.True(MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(readyRow.Entry)).IsReady);
        Assert.False(UiMapRevision.Parser.ParseFrom(Convert.FromBase64String(readyRow.Revision!)).Immutable);
        var detail = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("map-detail", root, MapId: row.Id,
            Generation: row.Generation, Entry: readyRow.Entry))).Maps);
        var revision = UiMapRevision.Parser.ParseFrom(Convert.FromBase64String(detail.Revision!));
        Assert.True(revision.Immutable); Assert.True(revision.NotAuthorization); Assert.NotEmpty(revision.CanonicalPayloadHash);
        Assert.NotEmpty(revision.DispositionSummary);

        await AtlasCatalogCommand.ExecuteAsync(new("build", root, RecordingId: row.Id));
        var rebuilt = await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root, KnownCatalogVersion: ready.CatalogVersion));
        Assert.False(rebuilt.NotModified); Assert.NotEqual(row.Generation, Assert.Single(rebuilt.Maps).Generation);
        await Assert.ThrowsAsync<IOException>(() => AtlasCatalogCommand.ExecuteAsync(new("map-detail", root,
            MapId: row.Id, Generation: row.Generation, Entry: readyRow.Entry)));
        new LocalArtifactCatalog(root).ArchiveMap(row.Id);
        var removed = await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root, KnownCatalogVersion: rebuilt.CatalogVersion));
        Assert.False(removed.NotModified); Assert.Empty(removed.Maps);
        Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings);
    }

    [Fact]
    public async Task CommittedWalChangesInvalidateSummariesBeforeCheckpoint()
    {
        using var temp = new TempDirectory();
        var (root, map) = await Build(temp.Path);
        var workspace = AtlasMapWorkspace.Open(map.Path);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(workspace.DirectoryPath, "map", "map.db"), Pooling = false }.ToString());
        connection.Open();
        var before = await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root));
        var session = workspace.Service.GetSession(workspace.Index.Sessions.Single().SessionRef);
        session.Warnings.Add("Changed by another catalog client");
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE sessions SET payload=$payload WHERE session_ref=$ref";
        update.Parameters.AddWithValue("$payload", session.ToByteArray());
        update.Parameters.AddWithValue("$ref", session.MappingSessionRef);
        update.ExecuteNonQuery();
        var after = await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", root, KnownCatalogVersion: before.CatalogVersion));
        Assert.False(after.NotModified);
        Assert.Contains("Changed by another catalog client", MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(Assert.Single(after.Maps).Entry)).Warnings);
    }
}
