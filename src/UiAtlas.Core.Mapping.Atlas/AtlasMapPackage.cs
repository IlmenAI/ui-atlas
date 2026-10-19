using System.IO.Compression;
using System.Security.Cryptography;
using Google.Protobuf;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;
using UIMaps.Perception.Core;
using UIMaps.Protocol.V1;

namespace UiAtlas.Core.Mapping.Atlas;

public static class AtlasMapPackage
{
    private static readonly BundleLimits Limits = new(MaxEntries: 100_000, MaxEntryBytes: 512L * 1024 * 1024, MaxTotalBytes: 4L * 1024 * 1024 * 1024);
    public static void Export(string mapPath, string archivePath, bool includeSensitiveEvidence)
    {
        if (!includeSensitiveEvidence) throw new ArgumentException("A full workspace contains screenshots and raw text. Explicit sensitive-evidence acknowledgement is required; use revision export for graph-only interchange.");
        var workspace = AtlasMapWorkspace.Open(mapPath);
        var root = AtlasMapWorkspace.RootFor(mapPath);
        using var lease = new FileStream(Path.Combine(root, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (AtlasMapWorkspace.Open(mapPath).DirectoryPath != workspace.DirectoryPath) throw new IOException("Map changed; retry export.");
        MapBackendCompanion.RejectLinks(workspace.DirectoryPath);
        var files = Directory.EnumerateFiles(workspace.DirectoryPath, "*", SearchOption.AllDirectories).ToArray();
        if (files.Length + 2 > Limits.MaxEntries || files.Sum(file => new FileInfo(file).Length) > Limits.MaxTotalBytes)
            throw new InvalidDataException("Workspace exceeds portable-package limits.");
        var destination = Path.GetFullPath(archivePath);
        if (File.Exists(destination)) throw new IOException("Export destination already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var snapshots = new UIMaps.Protocol.V3.ImportMappingWorkspaceRequest();
                snapshots.Snapshots.AddRange(workspace.Index.Sessions.Select(session => new UIMaps.Protocol.V3.MappingDraftSnapshot
                { Session = workspace.Service.GetSession(session.SessionRef), Draft = workspace.Service.GetDraft(session.DraftRef) }));
                using (var snapshotStream = archive.CreateEntry("map.db.uimaps/mapping-snapshots.pb", CompressionLevel.NoCompression).Open())
                    snapshots.WriteTo(snapshotStream);
                var editorSession = Path.ChangeExtension(mapPath, ".session.json");
                if (File.Exists(editorSession))
                {
                    // Includes predictions as predictions, never as observed Map entities.
                    var session = LogicalMapSessionStore.Load(editorSession);
                    using var editorStream = archive.CreateEntry("map.db.uimaps/editor-session.json", CompressionLevel.NoCompression).Open();
                    System.Text.Json.JsonSerializer.Serialize(editorStream, session, JsonDefaults.Options);
                }
                var generation = Path.GetFileName(workspace.DirectoryPath);
                var pointer = archive.CreateEntry("map.db.uimaps/active.json", CompressionLevel.NoCompression);
                using (var writer = new StreamWriter(pointer.Open())) writer.Write(System.Text.Json.JsonSerializer.Serialize(generation));
                foreach (var file in files)
                {
                    if (new FileInfo(file).Length > Limits.MaxEntryBytes) throw new InvalidDataException("A workspace file exceeds portable-package limits.");
                    // NoCompression avoids generating packages rejected as decompression bombs
                    // for valid, highly repetitive protobuf/SQLite source evidence.
                    archive.CreateEntryFromFile(file, "map.db.uimaps/generations/" + generation + "/" + Path.GetRelativePath(workspace.DirectoryPath, file).Replace('\\', '/'), CompressionLevel.NoCompression);
                }
            }
            File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string Import(string archivePath, string outputDirectory, bool allowSensitiveEvidence)
    {
        if (!allowSensitiveEvidence) throw new ArgumentException("Import retains screenshots and raw text. Explicit sensitive-evidence acknowledgement is required.");
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Choose a new import directory.");
        var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("Import directory needs a parent.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".atlas-map-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                var issues = BundleSecurity.Inspect(archive, Limits);
                if (issues.Count != 0) throw new InvalidDataException("Unsafe map package: " + string.Join(", ", issues.Take(10)));
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.StartsWith("map.db.uimaps/", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected file in Map package.");
                    var output = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!output.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package path escaped import directory.");
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    entry.ExtractToFile(output, false);
                }
            }
            var map = Path.Combine(staging, "map.db");
            var workspace = AtlasMapWorkspace.Open(map);
            foreach (var file in workspace.Index.BundleFiles)
            {
                var path = workspace.Resolve(file);
                var report = RecordingBundleValidator.Validate(path);
                if (!report.IsValid) throw new InvalidDataException("Imported recording failed integrity validation.");
                using var input = File.OpenRead(path);
                var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
                if (Path.GetFileNameWithoutExtension(file) != hash) throw new InvalidDataException("Imported recording identity hash is invalid.");
            }
            foreach (var source in workspace.Index.Sources)
            {
                var episode = SensorEpisodeBundle.Parser.ParseFrom(ReadBounded(workspace.Resolve(source.EpisodeFile)));
                new PacketValidator(PerceptionPolicy.Default).Validate(episode);
                var publication = GetPublicationResponse.Parser.ParseFrom(ReadBounded(workspace.Resolve(source.PublicationFile)));
                var payload = publication.Snapshot?.WorldStateSnapshot.ToByteArray() ?? publication.Survey?.ToByteArray()
                    ?? throw new InvalidDataException("Publication is empty.");
                var hash = AtlasEpisodeAdapter.Hash(payload);
                var retained = workspace.Drafts.SelectMany(draft => draft.Sources).Single(item => item.SourceObservationSetRef == source.SourceRef);
                if (retained.EpisodeId != episode.EpisodeId || retained.SourcePayloadHash != hash)
                    throw new InvalidDataException("The retained publication does not match Map evidence lineage.");
            }
            AtlasMapWorkspace.SaveProjection(workspace.Project(), map);
            foreach (var grid in workspace.Index.GridReviews ?? [])
            {
                var reviewFile = workspace.Resolve(grid.ReviewFile);
                if (new FileInfo(reviewFile).Length > 16 * 1024 * 1024) throw new InvalidDataException("Grid review exceeds import limits.");
                var review = System.Text.Json.JsonSerializer.Deserialize<UiAtlas.Core.Contracts.SavedDataGridReview>(File.ReadAllText(reviewFile))
                    ?? throw new InvalidDataException("Grid review is empty.");
                if (review.Review.ImagePath != grid.ImageFile || review.GridId != grid.GridId)
                    throw new InvalidDataException("Grid review identity does not match its index.");
                _ = AtlasGridReviewAdapter.ReadImage(workspace.Resolve(grid.ImageFile), review.Review.ImageSha256);
                if (!string.IsNullOrEmpty(review.Review.CaptureManifestPath))
                    AtlasGridReviewAdapter.ValidateCapture(review.Review.CaptureManifestPath, workspace.Resolve);
            }
            var editorSession = Path.Combine(AtlasMapWorkspace.RootFor(map), "editor-session.json");
            if (File.Exists(editorSession))
            {
                if (new FileInfo(editorSession).Length > 32 * 1024 * 1024) throw new InvalidDataException("Editor session exceeds the import limit.");
                var session = LogicalMapSessionStore.Load(editorSession);
                var finalGeneration = Path.Combine(destination, Path.GetRelativePath(staging, workspace.DirectoryPath));
                var recordings = workspace.Index.BundleFiles.Select(file =>
                {
                    using var bundle = RecordingBundle.Open(workspace.Resolve(file));
                    return new LogicalMapSessionRecording(bundle.ReadJson<UiAtlas.Core.Contracts.RecordingManifest>("manifest.json").SessionId,
                        Path.Combine(finalGeneration, file));
                }).ToArray();
                var grids = (workspace.Index.GridReviews ?? []).Select(grid =>
                {
                    var value = System.Text.Json.JsonSerializer.Deserialize<UiAtlas.Core.Contracts.SavedDataGridReview>(File.ReadAllText(workspace.Resolve(grid.ReviewFile)))!;
                    return value with { Review = value.Review with { ImagePath = Path.Combine(finalGeneration, value.Review.ImagePath),
                        CaptureManifestPath = string.IsNullOrEmpty(value.Review.CaptureManifestPath) ? "" : Path.Combine(finalGeneration, value.Review.CaptureManifestPath) } };
                }).ToArray();
                LogicalMapSessionStore.Save(Path.ChangeExtension(map, ".session.json"), session with { Recordings = recordings, DataGrids = grids });
            }
            Directory.Move(staging, destination);
            return Path.Combine(destination, "map.db");
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private static byte[] ReadBounded(string path)
    {
        if (new FileInfo(path).Length > CaptureSizeLimits.MaximumEpisodeBytes) throw new InvalidDataException("Evidence artifact exceeds size limit.");
        return File.ReadAllBytes(path);
    }
}
