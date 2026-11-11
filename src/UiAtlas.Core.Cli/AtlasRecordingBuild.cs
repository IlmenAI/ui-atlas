using UiAtlas.Catalog;
using UiAtlas.Core.Build;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Storage;
using UIMaps.Protocol;
using UIMaps.Map.Core;

namespace UiAtlas.Core.Cli;

internal static class AtlasRecordingBuild
{
    internal static async Task<CatalogReply> ExecuteAsync(LocalArtifactCatalog catalog, CatalogRequest request, CancellationToken token)
    {
        var id = request.RecordingId ?? throw new ArgumentException("Select a recording.");
        var recording = catalog.ListRecordings().Single(r => r.Id == id);
        if (catalog.IsRecordingArchived(id)) throw new InvalidOperationException("Restore this recording before building it.");
        if (request.ExpectedArtifactStamp is null || request.ExpectedArtifactStamp != catalog.RecordingStamp(id))
            throw new IOException("The selected recording changed. Refresh recordings and retry.");
        var selected = request.BuildSettings ?? throw new InvalidDataException("Refresh map-build settings before building.");
        var settings = new MapBuildSettings(selected.MaxNodes, selected.MaxEdges, selected.ExperimentalControlIdentity);
        settings.Validate();
        // A recording build is independent of maps which happen to include it.
        // Preserve those maps and their published revisions for existing tasks.
        var mapId = id[..Math.Min(id.Length, 100)] + "-build-" + Guid.NewGuid().ToString("N")[..12];
        var path = catalog.MapPath(mapId); var source = catalog.RecordingPath(id);
        var graph = await AtlasMapWorkspace.BuildAsync([source], path, mapId, token, buildSettings: settings);
        var workspace = AtlasMapWorkspace.Open(path);
        var policy = settings.ExperimentalControlIdentity ? LogicalControlIdentity.Policy : "";
        if (workspace.Index.Sources.Any(s => s.BundleId != id) || workspace.Drafts.Any(d => d.ControlIdentityPolicy != policy))
            throw new InvalidDataException("The built map does not match the selected recording and identity setting. It was not published.");
        LogicalMapSessionStore.Save(catalog.MapSessionPath(mapId), LogicalMapSessionStore.AddRecording(
            LogicalMapSessionStore.Create(mapId, recording.ProcessName, recording.StartedUtc), id, source, recording.StartedUtc));
        var published = new List<string>(); var problem = "";
        var requireReview = new MapReadinessSettingsStore(catalog.Root).Load().RequireReview;
        foreach (var draft in workspace.Drafts)
        {
            token.ThrowIfCancellationRequested();
            try { published.Add(workspace.MarkReady(draft.MapDraftRef, "local-user:recording-build", requireReview).MapRevisionRef); }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or UiMapException)
            { problem = error.Message; break; }
        }
        var result = new CatalogRecordingBuild(id, mapId, policy, workspace.Drafts.Sum(d => d.LogicalControlGroups.Count), published.ToArray(), problem);
        return new() { Root = catalog.Root, MapId = mapId, Nodes = graph.Nodes.Count, Edges = graph.Edges.Count,
            BuildSettings = selected, RecordingBuild = result,
            Maps = AtlasCatalogCommand.LoadMap(catalog, mapId, true, token),
            Message = (problem.Length == 0 ? "Ready for task analysis." : "Built; publication needs attention. " + problem)
                + " " + (settings.ExperimentalControlIdentity ? "Experimental control identity" : "Legacy control identity")
                + $" · Recording {id} · Map {mapId}." };
    }
}
