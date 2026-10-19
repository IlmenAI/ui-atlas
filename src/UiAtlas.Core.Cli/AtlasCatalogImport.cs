using System.Security.Cryptography;
using UiAtlas.Catalog;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Cli;

internal static class AtlasCatalogImport
{
    internal static CatalogImportCandidate[] Inventory(string? directory = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = directory is null ? new[] { Path.Combine(local, "UIMaps", "atlas-recordings"),
            Path.Combine(Environment.GetEnvironmentVariable("UIMAPS_MAP_DATA_ROOT") ?? Path.Combine(local, "UIMaps", "map"), "workspace-sources") } : [Path.GetFullPath(directory)];
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        return roots.Where(Directory.Exists).Where(root => (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", options)).Where(path => Path.GetExtension(path) is ".mlrec" or ".atlasmap")
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2000).Select(path => new CatalogImportCandidate(path,
                Path.GetExtension(path) == ".mlrec" ? "Recording" : "Atlas workspace", new FileInfo(path).Length)).ToArray();
    }
    internal static CatalogReply Import(LocalArtifactCatalog catalog, string source, CancellationToken token)
    {
        source = Path.GetFullPath(source);
        catalog.EnsureSafe();
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Import a regular file, not a link.");
        using var importLock = new FileStream(Path.Combine(catalog.Root, "catalog-import.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        token.ThrowIfCancellationRequested();
        if (Path.GetExtension(source).Equals(".mlrec", StringComparison.OrdinalIgnoreCase))
        {
            var recording = ImportRecording(catalog, source, token);
            return new() { Root = catalog.Root, Message = $"Recording {recording.SessionId} is available in the shared Atlas library. The source was kept." };
        }
        if (!Path.GetExtension(source).Equals(".atlasmap", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Import a .mlrec recording or .atlasmap workspace.");
        var id = "import-" + Hash(source)[..24];
        var map = catalog.MapPath(id);
        if (File.Exists(map)) return new() { Root = catalog.Root, MapId = id, Message = "This workspace is already in the Atlas library." };
        if (Directory.Exists(AtlasMapWorkspace.RootFor(map))) throw new IOException("An incomplete import already occupies this destination. Inspect it before retrying.");
        var staging = Path.Combine(catalog.Root, ".import-" + Guid.NewGuid().ToString("N"));
        var copied = false;
        try
        {
            var imported = AtlasMapPackage.Import(source, staging, allowSensitiveEvidence: true);
            var workspace = AtlasMapWorkspace.Open(imported);
            var recordings = workspace.Index.BundleFiles.Select(file => ImportRecording(catalog, workspace.Resolve(file), token)).ToArray();
            token.ThrowIfCancellationRequested();
            MapBackendCompanion.Copy(imported, map, id); copied = true;
            var final = AtlasMapWorkspace.Open(map);
            var sessionPath = Path.ChangeExtension(imported, ".session.json");
            var session = File.Exists(sessionPath) ? LogicalMapSessionStore.Load(sessionPath) : new LogicalMapSessionManifest(
                "ui-atlas.logical-map-session/1", id, final.Index.ApplicationName, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, recordings);
            string Rebase(string path) => string.IsNullOrEmpty(path) ? path : path.Replace(workspace.DirectoryPath, final.DirectoryPath, StringComparison.OrdinalIgnoreCase);
            LogicalMapSessionStore.Save(catalog.MapSessionPath(id), session with { LogicalMapId = id, Recordings = recordings,
                DataGrids = session.DataGrids?.Select(grid => grid with { Review = grid.Review with {
                    ImagePath = Rebase(grid.Review.ImagePath), CaptureManifestPath = Rebase(grid.Review.CaptureManifestPath) } }).ToArray() });
            AtlasMapWorkspace.SaveProjection(final.Project(), map);
            return new() { Root = catalog.Root, MapId = id, Message = "Atlas workspace and retained recordings imported. Review history was kept; source files were not changed." };
        }
        catch
        {
            if (copied && !File.Exists(map)) { MapBackendCompanion.Delete(map, catalog.MapsDirectory); File.Delete(catalog.MapSessionPath(id)); }
            throw;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private static LogicalMapSessionRecording ImportRecording(LocalArtifactCatalog catalog, string source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var validation = RecordingBundleValidator.Validate(source);
        if (!validation.IsValid) throw new InvalidDataException("The recording failed integrity validation.");
        using var bundle = RecordingBundle.Open(source);
        var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
        var destination = catalog.RecordingPath(manifest.SessionId);
        if (File.Exists(destination))
        {
            if (Hash(source) != Hash(destination)) throw new IOException($"Recording {manifest.SessionId} already exists with different contents. No existing recording was replaced.");
        }
        else
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(source, temporary); token.ThrowIfCancellationRequested(); File.Move(temporary, destination); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return new(manifest.SessionId, destination);
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
}
