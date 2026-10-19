using Google.Protobuf;
using UIMaps.Protocol.V3;

namespace UIMaps.Map.Core;

public sealed partial class UiMapService
{
    public ImportMappingWorkspaceResponse ImportWorkspace(ImportMappingWorkspaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.SourceFormat != "ui-atlas.uimaps-workspace/1"
            || request.Snapshots.Count is < 1 or > 1000
            || request.Snapshots.Select(value => value.Session?.MappingSessionRef).Distinct().Count() != request.Snapshots.Count
            || request.Snapshots.Select(value => value.Draft?.MapDraftRef).Distinct().Count() != request.Snapshots.Count)
            throw new UiMapException("MAP_WORKSPACE_INVALID", "A supported workspace and unique session/draft snapshots are required.");
        foreach (var value in request.Snapshots)
        {
            var session = value.Session;
            var draft = value.Draft;
            if (session?.Target is null || draft is null || !session.NotAuthorization
                || string.IsNullOrWhiteSpace(session.MappingSessionRef) || string.IsNullOrWhiteSpace(draft.MapDraftRef)
                || session.MapDraftRef != draft.MapDraftRef || draft.MappingSessionRef != session.MappingSessionRef
                || session.Target.ApplicationRef != draft.ApplicationRef
                || !string.IsNullOrWhiteSpace(session.PublishedMapRevisionRef))
                throw new UiMapException("MAP_WORKSPACE_INVALID", "Snapshot identities or safety markers are inconsistent; transfer unpublished drafts only.");
            ValidatePrivacy(session.PrivacyScope);
            // Blocking review findings are allowed in an unfinished draft. Structural
            // findings still prevent importing a graph with broken containment.
            var validation = UiMapValidator.Validate(draft, "workspace-import-check", Now());
            if (validation.Findings.Any(value => value.Severity == ValidationSeverity.ValidationError
                && value.Code.StartsWith("MAP_GRAPH_", StringComparison.Ordinal)))
                throw new UiMapException("MAP_WORKSPACE_INVALID", "The transferred draft has an invalid graph structure.");
        }
        return _store.ImportWorkspace(request);
    }
}

public sealed partial class UiMapStore
{
    public ImportMappingWorkspaceResponse ImportWorkspace(ImportMappingWorkspaceRequest request)
    {
        var hash = request.SourceArchiveSha256;
        if (hash.Length != 64 || hash.Any(character => !char.IsAsciiHexDigit(character)) || hash != hash.ToLowerInvariant())
            throw new UiMapException("MAP_WORKSPACE_INTEGRITY", "A lowercase SHA-256 archive digest is required.");
        var path = Path.GetFullPath(request.SourceArchivePath);
        for (FileSystemInfo? item = new FileInfo(path); item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UiMapException("MAP_WORKSPACE_INTEGRITY", "Workspace archives cannot be reached through filesystem links.");
        var directory = Path.Combine(_root, "workspace-sources");
        Directory.CreateDirectory(directory);
        var retained = Path.Combine(directory, hash + ".atlasmap");
        var temporary = retained + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length is <= 0 or > 4L * 1024 * 1024 * 1024)
                    throw new UiMapException("MAP_WORKSPACE_INTEGRITY", "Workspace archive exceeds the supported size.");
                using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(destination);
            }
            using (var copied = File.OpenRead(temporary))
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copied)).ToLowerInvariant() != hash)
                    throw new UiMapException("MAP_WORKSPACE_INTEGRITY", "Workspace archive digest does not match the handoff.");
            using (var archive = System.IO.Compression.ZipFile.OpenRead(temporary))
            {
                var entries = archive.Entries.Where(value => value.FullName == "map.db.uimaps/mapping-snapshots.pb").ToArray();
                if (entries.Length != 1 || entries[0].Length is <= 0 or > 64 * 1024 * 1024)
                    throw new UiMapException("MAP_WORKSPACE_INTEGRITY", "The retained archive must contain one bounded snapshot manifest.");
                using var source = entries[0].Open();
                var bytes = new byte[checked((int)entries[0].Length)]; source.ReadExactly(bytes);
                if (source.ReadByte() != -1) throw new UiMapException("MAP_WORKSPACE_INTEGRITY", "Snapshot manifest exceeds its declared size.");
                var manifest = ImportMappingWorkspaceRequest.Parser.ParseFrom(bytes);
                if (!manifest.Snapshots.SequenceEqual(request.Snapshots))
                    throw new UiMapException("MAP_WORKSPACE_INTEGRITY", "The draft snapshots differ from their retained archive.");
            }
            if (!File.Exists(retained)) File.Move(temporary, retained);
            else
            {
                using var existingArchive = File.OpenRead(retained);
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(existingArchive)).ToLowerInvariant() != hash)
                    throw new UiMapException("MAP_WORKSPACE_INTEGRITY", "The destination's retained workspace archive is corrupt.");
            }
            lock (_gate)
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                var present = 0;
                foreach (var snapshot in request.Snapshots)
                {
                    foreach (var (table, key, identity, payload) in new[]
                    {
                        ("sessions", "session_ref", snapshot.Session.MappingSessionRef, snapshot.Session.ToByteArray()),
                        ("drafts", "draft_ref", snapshot.Draft.MapDraftRef, snapshot.Draft.ToByteArray()),
                    })
                    {
                        using var check = connection.CreateCommand(); check.Transaction = transaction;
                        check.CommandText = $"SELECT payload FROM {table} WHERE {key}=$ref";
                        check.Parameters.AddWithValue("$ref", identity);
                        if (check.ExecuteScalar() is byte[] existing)
                        {
                            if (!existing.AsSpan().SequenceEqual(payload))
                                throw new UiMapException("MAP_WORKSPACE_CONFLICT", "A destination draft or session has changed. Import cannot overwrite it.");
                            present++;
                        }
                    }
                }
                if (present != 0 && present != request.Snapshots.Count * 2)
                    throw new UiMapException("MAP_WORKSPACE_CONFLICT", "Only part of this workspace already exists in the destination.");
                if (present == 0)
                    foreach (var snapshot in request.Snapshots)
                    {
                        Upsert(connection, transaction, "sessions", "session_ref", snapshot.Session.MappingSessionRef, snapshot.Session.ToByteArray(), snapshot.Session.UpdatedAt);
                        Upsert(connection, transaction, "drafts", "draft_ref", snapshot.Draft.MapDraftRef, snapshot.Draft.ToByteArray(), snapshot.Draft.UpdatedAt);
                    }
                transaction.Commit();
                return new ImportMappingWorkspaceResponse { MappingSessionRefs = { request.Snapshots.Select(value => value.Session.MappingSessionRef) },
                    MapDraftRefs = { request.Snapshots.Select(value => value.Draft.MapDraftRef) }, RetainedArchiveSha256 = hash, AlreadyPresent = present != 0 };
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
