using System.Security.Cryptography;
using System.Text.Json;
using UiAtlas.Catalog;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;
using UIMaps.Protocol;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

internal static class AtlasRecordingAppend
{
    internal static CatalogMap ValidateDestination(LocalArtifactCatalog catalog, CatalogRequest request, string processName)
    {
        if (request.Version != 1) throw new InvalidDataException("Unsupported append request version.");
        var map = Select(catalog, request);
        if (map.Generation != request.Generation || map.Entry != request.Entry)
            throw new IOException("The selected map changed. Refresh Maps and select it again. The recording is retained.");
        var workspace = AtlasMapWorkspace.Open(map.Path);
        RequireApplication(workspace, processName);
        return map;
    }

    private static CatalogMap Select(LocalArtifactCatalog catalog, CatalogRequest request)
    {
        if (string.IsNullOrEmpty(request.MapId) || string.IsNullOrEmpty(request.Generation) || string.IsNullOrEmpty(request.Entry))
            throw new ArgumentException("Select an existing map before adding observations.");
        var entry = MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(request.Entry));
        if (string.IsNullOrEmpty(entry.MapDraftRef)) throw new InvalidDataException("Select the editable map, not a historical revision.");
        var map = AtlasCatalogCommand.LoadMap(catalog, request.MapId, true, CancellationToken.None)
            .SingleOrDefault(m => MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(m.Entry)).MapDraftRef == entry.MapDraftRef)
            ?? throw new InvalidDataException("The selected map is no longer available.");
        if (MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(map.Entry)).Archived)
            throw new InvalidDataException("Restore the destination map before adding observations.");
        if (string.IsNullOrEmpty(map.Generation)) throw new InvalidDataException("This legacy map must be upgraded before adding observations.");
        return map;
    }

    private static void RequireApplication(AtlasMapWorkspace workspace, string processName)
    {
        if (workspace.Index.Sessions.Count != 1)
            throw new InvalidDataException("Select a map with one application draft.");
        if (!WindowsExecutableIdentity.ProcessName(workspace.Index.ApplicationName).Equals(
                WindowsExecutableIdentity.ProcessName(processName), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The recording and destination map belong to different applications.");
    }

    internal static async Task<CatalogReply> ExecuteAsync(LocalArtifactCatalog catalog, CatalogRequest request, CancellationToken token,
        IReadOnlyList<SavedDataGridReview>? gridReviews = null)
    {
        catalog.EnsureSafe();
        var id = request.RecordingId ?? throw new ArgumentException("Select a recording.");
        var source = catalog.RecordingPath(id);
        // Hold the source against replacement while validating and ingesting its bytes.
        using var sourceLease = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (request.ExpectedArtifactStamp is null || request.ExpectedArtifactStamp != catalog.RecordingStamp(id))
            throw new IOException("The recording changed. Refresh Recordings and retry.");
        if (catalog.IsRecordingArchived(id)) throw new InvalidDataException("Restore the recording before adding it.");
        using var bundle = RecordingBundle.Open(source);
        var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
        if (manifest.SessionId != id || manifest.Outcome is not (RecordingOutcome.Complete or RecordingOutcome.Partial))
            throw new InvalidDataException("A completed, retained recording is required.");
        var validation = RecordingBundleValidator.Validate(bundle);
        if (!validation.IsValid) throw new InvalidDataException("The retained recording failed validation.");
        var hash = Convert.ToHexString(SHA256.HashData(sourceLease)).ToLowerInvariant();
        var selected = Select(catalog, request);
        using var appendLease = new FileStream(selected.Path + ".append.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var original = AtlasMapWorkspace.Open(selected.Path);
        RequireApplication(original, manifest.Target.ProcessName);
        var settingsPath = Path.Combine(original.DirectoryPath, "build-settings.json");
        var settings = File.Exists(settingsPath) ? JsonSerializer.Deserialize<MapBuildSettings>(File.ReadAllText(settingsPath))
            ?? throw new InvalidDataException("The destination build settings are empty.") : new MapBuildSettings();
        var draft = original.Drafts.Single();
        if (draft.ControlIdentityPolicy.Length > 0 && draft.ControlIdentityPolicy != LogicalControlIdentity.Policy)
            throw new InvalidDataException("This map uses an unsupported control identity policy.");
        settings = settings with { ExperimentalControlIdentity = draft.ControlIdentityPolicy.Length > 0 };
        var oldEntities = draft.Entities.Select(e => e.MapEntityRef).ToHashSet(StringComparer.Ordinal);
        var sourceCount = original.Index.Sources.Count;
        var recordingSession = catalog.MapSessionPath(id);
        gridReviews ??= File.Exists(recordingSession) ? LogicalMapSessionStore.Load(recordingSession).DataGrids : null;
        var duplicate = false;
        var graph = await AtlasMapWorkspace.BuildAsync([source], selected.Path, selected.Id, token,
            gridReviews: gridReviews, buildSettings: settings, expectedGeneration: request.Generation, retainReadyForReplay: true, validateAppend: current =>
            {
                // Library readiness/archive changes do not necessarily change generation.
                var live = Select(catalog, request);
                RequireApplication(current, manifest.Target.ProcessName);
                var retained = current.Index.Sources.Where(s => s.BundleId == id).ToArray();
                if (retained.Any(s => s.BundleHash != hash))
                    throw new InvalidDataException("A recording identity was reused with different content.");
                duplicate = retained.Length > 0;
                var schemaRepair = duplicate && (gridReviews ?? []).Any(review =>
                    current.Index.GridReviews?.SingleOrDefault(g => g.GridId == review.GridId)?.SchemaHistoryVersion is not >= 1);
                if (duplicate && !schemaRepair) return true; // Retry after a fully reconciled append.
                duplicate = false;
                if (live.Generation != request.Generation || live.Entry != request.Entry || current.Drafts.Single().Etag != draft.Etag)
                    throw new IOException("The selected map changed. Refresh Maps and select it again. The recording is retained.");
                return false;
            });
        var updated = AtlasMapWorkspace.Open(selected.Path);
        var next = updated.Drafts.Single();
        var newGrids = next.Entities.Count(e => e.EntityKind == MapEntityKind.MapDataGrid) - draft.Entities.Count(e => e.EntityKind == MapEntityKind.MapDataGrid);
        var newVersions = next.Entities.Where(e => e.HostedDataGrid is not null).Sum(e =>
            e.HostedDataGrid.SchemaVersions.Count - Math.Max(1, draft.Entities.FirstOrDefault(old => old.MapEntityRef == e.MapEntityRef)?.HostedDataGrid?.SchemaVersions.Count ?? 0));
        var recovered = next.Entities.Where(e => e.HostedDataGrid is not null).Sum(e => Math.Max(0,
            e.HostedDataGrid.ExpectedHeaders.Count(h => !string.IsNullOrWhiteSpace(h)) -
            (draft.Entities.FirstOrDefault(old => old.MapEntityRef == e.MapEntityRef)?.HostedDataGrid?.ExpectedHeaders.Count(h => !string.IsNullOrWhiteSpace(h)) ?? 0)));
        var alternatives = next.Entities.Where(e => e.HostedDataGrid is not null).Sum(e => e.HostedDataGrid.SchemaVersions.Count(v =>
            v.SchemaVersionRef != e.HostedDataGrid.DefaultSchemaVersionRef && v.SelectionReason.StartsWith("Alternative", StringComparison.Ordinal)));
        var schemaSummary = $" {newGrids} grids added; {Math.Max(0, newVersions)} new schema versions; {recovered} column names added to defaults."
            + (alternatives > 0 ? $" {alternatives} alternative schemas retained; inspect Schema history to select a default." : "");
        // The authoritative source index is committed with the map. Repeating this
        // operation repairs the secondary resume manifest after an interrupted save.
        var sessionPath = catalog.MapSessionPath(selected.Id);
        var session = File.Exists(sessionPath) ? LogicalMapSessionStore.Load(sessionPath)
            : LogicalMapSessionStore.Create(selected.Id, updated.Index.ApplicationName, manifest.StartedUtc);
        foreach (var retained in updated.Index.Sources.DistinctBy(s => s.BundleId))
        {
            if (session.Recordings.Any(r => r.SessionId == retained.BundleId)) continue;
            var retainedPath = catalog.RecordingPath(retained.BundleId);
            if (!File.Exists(retainedPath)) retainedPath = updated.Resolve("recordings/" + retained.BundleHash + ".mlrec");
            session = LogicalMapSessionStore.AddRecording(session, retained.BundleId, retainedPath, manifest.EndedUtc);
        }
        LogicalMapSessionStore.Save(sessionPath, session);
        var result = new CatalogRecordingAppend(id, selected.Id, duplicate ? 0 : updated.Index.Sources.Count - sourceCount,
            duplicate ? 0 : next.Entities.Count - oldEntities.Count, oldEntities.Count,
            next.ReviewItems.Count(r => r.ReviewState == ReviewState.ReviewUnreviewed), duplicate);
        return new() { Root = catalog.Root, MapId = selected.Id, RecordingAppend = result,
            Nodes = graph.Nodes.Count, Edges = graph.Edges.Count,
            Maps = AtlasCatalogCommand.LoadMap(catalog, selected.Id, true, token).Where(m =>
                MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(m.Entry)).MapDraftRef == draft.MapDraftRef).ToArray(),
            Message = duplicate ? "This recording is already included. Existing tasks retain their previous map."
                : $"Added {result.AddedObservations:N0} observations and {result.AddedEntities:N0} entities; retained {result.RetainedEntities:N0} entities. "
                    + $"{result.PendingReviews:N0} review items." + schemaSummary + " Updated draft saved. Mark Ready to use it in new tasks. Existing tasks retain their previous map." };
    }
}
