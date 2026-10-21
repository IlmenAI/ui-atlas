using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using UiAtlas.Catalog;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Mapping.Atlas;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Tests;

public sealed class MapReadinessTests
{
    [Fact]
    public void SettingsDefaultToReviewAndPersistForBothLaunchers()
    {
        using var temp = new TempDirectory();
        var atlas = new MapReadinessSettingsStore(temp.Path);
        var mapLock = new MapReadinessSettingsStore(temp.Path);
        Assert.True(atlas.Load().RequireReview);
        atlas.Save(new(false)); Assert.False(mapLock.Load().RequireReview);
        mapLock.Save(new(true)); Assert.True(atlas.Load().RequireReview);
        var path = Path.Combine(temp.Path, "map-readiness-settings.json");
        File.WriteAllText(path, "{}"); Assert.True(atlas.Load().RequireReview);
        File.WriteAllText(path, "broken"); Assert.Throws<JsonException>(() => atlas.Load());
    }

    [Fact]
    public async Task MarkReadyUsesSavedPolicyAndRecordsAutomaticAcceptanceWithoutClaimingHumanReview()
    {
        using var temp = new TempDirectory();
        var map = await Build(temp.Path);
        var workspace = AtlasMapWorkspace.Open(map.Path);
        var before = Assert.Single(workspace.Drafts);
        Assert.Contains(before.ReviewItems, item => item.Blocking && item.ReviewState == ReviewState.ReviewUnreviewed);
        var ready = new CatalogRequest("ready", temp.Path, MapId: map.Id, DraftRef: before.MapDraftRef,
            Entry: map.Entry, Generation: map.Generation);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => AtlasCatalogCommand.ExecuteAsync(ready));
        Assert.Contains("Require review before marking UI maps Ready", error.Message);
        Assert.Equal(before.Etag, Assert.Single(workspace.Drafts).Etag);
        new MapReadinessSettingsStore(temp.Path).Save(new(false));
        var result = await AtlasCatalogCommand.ExecuteAsync(ready);
        var revision = UiMapRevision.Parser.ParseFrom(Convert.FromBase64String(result.Revision!));
        Assert.NotEmpty(revision.Entities);
        var record = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps-summary", temp.Path))).Maps);
        var entry = MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(record.Entry));
        Assert.True(entry.IsReady);
        Assert.Equal(revision.MapRevisionRef, entry.LatestMapRevisionRef);
        Assert.DoesNotContain(Assert.Single(workspace.Drafts).ReviewItems,
            item => item.Blocking && item.ReviewState == ReviewState.ReviewUnreviewed);
        using var database = OpenDatabase(workspace);
        using var query = database.CreateCommand();
        query.CommandText = "SELECT command_payload FROM review_commands";
        var command = ReviewCommand.Parser.ParseFrom((byte[])query.ExecuteScalar()!);
        Assert.Equal("policy:manual-review-disabled", command.ReviewedBy);
        Assert.Contains("accepted automatically", command.Reason);
        var exact = (await AtlasCatalogCommand.ExecuteAsync(new("revision-read", temp.Path,
            MapId: map.Id, RevisionRef: revision.MapRevisionRef, RequireReady: true))).ExactRevision;
        Assert.True(exact!.Ready);
        // Restoring the default does not revoke an existing Ready revision.
        new MapReadinessSettingsStore(temp.Path).Save(new());
        var repeated = await AtlasCatalogCommand.ExecuteAsync(ready);
        Assert.Equal(result.Revision, repeated.Revision);
    }

    [Fact]
    public async Task ReviewDisabledStillRejectsStructuralErrorsBeforeChangingReviewState()
    {
        using var temp = new TempDirectory();
        var map = await Build(temp.Path);
        var workspace = AtlasMapWorkspace.Open(map.Path);
        var draft = Assert.Single(workspace.Drafts);
        draft.Entities.Single(value => value.EntityKind == MapEntityKind.MapApplication).ParentEntityRef = "missing-parent";
        SaveDraft(workspace, draft);
        new MapReadinessSettingsStore(temp.Path).Save(new(false));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => AtlasCatalogCommand.ExecuteAsync(
            new("ready", temp.Path, MapId: map.Id, DraftRef: draft.MapDraftRef, Entry: map.Entry, Generation: map.Generation)));
        Assert.Contains("MAP_GRAPH_PARENT_MISSING", error.Message);
        Assert.Equal(draft, Assert.Single(workspace.Drafts));
        using var database = OpenDatabase(workspace);
        foreach (var table in new[] { "review_commands", "revisions", "ready_map_selections" })
        {
            using var query = database.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM " + table;
            Assert.Equal(0L, query.ExecuteScalar());
        }
    }

    [Fact]
    public async Task AutomaticAcceptancePreservesRejectedVariantsAndDoesNotPublishOtherDrafts()
    {
        using var temp = new TempDirectory();
        var map = await Build(temp.Path);
        var workspace = AtlasMapWorkspace.Open(map.Path);
        var draft = Assert.Single(workspace.Drafts);
        var surface = draft.Entities.First(value => value.EntityKind == MapEntityKind.MapSurface);
        draft.Entities.Add(new MapEntity
        {
            MapEntityRef = "rejected-variant", ParentEntityRef = surface.MapEntityRef,
            ApplicationRef = draft.ApplicationRef, EntityKind = MapEntityKind.MapSurfaceVariant,
            ReviewState = ReviewState.ReviewRejected,
        });
        SaveDraft(workspace, draft);
        // An unrelated draft in the same workspace must not block or be accepted
        // by the selected map's Mark ready action.
        var other = new UiMapDraft
        {
            MapDraftRef = "unrelated-draft", MappingSessionRef = "unrelated-session", ApplicationRef = "unrelated-app",
            ReviewItems = { new ReviewItem { SubjectRef = "unrelated-control", Blocking = true, ReviewState = ReviewState.ReviewUnreviewed } },
        };
        using (var database = OpenDatabase(workspace))
        {
            using var insert = database.CreateCommand();
            insert.CommandText = "INSERT INTO drafts(draft_ref,payload,updated_at) VALUES($ref,$payload,$time)";
            insert.Parameters.AddWithValue("$ref", other.MapDraftRef);
            insert.Parameters.AddWithValue("$payload", other.ToByteArray());
            insert.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }
        File.WriteAllText(Path.Combine(workspace.DirectoryPath, "atlas-map.json"), JsonSerializer.Serialize(
            workspace.Index with { Sessions = [.. workspace.Index.Sessions, new("other", other.MappingSessionRef, other.MapDraftRef)] }));
        workspace = AtlasMapWorkspace.Open(map.Path);
        var revision = workspace.MarkReady(draft.MapDraftRef, "test", requireReview: false);
        Assert.DoesNotContain(revision.Entities, value => value.MapEntityRef == "rejected-variant");
        Assert.Equal(ReviewState.ReviewRejected, workspace.Service.GetDraft(draft.MapDraftRef).Entities
            .Single(value => value.MapEntityRef == "rejected-variant").ReviewState);
        Assert.Equal(other, workspace.Service.GetDraft(other.MapDraftRef));
    }

    private static async Task<CatalogMap> Build(string root)
    {
        var source = Path.Combine(root, "source.mlrec");
        Assert.Equal(0, await Program.Main(["synthetic-record", "--out", source]));
        await AtlasCatalogCommand.ExecuteAsync(new("import", root, SourcePath: source));
        var recording = Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("recordings", root))).Recordings);
        await AtlasCatalogCommand.ExecuteAsync(new("build", root, RecordingId: recording.Id));
        return Assert.Single((await AtlasCatalogCommand.ExecuteAsync(new("maps", root))).Maps);
    }

    private static SqliteConnection OpenDatabase(AtlasMapWorkspace workspace)
    {
        var database = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(workspace.DirectoryPath, "map", "map.db"), Pooling = false }.ToString());
        database.Open();
        return database;
    }

    private static void SaveDraft(AtlasMapWorkspace workspace, UiMapDraft draft)
    {
        using var database = OpenDatabase(workspace);
        using var update = database.CreateCommand();
        update.CommandText = "UPDATE drafts SET payload=$payload WHERE draft_ref=$ref";
        update.Parameters.AddWithValue("$payload", draft.ToByteArray());
        update.Parameters.AddWithValue("$ref", draft.MapDraftRef);
        Assert.Equal(1, update.ExecuteNonQuery());
    }
}
