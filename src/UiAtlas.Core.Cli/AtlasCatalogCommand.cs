using System.Text;
using System.Text.Json;
using Google.Protobuf;
using UiAtlas.Catalog;
using UiAtlas.Core.Build;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Storage;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

/// <summary>The Atlas-owned catalog API used by both desktop hosts.</summary>
internal static class AtlasCatalogCommand
{
    internal static async Task<int> RunAsync()
    {
        Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var line = await Console.In.ReadLineAsync() ?? throw new InvalidDataException("A catalog request is required.");
            if (line.Length > 1024 * 1024) throw new InvalidDataException("Catalog request exceeds its limit.");
            var request = JsonSerializer.Deserialize<CatalogRequest>(line) ?? throw new InvalidDataException("Catalog request is empty.");
            _ = Task.Run(() => MonitorCancellationAsync(cancellation));
            var reply = await ExecuteAsync(request, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(reply));
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new CatalogReply { Success = false, Message = error is OperationCanceledException
                ? "Build cancelled. Previous maps and recordings were preserved." : error.GetBaseException().Message }));
            return 2;
        }
    }
    private static async Task MonitorCancellationAsync(CancellationTokenSource cancellation)
    {
        try { if (await Console.In.ReadLineAsync() == "cancel") cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    internal static async Task<CatalogReply> ExecuteAsync(CatalogRequest request, CancellationToken token = default)
    {
        if (request.Version != 1) throw new InvalidDataException("Unsupported Atlas catalog version. Install matching tools and launcher.");
        var catalog = new LocalArtifactCatalog(AtlasCatalogLocation.Resolve(request.Root));
        token.ThrowIfCancellationRequested();
        switch (request.Operation)
        {
            case "maps": return new() { Root = catalog.Root, Maps = LoadMaps(catalog, token) };
            case "maps-summary": return AtlasCatalogIndex.Read(catalog, request.KnownCatalogVersion, token);
            case "map-detail":
                var details = LoadMap(catalog, request.MapId ?? throw new ArgumentException("Select a map."), false, token);
                var selected = details.SingleOrDefault(map => map.Entry == request.Entry && map.Generation == request.Generation)
                    ?? throw new IOException("The selected map changed. Refresh Maps and retry.");
                return new() { Root = catalog.Root, Maps = [selected] };
            case "recordings":
                var links = RecordingMapLinks(catalog, token);
                return new() { Root = catalog.Root, Recordings = catalog.ListRecordings().Select(item => new CatalogRecording(item.Id,
                    item.StartedUtc, item.Outcome, item.ProcessName, item.FrameCount,
                    File.Exists(catalog.MapPath(item.Id)) ? item.Id : links.GetValueOrDefault(item.Id))).ToArray() };
            case "build":
                var recording = catalog.RecordingPath(request.RecordingId ?? throw new ArgumentException("Select a recording."));
                var mapId = request.MapId ?? request.RecordingId!;
                var mapPath = catalog.MapPath(mapId);
                var sessionPath = catalog.MapSessionPath(mapId);
                var recordings = File.Exists(sessionPath) ? LogicalMapSessionStore.Load(sessionPath).Recordings.Select(value => value.RecordingPath).ToArray() : [recording];
                if (recordings.Any(path => !File.Exists(path))) throw new FileNotFoundException("A source recording is missing. Restore it before rebuilding this map.");
                var graph = await AtlasMapWorkspace.BuildAsync(recordings, mapPath, mapId, token, buildSettings: new MapBuildSettingsStore().Load());
                return new() { Root = catalog.Root, MapId = mapId, Nodes = graph.Nodes.Count, Edges = graph.Edges.Count,
                    Message = $"Draft ready: {graph.Nodes.Count:N0} nodes and {graph.Edges.Count:N0} edges. Open map to review it." };
            case "legacy-inventory": return new() { Root = catalog.Root, Imports = AtlasCatalogImport.Inventory(request.SourcePath) };
            case "import": return AtlasCatalogImport.Import(catalog, request.SourcePath ?? throw new ArgumentException("Select an artifact to import."), token);
            case "archive": case "restore": case "ready": case "draft": case "publish":
                return ChangeMap(catalog, request, token);
            default: throw new ArgumentException("Unknown Atlas catalog operation.");
        }
    }
    private static Dictionary<string, string> RecordingMapLinks(LocalArtifactCatalog catalog, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var map in catalog.ListMaps())
        {
            token.ThrowIfCancellationRequested();
            var path = catalog.MapPath(map.Id);
            if (!AtlasMapWorkspace.Exists(path)) continue;
            foreach (var source in AtlasMapWorkspace.Open(path).Index.Sources) result.TryAdd(source.BundleId, map.Id);
        }
        return result;
    }
    internal static CatalogMap[] LoadMaps(LocalArtifactCatalog catalog, CancellationToken token)
    {
        var records = new List<CatalogMap>();
        foreach (var path in catalog.ListMapPaths())
            records.AddRange(LoadMap(catalog, Path.GetFileNameWithoutExtension(path), false, token));
        return records.ToArray();
    }
    internal static CatalogMap[] LoadMap(LocalArtifactCatalog catalog, string id, bool summaryOnly, CancellationToken token)
    {
        var records = new List<CatalogMap>();
        {
            token.ThrowIfCancellationRequested();
            var path = catalog.MapPath(id);
            if (!File.Exists(path)) throw new FileNotFoundException("The map is no longer in the catalog.", path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Catalog maps cannot be links.");
            if (!AtlasMapWorkspace.Exists(path))
            {
                var built = File.GetCreationTimeUtc(path);
                try { built = SqliteGraphStore.ReadCatalogMetadata(path).BuiltUtc.UtcDateTime; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or JsonException) { }
                var legacy = new MapLibraryEntry { ApplicationRef = "atlas:legacy:" + id, ApplicationDisplayName = id,
                    MapDraftRef = id, UpdatedAt = new DateTimeOffset(built).ToString("O"), Warnings = { "Legacy projection: open in Atlas to inspect retained evidence." } };
                return [new(id, path, legacy.ApplicationRef, "", Encode(legacy)!, null, null, null, summaryOnly)];
            }
            var workspace = AtlasMapWorkspace.Open(path);
            string continuation = "";
            do
            {
                token.ThrowIfCancellationRequested();
                var page = workspace.Service.ListLibrary(new ListMapLibraryRequest { IncludeArchived = true, PageSize = 500, ContinuationToken = continuation });
                foreach (var entry in page.Entries)
                {
                    var draft = string.IsNullOrEmpty(entry.MapDraftRef) ? null : workspace.Service.GetDraft(entry.MapDraftRef);
                    var revision = string.IsNullOrEmpty(entry.LatestMapRevisionRef) ? null : workspace.Service.GetRevision(entry.LatestMapRevisionRef);
                    records.Add(new(id, path, "atlas:app:" + Uri.EscapeDataString(workspace.Index.ApplicationName.ToLowerInvariant()),
                        Path.GetFileName(workspace.DirectoryPath), Encode(entry)!,
                        Encode(string.IsNullOrEmpty(entry.MappingSessionRef) ? null : workspace.Service.GetSession(entry.MappingSessionRef)),
                        Encode(summaryOnly && draft is not null ? AtlasMapListProjection.Draft(draft) : draft),
                        Encode(summaryOnly && revision is not null ? AtlasMapListProjection.Revision(revision) : revision), summaryOnly));
                }
                continuation = page.ContinuationToken;
            } while (!string.IsNullOrEmpty(continuation));
        }
        return records.ToArray();
    }
    private static CatalogReply ChangeMap(LocalArtifactCatalog catalog, CatalogRequest request, CancellationToken token)
    {
        var path = catalog.MapPath(request.MapId ?? throw new ArgumentException("Refresh Maps before changing this entry."));
        if (!AtlasMapWorkspace.Exists(path)) throw new InvalidOperationException("Build an Atlas workspace from the recording before changing its status.");
        var workspace = AtlasMapWorkspace.Open(path);
        if (request.Generation != Path.GetFileName(workspace.DirectoryPath)) throw new IOException("This map changed in another app. Refresh Maps and retry.");
        var entry = MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(request.Entry ?? ""));
        if (workspace.Index.Sessions.All(session => session.DraftRef != entry.MapDraftRef) || entry.MapDraftRef != request.DraftRef)
            throw new InvalidDataException("The selected entry does not belong to this Atlas workspace.");
        token.ThrowIfCancellationRequested();
        UiMapRevision? revision = null;
        if (request.Operation == "publish" || request.Operation == "ready" && string.IsNullOrEmpty(entry.LatestMapRevisionRef))
            revision = workspace.Publish("local-user:atlas")[workspace.Index.Sessions.ToList().FindIndex(value => value.DraftRef == entry.MapDraftRef)];
        if (request.Operation != "publish") workspace.UpdateLibrary(service =>
        {
            switch (request.Operation)
            {
                case "archive": service.ArchiveLibraryEntries(new ArchiveMapLibraryEntriesRequest { RequestId = Guid.NewGuid().ToString("N"), ArchivedBy = "local-user:atlas", Entries = { entry } }); break;
                case "restore": service.RestoreLibraryEntries(new RestoreMapLibraryEntriesRequest { RequestId = Guid.NewGuid().ToString("N"), RestoredBy = "local-user:atlas", Entries = { entry } }); break;
                case "ready": case "draft": service.SetLibraryReadiness(new SetMapLibraryReadinessRequest { RequestId = Guid.NewGuid().ToString("N"), ChangedBy = "local-user:atlas", Entry = entry, Ready = request.Operation == "ready" }); break;
            }
        });
        return new() { Root = catalog.Root, Revision = Encode(revision) };
    }
    private static string? Encode(IMessage? value) => value is null ? null : Convert.ToBase64String(value.ToByteArray());
}
