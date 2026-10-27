using System.Text;
using System.Text.Json;
using Google.Protobuf;
using UiAtlas.Catalog;
using UiAtlas.Core.Build;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Storage;
using UIMaps.Protocol.V3;
using UIMaps.Protocol.V1;

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
            case "revision-read": case "revision-eligibility": return ReadRevision(catalog, request, token);
            case "identity-review-read": case "identity-confirm": case "parameter-review-read": case "parameter-confirm": return ReadRevision(catalog, request, token);
            case "profile-read":
                if (request.ProfileId != "fast") throw new ArgumentException("This consumer supports the Fast profile.");
                var settings = new UiAtlas.Core.Recording.Windows.RecordingProfileSettingsStore().Load();
                var policy = UiAtlas.Core.Recording.Windows.RecordingProfileSettingsStore.Request(settings with { ActiveProfileId = "fast" });
                var profiles = new UIMaps.Recorder.Windows.CaptureProfileCatalogService().GetCatalog();
                policy.RequestedCatalogVersion = profiles.CatalogVersion;
                policy.RequestedProfileVersion = profiles.Profiles.Single(value => value.ProfileId == "fast").ProfileVersion;
                return new() { Root = catalog.Root, CaptureRequest = Encode(policy) };
            case "map-detail":
            case "map-preview":
                var details = LoadMap(catalog, request.MapId ?? throw new ArgumentException("Select a map."), false, token);
                var selected = details.SingleOrDefault(map => map.Entry == request.Entry && map.Generation == request.Generation)
                    ?? throw new IOException("The selected map changed. Refresh Maps and retry.");
                if (request.Operation == "map-preview")
                {
                    var preview = AtlasMapWorkspace.Exists(selected.Path) ? AtlasCatalogPresentation.Preview(AtlasMapWorkspace.Open(selected.Path)) : null;
                    return new() { Root = catalog.Root, PreviewPng = preview is null ? null : Convert.ToBase64String(preview) };
                }
                return new() { Root = catalog.Root, Maps = [selected] };
            case "recordings":
                var links = RecordingMapLinks(catalog, token);
                return new() { Root = catalog.Root, Recordings = catalog.ListRecordings().Select(item => new CatalogRecording(item.Id,
                    item.StartedUtc, item.Outcome, item.ProcessName, item.FrameCount,
                    File.Exists(catalog.MapPath(item.Id)) ? item.Id : links.GetValueOrDefault(item.Id), catalog.IsRecordingArchived(item.Id), catalog.RecordingStamp(item.Id))).ToArray() };
            case "recording-delete":
                catalog.DeleteRecordingPermanently(request.RecordingId ?? throw new ArgumentException("Select a recording."), request.ExpectedArtifactStamp);
                return new() { Root = catalog.Root, Message = "Recording deleted." };
            case "recording-archive": case "recording-restore":
                catalog.SetRecordingArchived(request.RecordingId ?? throw new ArgumentException("Select a recording."), request.Operation == "recording-archive");
                return new() { Root = catalog.Root, Message = request.Operation == "recording-archive" ? "Recording archived. Source evidence remains available to its maps." : "Recording restored." };
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
            case "map-delete":
                var selectedId = request.MapId ?? throw new ArgumentException("Select a UI Map.");
                var reviewed = MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(request.Entry ?? ""));
                var subject = LibrarySubject(reviewed);
                catalog.DeleteMapLibraryEntry(selectedId, subject, () =>
                {
                    if (!LoadMap(catalog, selectedId, false, token).Any(m => m.Generation == request.Generation && m.Entry == request.Entry))
                        throw new IOException("The selected map changed. Refresh Maps and retry.");
                });
                return new() { Root = catalog.Root, Message = "UI Map removed from the library. Retained revisions and recordings were preserved." };
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
    private static CatalogReply ReadRevision(LocalArtifactCatalog catalog, CatalogRequest request, CancellationToken token)
    {
        const string prefix = "uimaps:map-revision:sha256:";
        var reference = request.RevisionRef ?? "";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal) || reference.Length != prefix.Length + 64
            || reference[prefix.Length..].Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("An exact Atlas revision identity is required.");
        foreach (var path in catalog.ListMapPaths())
        {
            token.ThrowIfCancellationRequested();
            if (request.MapId is { } id && Path.GetFileNameWithoutExtension(path) != id) continue;
            if (!AtlasMapWorkspace.Exists(path)) continue;
            var workspace = AtlasMapWorkspace.Open(path);
            UiMapRevision revision;
            try { revision = workspace.Service.GetRevision(reference); }
            catch (UIMaps.Map.Core.UiMapException error) when (error.Code == "MAP_REVISION_NOT_FOUND") { continue; }
            if (!revision.Immutable || !revision.NotAuthorization
                || revision.CanonicalPayloadHash != UIMaps.Protocol.UiMapRevisionIntegrity.Hash(revision))
                throw new InvalidDataException("Atlas revision failed its canonical integrity check.");
            // An old revision remains readable; only the currently published Ready
            // entry is eligible for new analysis. Completed work retains its own bytes.
            MapLibraryEntry? entry = null;
            var cursor = "";
            do
            {
                token.ThrowIfCancellationRequested();
                var page = workspace.Service.ListLibrary(new ListMapLibraryRequest { IncludeArchived = true, PageSize = 500, ContinuationToken = cursor });
                entry = page.Entries.FirstOrDefault(value => value.LatestMapRevisionRef == reference) ?? entry;
                cursor = page.ContinuationToken;
            } while (cursor.Length > 0);
            var ready = entry is { IsReady: true, Archived: false };
            if ((request.RequireReady || request.Operation == "parameter-confirm") && !ready)
                throw new InvalidOperationException("This exact revision is no longer a Ready published Atlas entry. Select an eligible revision; completed drafts retain their original map.");
            if (request.Operation == "revision-eligibility")
                return new CatalogReply { Root = catalog.Root, RevisionEligibility = new(reference, revision.CanonicalPayloadHash,
                    Path.GetFileNameWithoutExtension(path), Path.GetFileName(workspace.DirectoryPath), ready,
                    entry?.Archived ?? false, DateTimeOffset.UtcNow.ToString("O")) };
            if (request.Operation is "identity-review-read" or "identity-confirm")
                return AtlasWorkflowIdentityReview.Execute(workspace, revision, entry, path, request, token);
            if (request.Operation is "parameter-review-read" or "parameter-confirm")
                return AtlasWorkflowParameterReview.Execute(workspace, revision, ReadRawWorldSources(workspace, revision, token),
                    path, request, token);
            var rawWorld = request.IncludeRawWorld ? ReadRawWorldSources(workspace, revision, token) : [];
            var parameters = request.IncludeRawWorld ? AtlasParameterMatching.Read(workspace, revision, rawWorld, token) : null;
            return new CatalogReply { Root = catalog.Root, RawWorldSources = rawWorld, Parameterization = parameters,
                RawWorldContractVersion = request.IncludeRawWorld ? 1 : 0, ExactRevision = new(reference, revision.CanonicalPayloadHash,
                Encode(revision)!, Path.GetFileNameWithoutExtension(path), Path.GetFileName(workspace.DirectoryPath),
                Encode(entry) ?? "", ready, entry?.Archived ?? false, DateTimeOffset.UtcNow.ToString("O")) };
        }
        throw new FileNotFoundException("The exact published Atlas revision is unavailable in this catalog.");
    }
    private static CatalogRawWorldSource[] ReadRawWorldSources(AtlasMapWorkspace workspace, UiMapRevision revision, CancellationToken token)
    {
        if (revision.Sources.Count == 0)
            throw new InvalidDataException("This revision has no retained Raw World sources for workflow binding.");
        var result = new List<CatalogRawWorldSource>();
        long total = revision.CalculateSize();
        var sources = workspace.Index.Sources.ToDictionary(source => source.SourceRef, StringComparer.Ordinal);
        foreach (var source in revision.Sources)
        {
            token.ThrowIfCancellationRequested();
            if (!sources.TryGetValue(source.SourceObservationSetRef, out var retained))
                throw new InvalidDataException("The exact revision's Raw World source is unavailable.");
            var path = workspace.Resolve(retained.PublicationFile);
            if (new FileInfo(path).Length > CaptureSizeLimits.MaximumEpisodeBytes)
                throw new InvalidDataException("The retained Raw World source exceeds its byte limit.");
            var publication = GetPublicationResponse.Parser.ParseFrom(File.ReadAllBytes(path));
            IMessage payload = source.AcquisitionSemantic == AcquisitionSemantic.AcquisitionSnapshot
                ? publication.Snapshot?.WorldStateSnapshot ?? throw new InvalidDataException("Expected a retained Raw World snapshot.")
                : source.AcquisitionSemantic == AcquisitionSemantic.AcquisitionSurvey
                    ? publication.Survey ?? throw new InvalidDataException("Expected a retained Raw World survey.")
                    : throw new InvalidDataException("Unsupported Raw World source semantic.");
            var bytes = payload.ToByteArray();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            if (hash != source.SourcePayloadHash || hash != source.WorldStateHash)
                throw new InvalidDataException("The retained Raw World payload does not match the exact revision's source hashes.");
            total += bytes.LongLength;
            if (total > CaptureSizeLimits.MaximumEpisodeBytes)
                throw new InvalidDataException("The revision and its Raw World sources exceed the handoff limit; no sources were omitted.");
            result.Add(new(source.SourceObservationSetRef, publication.Snapshot is not null ? "snapshot" : "survey", hash, Convert.ToBase64String(bytes)));
        }
        return result.ToArray();
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
                return catalog.IsMapLibraryEntryDeleted(id, LibrarySubject(legacy)) ? [] : [new(id, path, legacy.ApplicationRef, "", Encode(legacy)!, null, null, null, summaryOnly)];
            }
            var workspace = AtlasMapWorkspace.Open(path);
            string continuation = "";
            var presentation = AtlasCatalogPresentation.Read(workspace);
            do
            {
                token.ThrowIfCancellationRequested();
                var page = workspace.Service.ListLibrary(new ListMapLibraryRequest { IncludeArchived = true, PageSize = 500, ContinuationToken = continuation });
                foreach (var entry in page.Entries)
                {
                    if (catalog.IsMapLibraryEntryDeleted(id, LibrarySubject(entry))) continue;
                    var draft = string.IsNullOrEmpty(entry.MapDraftRef) ? null : workspace.Service.GetDraft(entry.MapDraftRef);
                    var revision = string.IsNullOrEmpty(entry.LatestMapRevisionRef) ? null : workspace.Service.GetRevision(entry.LatestMapRevisionRef);
                    records.Add(new(id, path, "atlas:app:" + Uri.EscapeDataString(workspace.Index.ApplicationName.ToLowerInvariant()),
                        Path.GetFileName(workspace.DirectoryPath), Encode(entry)!,
                        Encode(string.IsNullOrEmpty(entry.MappingSessionRef) ? null : workspace.Service.GetSession(entry.MappingSessionRef)),
                        Encode(summaryOnly && draft is not null ? AtlasMapListProjection.Draft(draft) : draft),
                        Encode(summaryOnly && revision is not null ? AtlasMapListProjection.Revision(revision) : revision), summaryOnly, presentation));
                }
                continuation = page.ContinuationToken;
            } while (!string.IsNullOrEmpty(continuation));
        }
        return records.ToArray();
    }
    private static string LibrarySubject(MapLibraryEntry entry) => !string.IsNullOrEmpty(entry.MappingSessionRef) ? entry.MappingSessionRef
        : !string.IsNullOrEmpty(entry.MapDraftRef) ? entry.MapDraftRef : entry.LatestMapRevisionRef;
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
        if (request.Operation == "ready")
            return new() { Root = catalog.Root, Revision = Encode(workspace.MarkReady(entry.MapDraftRef, "local-user:atlas",
                new MapReadinessSettingsStore(catalog.Root).Load().RequireReview)) };
        UiMapRevision? revision = null;
        if (request.Operation == "publish")
            revision = workspace.Publish("local-user:atlas")[workspace.Index.Sessions.ToList().FindIndex(value => value.DraftRef == entry.MapDraftRef)];
        if (request.Operation != "publish") workspace.UpdateLibrary(service =>
        {
            switch (request.Operation)
            {
                case "archive": service.ArchiveLibraryEntries(new ArchiveMapLibraryEntriesRequest { RequestId = Guid.NewGuid().ToString("N"), ArchivedBy = "local-user:atlas", Entries = { entry } }); break;
                case "restore": service.RestoreLibraryEntries(new RestoreMapLibraryEntriesRequest { RequestId = Guid.NewGuid().ToString("N"), RestoredBy = "local-user:atlas", Entries = { entry } }); break;
                case "draft": service.SetLibraryReadiness(new SetMapLibraryReadinessRequest { RequestId = Guid.NewGuid().ToString("N"), ChangedBy = "local-user:atlas", Entry = entry, Ready = false }); break;
            }
        });
        return new() { Root = catalog.Root, Revision = Encode(revision) };
    }
    private static string? Encode(IMessage? value) => value is null ? null : Convert.ToBase64String(value.ToByteArray());
}
