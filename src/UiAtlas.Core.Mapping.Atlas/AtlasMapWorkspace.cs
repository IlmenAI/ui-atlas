using System.Text.Json;
using Google.Protobuf;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;
using UIMaps.Map.Core;
using UIMaps.Perception.Core;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Mapping.Atlas;

public sealed record AtlasMapSession(string ProcessIdentity, string SessionRef, string DraftRef);
public sealed record AtlasMapSource(string BundleId, long Frame, string BundleHash, string EpisodeFile,
    string PublicationFile, string SourceRef, string ScreenshotEntry);
public sealed record AtlasMapIndex(string Format, string LogicalMapId, string ApplicationName,
    IReadOnlyList<AtlasMapSession> Sessions, IReadOnlyList<AtlasMapSource> Sources,
    IReadOnlyList<string> BundleFiles, IReadOnlyList<AtlasGridReview>? GridReviews = null);

/// <summary>A local, versioned Map authority. The adjacent Atlas graph is a disposable
/// compatibility projection. Source bundles, episodes, and publications are retained.
/// A process-wide file lease and generation pointer isolate readers from interrupted writes.</summary>
public sealed class AtlasMapWorkspace : IMappingReviewStore
{
    public const string Format = "ui-atlas.uimaps-workspace/1";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _graphPath;
    public string DirectoryPath { get; }
    public AtlasMapIndex Index { get; }
    public UiMapService Service { get; }

    private AtlasMapWorkspace(string graphPath, string directory)
    {
        _graphPath = Path.GetFullPath(graphPath);
        DirectoryPath = directory;
        MapBackendCompanion.RejectLinks(directory);
        if (new FileInfo(Path.Combine(directory, "atlas-map.json")).Length > 32 * 1024 * 1024)
            throw new InvalidDataException("Map index exceeds the size limit.");
        Index = JsonSerializer.Deserialize<AtlasMapIndex>(File.ReadAllText(Path.Combine(directory, "atlas-map.json")), Json)
            ?? throw new InvalidDataException("Map index is empty.");
        if (Index.Format != Format) throw new InvalidDataException("Unsupported UI Atlas Map workspace version.");
        if (Index.Sources.Count > 100_000 || Index.Sessions.Count > 10_000 || Index.BundleFiles.Count > 10_000)
            throw new InvalidDataException("Map index exceeds supported counts.");
        if (!File.Exists(Path.Combine(directory, "map", "map.db"))) throw new InvalidDataException("Map authority database is missing.");
        Service = new UiMapService(Path.Combine(directory, "map"));
    }

    public static string RootFor(string graphPath) => Path.GetFullPath(graphPath) + ".uimaps";
    public static bool Exists(string graphPath) => File.Exists(Path.Combine(RootFor(graphPath), "active.json"));
    public static AtlasMapWorkspace Open(string graphPath)
    {
        MapBackendCompanion.RejectLinks(RootFor(graphPath));
        var generation = JsonSerializer.Deserialize<string>(File.ReadAllText(Path.Combine(RootFor(graphPath), "active.json")))
            ?? throw new InvalidDataException("Map generation is missing.");
        if (!Guid.TryParseExact(generation, "N", out _)) throw new InvalidDataException("Invalid Map generation.");
        return new(graphPath, Path.Combine(RootFor(graphPath), "generations", generation));
    }
    public IReadOnlyList<UiMapDraft> Drafts => Index.Sessions.Select(session => Service.GetDraft(session.DraftRef)).ToArray();
    public IReadOnlyList<MapValidationReport> Validate()
    {
        using var lease = AcquireLease();
        return Drafts.Select(draft => Service.ValidateDraft(draft.MapDraftRef, draft.Etag)).ToArray();
    }
    public ImportMapRevisionResponse ImportRevision(ByteString bytes)
    {
        using var lease = AcquireLease(); return Service.Import(bytes, true);
    }
    public UiKnowledgeGraph Project() => AtlasMapProjection.Create(this);
    public string Resolve(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(DirectoryPath, relative));
        if (!path.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Map artifact path escapes its workspace.");
        return path;
    }

