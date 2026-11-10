using System.Text.Json;
using UiAtlas.Catalog;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Core.Storage;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Cli;

internal static class AtlasSurfaceLearning
{
    private sealed record Job(string Id, string MapId, string Revision, string Generation, string Draft, string? BuiltGeneration = null, string? CaptureAttempt = null);
    private static bool Complete(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var bundle = RecordingBundle.Open(path);
            return bundle.ReadJson<RecordingManifest>("manifest.json").Outcome == RecordingOutcome.Complete
                && RecordingBundleValidator.Validate(bundle).IsValid;
        }
        catch (InvalidDataException) { return false; }
    }
    private static string DirectoryFor(LocalArtifactCatalog catalog, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("A retained capture identity is required.");
        var directory = Path.Combine(catalog.Root, "surface-learning", id);
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Capture paths cannot contain links.");
        return directory;
    }
    private static void Save(string directory, Job job)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "learning.json"); var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(job)); File.Move(temporary, path, true);
    }
    private static CatalogReply Reply(LocalArtifactCatalog catalog, Job job, string stage, string detail, string revision = "")
        => new() { Root = catalog.Root, MapId = job.MapId, Message = detail,
            LearnedSurface = new(job.MapId, catalog.MapPath(job.MapId), revision, detail, job.Id, stage) };
    internal static async Task<CatalogReply> CaptureAndAppendAsync(LocalArtifactCatalog catalog, CatalogRequest request, CancellationToken token)
    {
        var captured = await ExecuteAsync(catalog, request with { Operation = "surface-capture" }, token);
        return await ExecuteAsync(catalog, request with { Operation = "surface-build", SurfaceCaptureId = captured.LearnedSurface!.CaptureId }, token);
    }
    internal static async Task<CatalogReply> ExecuteAsync(LocalArtifactCatalog catalog, CatalogRequest request, CancellationToken token)
    {
        catalog.EnsureSafe();
        var id = request.SurfaceCaptureId ?? (request.Operation == "surface-capture" ? Guid.NewGuid().ToString("N") : "");
        var directory = DirectoryFor(catalog, id); Directory.CreateDirectory(directory);
        using var lease = new FileStream(Path.Combine(directory, "learning.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var jobPath = Path.Combine(directory, "learning.json");
        var job = File.Exists(jobPath) ? JsonSerializer.Deserialize<Job>(File.ReadAllText(jobPath)) : null;
        if (job is not null && (job.Id != id || request.RevisionRef is { } expected && expected != job.Revision))
            throw new InvalidDataException("The retained capture belongs to a different map revision.");
        var bundlePath = Path.Combine(directory, id + ".mlrec");
        if (request.Operation == "surface-capture")
        {
            if (job is null)
            {
                var source = (await AtlasCatalogCommand.ExecuteAsync(new("revision-read", catalog.Root, RevisionRef: request.RevisionRef, RequireReady: true), token)).ExactRevision
                    ?? throw new InvalidDataException("The source map revision is unavailable.");
                var entry = MapLibraryEntry.Parser.ParseFrom(Convert.FromBase64String(source.Entry));
                job = new(id, source.MapId, source.RevisionRef, source.Generation, entry.MapDraftRef); Save(directory, job);
            }
            if (!File.Exists(bundlePath))
            {
                var attemptPath = job.CaptureAttempt is null ? "" : Path.Combine(directory, job.CaptureAttempt, id + ".mlrec");
                if (!Complete(attemptPath))
                {
                    var capture = request.SurfaceCapture ?? throw new ArgumentException("An assigned application is required.");
                    var source = (await AtlasCatalogCommand.ExecuteAsync(new("revision-read", catalog.Root, RevisionRef: job.Revision, RequireReady: true), token)).ExactRevision!;
                    var revision = UiMapRevision.Parser.ParseFrom(Convert.FromBase64String(source.Payload));
                    var target = WindowCatalog.Resolve(capture.RootWindow);
                    if (target.ProcessId != capture.ProcessId || target.ProcessStartedUtc.UtcTicks != capture.ProcessCreatedUtcTicks
                        || !UIMaps.Protocol.WindowsExecutableIdentity.ProcessName(target.ProcessName).Equals(
                            UIMaps.Protocol.WindowsExecutableIdentity.ProcessName(revision.Target.ExecutableName), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The application changed. Keep the intended surface open and capture it again.");
                    job = job with { CaptureAttempt = Guid.NewGuid().ToString("N") }; Save(directory, job);
                    var attemptDirectory = Path.Combine(directory, job.CaptureAttempt);
                    await AtlasSimulationEvidence.CaptureAsync(capture, attemptDirectory, token, "debug-surface-learning", id + ".mlrec");
                    attemptPath = Path.Combine(attemptDirectory, id + ".mlrec");
                }
                File.Copy(attemptPath, bundlePath + ".tmp", true); File.Move(bundlePath + ".tmp", bundlePath);
            }
            using var bundle = RecordingBundle.Open(bundlePath);
            if (bundle.ReadJson<RecordingManifest>("manifest.json").Outcome != RecordingOutcome.Complete)
                throw new InvalidDataException("Capture did not complete. Open the intended surface and start a new capture.");
            return Reply(catalog, job, "Captured", "Capture saved. You may close the application dialog. Finish building the map from this retained evidence.");
        }
        if (job is null) throw new InvalidOperationException("Capture the surface before building its map.");
        var mapPath = catalog.MapPath(job.MapId); var workspace = AtlasMapWorkspace.Open(mapPath);
        if (request.Operation == "surface-build")
        {
            using var bundle = RecordingBundle.Open(bundlePath);
            var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
            if (!Complete(bundlePath)) throw new InvalidDataException("The capture is incomplete.");
            var retainedPath = catalog.RecordingPath(id);
            if (!File.Exists(retainedPath)) File.Copy(bundlePath, retainedPath, overwrite: false);
            // Recover a crash after committing the generation but before saving this checkpoint.
            if (!workspace.Index.Sources.Any(s => s.BundleId == manifest.SessionId))
            {
                var graph = await AtlasMapWorkspace.BuildAsync([retainedPath], mapPath, job.MapId, token, expectedGeneration: job.Generation);
                AtlasMapWorkspace.SaveProjection(graph, mapPath); workspace = AtlasMapWorkspace.Open(mapPath);
            }
            var sessionPath = catalog.MapSessionPath(job.MapId);
            if (File.Exists(sessionPath)) LogicalMapSessionStore.Save(sessionPath,
                LogicalMapSessionStore.AddRecording(LogicalMapSessionStore.Load(sessionPath), id, retainedPath, DateTimeOffset.UtcNow));
            job = job with { BuiltGeneration = Path.GetFileName(workspace.DirectoryPath) }; Save(directory, job);
            return Reply(catalog, job, "Drafted", "Surface added to a saved draft. The current Ready map remains usable. Review and update the task when ready.");
        }
        if (request.Operation != "surface-publish") throw new ArgumentException("Unknown surface operation.");
        if (job.BuiltGeneration is null || job.BuiltGeneration != Path.GetFileName(workspace.DirectoryPath))
            throw new InvalidOperationException("The map changed after this draft was built. Review the current map before updating the task.");
        try
        {
            var revision = workspace.MarkReady(job.Draft, "local-user:debug-surface-learning", new MapReadinessSettingsStore(catalog.Root).Load().RequireReview);
            return Reply(catalog, job, "Ready", "Updated map published. Reanalyze the retained task to finish the update.", revision.MapRevisionRef);
        }
        catch (InvalidDataException error)
        {
            return Reply(catalog, job, "Drafted", "Open Review updated UI Map, complete the required review and mark it Ready. " + error.Message);
        }
    }
}