    public static async Task<UiKnowledgeGraph> BuildAsync(IEnumerable<string> bundlePaths, string graphPath,
        string? logicalMapId = null, CancellationToken cancellationToken = default, IReadOnlyList<SavedDataGridReview>? gridReviews = null,
        MapBuildSettings? buildSettings = null, string? expectedGeneration = null,
        Func<AtlasMapWorkspace, bool>? validateAppend = null, bool retainReadyForReplay = false)
    {
        buildSettings ??= new MapBuildSettingsStore().Load();
        buildSettings.Validate();
        graphPath = Path.GetFullPath(graphPath);
        var root = RootFor(graphPath);
        if (Directory.Exists(root)) MapBackendCompanion.RejectLinks(root);
        Directory.CreateDirectory(root);
        using var lease = new FileStream(Path.Combine(root, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var existing = Exists(graphPath) ? Open(graphPath) : null;
        // Append admission and duplicate detection share the writer lease with commit.
        if (validateAppend is not null)
        {
            if (existing is null) throw new InvalidDataException("The destination map is unavailable.");
            if (validateAppend(existing)) return existing.Project();
        }
        if (expectedGeneration is not null && (existing is null || Path.GetFileName(existing.DirectoryPath) != expectedGeneration))
            throw new InvalidOperationException("The UI Map changed during capture. The capture was retained; refresh the map before merging it.");
        var generation = Guid.NewGuid().ToString("N");
        var output = Path.Combine(root, "generations", generation);
        Directory.CreateDirectory(output);
        try
        {
            if (existing is not null) CopyTree(existing.DirectoryPath, output);
            else if (File.Exists(graphPath))
            {
                var legacy = Path.Combine(root, "legacy");
                Directory.CreateDirectory(legacy);
                foreach (var file in new[] { graphPath, MapCurationStore.PathForMap(graphPath) }.Where(File.Exists))
                {
                    using var input = File.OpenRead(file);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant();
                    var backup = Path.Combine(legacy, hash + Path.GetExtension(file));
                    if (!File.Exists(backup)) File.Copy(file, backup);
                }
            }
            var sessions = existing?.Index.Sessions.ToList() ?? [];
            var sources = existing?.Index.Sources.ToList() ?? [];
            var bundles = existing?.Index.BundleFiles.ToList() ?? [];
            var retainedGrids = existing?.Index.GridReviews?.ToList() ?? [];
            foreach (var grid in gridReviews ?? [])
            {
                if (retainedGrids.Any(value => value.GridId == grid.GridId && File.Exists(Path.Combine(output, value.ReviewFile))
                    && JsonSerializer.Deserialize<SavedDataGridReview>(File.ReadAllText(Path.Combine(output, value.ReviewFile)))?.Review.Schema.Revision == grid.Review.Schema.Revision)) continue;
                var retained = AtlasGridReviewAdapter.Retain(grid, output);
                retainedGrids.RemoveAll(value => value.GridId == grid.GridId);
                retainedGrids.Add(retained);
            }
            var service = new UiMapService(Path.Combine(output, "map"));
            if (retainReadyForReplay && existing is not null)
            {
                var retainedPath = Path.Combine(output, "retained-replay-revisions.json");
                var retained = File.Exists(retainedPath)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(retainedPath))! : new();
                var cursor = "";
                do
                {
                    var page = existing.Service.ListLibrary(new ListMapLibraryRequest { IncludeArchived = true, PageSize = 500, ContinuationToken = cursor });
                    foreach (var entry in page.Entries.Where(e => e.IsReady && !e.Archived && e.LatestMapRevisionRef.Length > 0
                        && existing.Index.Sessions.Any(s => s.DraftRef == e.MapDraftRef)))
                        retained[entry.LatestMapRevisionRef] = entry.MapDraftRef;
                    cursor = page.ContinuationToken;
                } while (cursor.Length > 0);
                File.WriteAllText(retainedPath, JsonSerializer.Serialize(retained));
            }
            string? application = existing?.Index.ApplicationName;
            // Upgrade retained schema evidence before new observations are attached.
            // This also repairs a formerly rejected schema on an idempotent retry.
            var priorReviews = retainedGrids.Select(entry => JsonSerializer.Deserialize<SavedDataGridReview>(
                File.ReadAllText(Path.Combine(output, entry.ReviewFile)))!).ToArray();
            foreach (var prior in sources)
            {
                var relevant = priorReviews.Where(r => r.Context.SourceBundleId == prior.BundleId && r.Context.SourceFrame == prior.Frame).ToArray();
                if (relevant.Length == 0) continue;
                var owner = sessions.Single(s => service.GetDraft(s.DraftRef).Sources.Any(v => v.SourceObservationSetRef == prior.SourceRef));
                var publication = GetPublicationResponse.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(output, prior.PublicationFile)));
                var package = AtlasGridReviewAdapter.Bind(relevant, retainedGrids, prior.BundleId, prior.Frame,
                    publication, service.GetSession(owner.SessionRef).Target);
                if (package is null) continue;
                var objects = publication.Snapshot?.WorldStateSnapshot.WorldObjects.ToArray()
                    ?? publication.Survey.WorldObjects.Select(o => o.WorldObject).ToArray();
                service.ReconcileReviewedGridSchemas(owner.DraftRef, prior.SourceRef, package, objects);
                UpdateGridRetention(retainedGrids, package, service.GetDraft(owner.DraftRef));
            }
            foreach (var bundlePath in bundlePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Temporal ownership evidence belongs to one continuous recording.
                var perception = new PerceptionEngine();
                cancellationToken.ThrowIfCancellationRequested();
                using var bundle = RecordingBundle.Open(bundlePath);
                var report = RecordingBundleValidator.Validate(bundle);
                if (!report.IsValid) throw new InvalidDataException(string.Join("; ", report.Issues.Where(issue => issue.Severity == "error")
                    .Select(issue => $"{issue.Code} at {BundleSecurity.SafeDiagnostic(issue.Path)}: {BundleSecurity.SafeDiagnostic(issue.Message, 500)}")));
                var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
                application ??= manifest.Target.ProcessName;
                if (!application.Equals(manifest.Target.ProcessName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A Map workspace cannot mix recordings from different applications.");
                logicalMapId ??= existing?.Index.LogicalMapId ?? manifest.SessionId;
                using var hashStream = File.OpenRead(bundlePath);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(hashStream)).ToLowerInvariant();
                if (sources.Any(source => source.BundleId == manifest.SessionId && source.BundleHash != hash))
                    throw new InvalidDataException("A recording identity was reused with different content.");
                var bundleFile = "recordings/" + hash + ".mlrec";
                Directory.CreateDirectory(Path.Combine(output, "recordings"));
                if (!File.Exists(Path.Combine(output, bundleFile))) File.Copy(bundlePath, Path.Combine(output, bundleFile));
                if (!bundles.Contains(bundleFile, StringComparer.Ordinal)) bundles.Add(bundleFile);
                var interactions = new AtlasInteractionAdapter(bundle, hash);
                var identity = AtlasEpisodeAdapter.ProcessIdentity(manifest.Target);
                var session = sessions.SingleOrDefault(value => value.ProcessIdentity == identity);
                // Resuming a logical map after an application restart must append
                // to its application draft. Process identity remains exact and
                // observation-scoped below, never replaced with a name-only match.
                if (session is null && sessions.Count == 1) session = sessions[0];
                if (session is null)
                {
                    var created = service.CreateSession(new CreateMappingSessionRequest
                    {
                        RequestId = Guid.NewGuid().ToString("N"), Target = new TargetApplicationIdentity
                        {
                            ApplicationRef = "uimaps:app:" + Guid.NewGuid().ToString("N"), DisplayName = application,
                            ExecutableName = manifest.Target.ProcessName, ProcessIdentityEvidence = { identity,
                                $"pid:{manifest.Target.ProcessId}:started:{manifest.Target.ProcessStartedUtc.UtcDateTime:O}" },
                            WindowIdentityEvidence = { manifest.Target.SelectedHwnd.ToString(), manifest.Target.RootOwnerHwnd.ToString() },
                        },
                        PrivacyScope = new PrivacyScopeAcknowledgement { ScopeVersion = "ui-atlas.attended-mapping/1", ScopeHash = hash,
                            AcknowledgedBy = "local-user:recording-consent", AcknowledgedAt = manifest.StartedUtc.ToString("O"),
                            ScreenshotsPermitted = manifest.Privacy.ScreenshotsRetained, PrintableTextPermitted = true,
                            ProtectedValuesExcluded = true, TargetInputProhibited = true },
                    });
                    session = new(identity, created.Session.MappingSessionRef, created.Draft.MapDraftRef);
                    sessions.Add(session);
                }
                foreach (var entry in bundle.Entries.Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal) && entry.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frame = bundle.ReadJson<FrameObservation>(entry);
                    if (sources.Any(source => source.BundleHash == hash && source.Frame == frame.Sequence)) continue;
                    var episode = frame.NativeEpisodeEntry is { Length: > 0 } nativeEntry
                        ? SensorEpisodeBundle.Parser.ParseFrom(bundle.ReadBytes(nativeEntry))
                        : AtlasEpisodeAdapter.Convert(bundle, manifest, frame);
                    if (frame.NativeEpisodeEntry is { Length: > 0 }
                        && (frame.NativeEpisodeEntry != $"raw/native/frame-{frame.Sequence:D6}.episode.pb"
                            || !episode.SensorPackets.Single(packet => packet.Screenshot is not null).Screenshot.ImageBlob.Span.SequenceEqual(bundle.ReadBytes(frame.FrameEntry))))
                        throw new InvalidDataException("The native episode does not match this frame's retained screenshot.");
                    var ingested = await perception.IngestAsync(episode, cancellationToken).ConfigureAwait(false);
                    var publication = perception.GetPublication(ingested.PublicationId);
                    var request = new AttachObservationRequest { RequestId = Guid.NewGuid().ToString("N"),
                        MappingSessionRef = session.SessionRef, ExpectedEtag = service.GetSession(session.SessionRef).Etag,
                        PublicationRef = ingested.PublicationId };
                    if (publication.Snapshot is not null) request.Snapshot = publication.Snapshot.WorldStateSnapshot;
                    else request.Survey = publication.Survey;
                    request.ControlDetectionPolicy = new UIMaps.Protocol.V3.ControlDetectionPolicy
                    { VisualControlDetectionEnabled = true, OcrOnVisualControlsEnabled = true };
                    var recordedTarget = service.GetSession(session.SessionRef).Target.Clone();
                    recordedTarget.ProcessIdentityEvidence.Clear();
                    recordedTarget.ProcessIdentityEvidence.Add(identity);
                    recordedTarget.ProcessIdentityEvidence.Add($"pid:{manifest.Target.ProcessId}:started:{manifest.Target.ProcessStartedUtc.UtcDateTime:O}");
                    recordedTarget.WindowIdentityEvidence.Clear();
                    recordedTarget.WindowIdentityEvidence.Add(manifest.Target.SelectedHwnd.ToString());
                    recordedTarget.WindowIdentityEvidence.Add(manifest.Target.RootOwnerHwnd.ToString());
                    request.ReviewedGridMap = AtlasGridReviewAdapter.Bind(gridReviews ?? [], retainedGrids, manifest.SessionId,
                        frame.Sequence, publication, recordedTarget);
                    request.RecordingContext = interactions.ForFrame(frame.Sequence, service.GetDraft(session.DraftRef), sources);
                    var attached = service.AttachObservationFromSelectedRecording(request, recordedTarget);
                    if (request.ReviewedGridMap is { } gridMap) UpdateGridRetention(retainedGrids, gridMap, attached.Draft);
                    var stem = "evidence/" + hash + "-" + frame.Sequence;
                    Directory.CreateDirectory(Path.Combine(output, "evidence"));
                    await File.WriteAllBytesAsync(Path.Combine(output, stem + ".episode.pb"), episode.ToByteArray(), cancellationToken).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(Path.Combine(output, stem + ".perception.pb"), publication.ToByteArray(), cancellationToken).ConfigureAwait(false);
                    sources.Add(new(manifest.SessionId, frame.Sequence, hash, stem + ".episode.pb", stem + ".perception.pb",
                        attached.Draft.Sources.Last().SourceObservationSetRef, frame.FrameEntry));
                }
            }
            if (sources.Count == 0 || application is null || logicalMapId is null) throw new InvalidDataException("At least one recorded observation is required.");
            var legacyParameters = MapParameterizationStore.LegacyPath(graphPath);
            if (existing is null && File.Exists(legacyParameters))
            {
                var originalId = File.Exists(graphPath) ? SqliteGraphStore.ReadSummary(graphPath).Metadata.EffectiveLogicalMapId : logicalMapId;
                _ = MapParameterizationStore.LoadFile(legacyParameters, originalId);
                var retainedParameters = Path.Combine(output, "parameter-review.json");
                File.Copy(legacyParameters, retainedParameters);
                MapParameterizationStore.RebindFile(retainedParameters, logicalMapId);
            }
            var identitySources = sources.ToDictionary(source => source.SourceRef, source =>
            {
                var publication = GetPublicationResponse.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(output, source.PublicationFile)));
                var objects = publication.Snapshot?.WorldStateSnapshot.WorldObjects.ToArray()
                    ?? publication.Survey.WorldObjects.Select(o => o.WorldObject).ToArray();
                return (IReadOnlyDictionary<string, WorldObject>)objects.ToDictionary(o => o.WorldObjectId);
            });
            foreach (var session in sessions)
                service.SetControlIdentity(session.DraftRef, buildSettings.ExperimentalControlIdentity, identitySources);
            var index = new AtlasMapIndex(Format, logicalMapId, application, sessions, sources, bundles, retainedGrids);
            File.WriteAllText(Path.Combine(output, "atlas-map.json"), JsonSerializer.Serialize(index, Json));
            File.WriteAllText(Path.Combine(output, "build-settings.json"), JsonSerializer.Serialize(buildSettings, Json));
            var workspace = new AtlasMapWorkspace(graphPath, output);
            if (validateAppend is not null && existing is not null)
            {
                var retained = workspace.Drafts.SelectMany(d => d.Entities).Select(e => e.MapEntityRef).ToHashSet(StringComparer.Ordinal);
                if (existing.Drafts.SelectMany(d => d.Entities).Any(e => !retained.Contains(e.MapEntityRef)))
                    throw new InvalidDataException("Append must retain every existing map entity.");
            }
            var graph = AtlasMapProjection.Create(workspace, buildSettings);
            var validation = GraphValidator.Validate(graph);
            if (!validation.IsValid) throw new InvalidDataException(
                string.Join("; ", validation.Issues.Take(3).Select(issue => issue.Code + ":" + issue.Message))
                + (validation.Issues.Count > 3 ? $"; {validation.Issues.Count - 3} additional validation issues." : ""));
            cancellationToken.ThrowIfCancellationRequested();
            SaveProjection(graph, graphPath);
            var pointer = Path.Combine(root, "active.json");
            var temporary = pointer + "." + generation + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(generation)); File.Move(temporary, pointer, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return graph;
        }
        catch
        {
            // Only this invocation's new generation; previous authority remains intact.
            if (Directory.Exists(output)) Directory.Delete(output, true);
            throw;
        }
    }

    public ApplyReviewCommandResponse Review(ReviewCommand command)
    {
        using var lease = AcquireLease();
        if (Index.Sessions.All(session => session.DraftRef != command.MapDraftRef)) throw new ArgumentException("The review command names a different workspace's draft.");
        MappingReviewGuard.ValidateParent(command, Service.GetDraft(command.MapDraftRef));
        var result = Service.ApplyReviewCommand(command);
        if (command.CommandKind == ReviewCommandKind.ReviewSelectGridSchema)
        {
            var session = Service.GetSession(result.Draft.MappingSessionRef);
            if (session.PublishedMapRevisionRef.Length > 0)
            {
                var path = Path.Combine(DirectoryPath, "retained-replay-revisions.json");
                var retained = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))! : new();
                retained[session.PublishedMapRevisionRef] = command.MapDraftRef;
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(retained)); File.Move(temporary, path, true);
            }
        }
        SaveProjection(Project(), _graphPath);
        return result;
    }

    private static void UpdateGridRetention(List<AtlasGridReview> retained, UIMaps.Protocol.V2_1.DataGridMapPackage package, UiMapDraft draft)
    {
        foreach (var grid in package.DataGrids)
        {
            var index = retained.FindIndex(value => "atlas:grid:" + value.GridId == grid.DataGridRef);
            var entity = draft.Entities.FirstOrDefault(e => e.HostedDataGrid?.SchemaVersions.Any(v => v.SourceGridRefs.Contains(grid.DataGridRef)) == true);
            retained[index] = retained[index] with { SchemaHistoryVersion = 1,
                Status = entity is null ? "pending-host-binding" : "mapped-schema",
                Detail = entity is null ? "Retained schema needs a unique mapped host. Open Data grids to inspect its evidence."
                    : "Schema retained in grid history. The selected default is used after publication." };
        }
    }
    public IReadOnlyList<UiMapRevision> Publish(string reviewer)
    {
        using var lease = AcquireLease();
        var drafts = Drafts;
        var validations = drafts.Select(draft => Service.ValidateDraft(draft.MapDraftRef, draft.Etag)).ToArray();
        if (validations.Any(report => !report.PublicationEligible))
            throw new InvalidDataException("Every draft must pass validation and explicit review before publication.");
        return drafts.Select((draft, index) => Service.Publish(new PublishMapDraftRequest { RequestId = Guid.NewGuid().ToString("N"),
            MapDraftRef = draft.MapDraftRef, ExpectedEtag = draft.Etag, ValidationRunRef = validations[index].ValidationRunRef,
            PublishedBy = reviewer }).Revision).ToArray();
    }

    public UiMapRevision MarkReady(string draftRef, string changedBy, bool requireReview = true)
    {
        using var lease = AcquireLease();
        var selected = Index.Sessions.SingleOrDefault(value => value.DraftRef == draftRef)
            ?? throw new ArgumentException("The selected draft does not belong to this workspace.");
        var session = Service.GetSession(selected.SessionRef);
        UiMapRevision revision;
        if (Service.IsPublishedDraftCurrent(draftRef))
        {
            revision = Service.GetRevision(session.PublishedMapRevisionRef);
        }
        else
        {
            var draft = Service.GetDraft(draftRef);
            // Check real defects before changing any review state. Only the two
            // explicit-review gates may be satisfied by automatic acceptance.
            var preflight = UiMapValidator.Validate(draft, "readiness-preflight", DateTimeOffset.UtcNow.ToString("O"));
            var blockers = preflight.Findings.Where(finding => finding.Severity == ValidationSeverity.ValidationError
                && (requireReview || finding.Code is not ("MAP_VALIDATION_BLOCKED" or "MAP_INTERACTION_REVIEW_REQUIRED"))).ToArray();
            if (blockers.Length > 0)
                throw new InvalidDataException("This map cannot be marked Ready. "
                    + string.Join(" ", blockers.Select(value => value.Code + ": " + value.Message))
                    + (requireReview && blockers.Any(value => value.Code is "MAP_VALIDATION_BLOCKED" or "MAP_INTERACTION_REVIEW_REQUIRED")
                        ? " Review the map, or turn off 'Require review before marking UI maps Ready' in launcher Settings." : ""));
            if (!requireReview && draft.ReviewItems.Any(value => value.Blocking && value.ReviewState == ReviewState.ReviewUnreviewed))
            {
                draft = Service.ApplyReviewCommand(new ReviewCommand
                {
                    RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = draftRef, ExpectedEtag = draft.Etag,
                    CommandKind = ReviewCommandKind.ReviewAcceptAllBlocking,
                    ReviewedBy = "policy:manual-review-disabled",
                    Reason = $"Pending items accepted automatically because manual review was disabled when {changedBy} marked this map Ready.",
                }).Draft;
                SaveProjection(Project(), _graphPath);
            }
            var validation = Service.ValidateDraft(draftRef, draft.Etag);
            if (!validation.PublicationEligible)
                throw new InvalidDataException("This map cannot be marked Ready. " + string.Join(" ",
                    validation.Findings.Where(value => value.Severity == ValidationSeverity.ValidationError)
                        .Select(value => value.Code + ": " + value.Message)));
            revision = Service.Publish(new PublishMapDraftRequest
            {
                RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = draftRef, ExpectedEtag = draft.Etag,
                ValidationRunRef = validation.ValidationRunRef, PublishedBy = changedBy,
            }).Revision;
        }
        Service.SetLibraryReadiness(new SetMapLibraryReadinessRequest
        {
            RequestId = Guid.NewGuid().ToString("N"), ChangedBy = changedBy, Ready = true,
            Entry = new MapLibraryEntry { MappingSessionRef = selected.SessionRef, MapDraftRef = draftRef },
        });
        return revision;
    }

    public void UpdateLibrary(Action<UiMapService> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        using var lease = AcquireLease();
        change(Service);
    }
    private FileStream AcquireLease()
    {
        var lease = new FileStream(Path.Combine(RootFor(_graphPath), "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Open(_graphPath).DirectoryPath != DirectoryPath) { lease.Dispose(); throw new InvalidOperationException("The map changed in another process. Reopen it before editing."); }
        return lease;
    }
    public static void SaveProjection(UiKnowledgeGraph graph, string path)
    {
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) GraphJsonStore.Save(graph, path);
        else SqliteGraphStore.SaveProjection(graph, path);
    }
    private static void CopyTree(string source, string destination)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Map workspaces cannot contain links.");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) { Directory.CreateDirectory(target); CopyTree(entry, target); }
            else File.Copy(entry, target);
        }
    }
}
