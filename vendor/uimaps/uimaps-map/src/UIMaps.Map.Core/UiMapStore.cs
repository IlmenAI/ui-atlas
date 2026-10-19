using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using UIMaps.Protocol.V3;

namespace UIMaps.Map.Core;

public sealed partial class UiMapStore
{
    private readonly string _root;
    private readonly string _connectionString;
    private readonly object _gate = new();

    public UiMapStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A Map data directory is required.", nameof(directory));
        _root = Path.GetFullPath(directory);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "revisions", "sha256"));
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "map.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };
        _connectionString = builder.ToString();
        Initialize();
    }

    public void SaveSessionAndDraft(MappingSessionDescriptor session, UiMapDraft draft)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(draft);
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Upsert(connection, transaction, "sessions", "session_ref", session.MappingSessionRef, session.ToByteArray(), session.UpdatedAt);
            Upsert(connection, transaction, "drafts", "draft_ref", draft.MapDraftRef, draft.ToByteArray(), draft.UpdatedAt);
            transaction.Commit();
        }
    }

    public MappingSessionDescriptor LoadSession(string sessionRef) =>
        MappingSessionDescriptor.Parser.ParseFrom(Load("sessions", "session_ref", sessionRef, "MAP_SESSION_NOT_FOUND"));

    public UiMapDraft LoadDraft(string draftRef) =>
        UiMapDraft.Parser.ParseFrom(Load("drafts", "draft_ref", draftRef, "MAP_DRAFT_NOT_FOUND"));

    public IReadOnlyList<MappingSessionDescriptor> ListSessions()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM sessions ORDER BY updated_at DESC, session_ref";
            using var reader = command.ExecuteReader();
            var values = new List<MappingSessionDescriptor>();
            while (reader.Read()) values.Add(MappingSessionDescriptor.Parser.ParseFrom((byte[])reader[0]));
            return values;
        }
    }

    public void SaveValidation(MapValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Upsert(connection, transaction, "validations", "validation_ref", report.ValidationRunRef, report.ToByteArray(), report.CompletedAt);
            transaction.Commit();
        }
    }

    public MapValidationReport LoadValidation(string validationRef) =>
        MapValidationReport.Parser.ParseFrom(Load("validations", "validation_ref", validationRef, "MAP_VALIDATION_NOT_FOUND"));

    public void AppendReviewCommand(string commandRef, string draftRef, ReviewCommand command, ReviewCommandResult result, string createdAt)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var statement = connection.CreateCommand();
            statement.Transaction = transaction;
            statement.CommandText = "INSERT INTO review_commands(command_ref,draft_ref,command_payload,result_payload,created_at) VALUES($ref,$draft,$command,$result,$created)";
            statement.Parameters.AddWithValue("$ref", commandRef);
            statement.Parameters.AddWithValue("$draft", draftRef);
            statement.Parameters.AddWithValue("$command", command.ToByteArray());
            statement.Parameters.AddWithValue("$result", result.ToByteArray());
            statement.Parameters.AddWithValue("$created", createdAt);
            statement.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public (Stack<UiMapDraft> Undo, Stack<UiMapDraft> Redo) LoadReviewStacks(string draftRef)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT undo_payload,redo_payload FROM review_stacks WHERE draft_ref=$draft";
            command.Parameters.AddWithValue("$draft", draftRef);
            using var reader = command.ExecuteReader();
            return !reader.Read()
                ? (new Stack<UiMapDraft>(), new Stack<UiMapDraft>())
                : (DeserializeStack((byte[])reader[0]), DeserializeStack((byte[])reader[1]));
        }
    }

    public void CommitReview(
        MappingSessionDescriptor session,
        UiMapDraft draft,
        string commandRef,
        ReviewCommand command,
        ReviewCommandResult result,
        Stack<UiMapDraft> undo,
        Stack<UiMapDraft> redo,
        string createdAt)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Upsert(connection, transaction, "sessions", "session_ref", session.MappingSessionRef, session.ToByteArray(), session.UpdatedAt);
            Upsert(connection, transaction, "drafts", "draft_ref", draft.MapDraftRef, draft.ToByteArray(), draft.UpdatedAt);
            using (var statement = connection.CreateCommand())
            {
                statement.Transaction = transaction;
                statement.CommandText = "INSERT INTO review_commands(command_ref,draft_ref,command_payload,result_payload,created_at) VALUES($ref,$draft,$command,$result,$created)";
                statement.Parameters.AddWithValue("$ref", commandRef);
                statement.Parameters.AddWithValue("$draft", draft.MapDraftRef);
                statement.Parameters.AddWithValue("$command", command.ToByteArray());
                statement.Parameters.AddWithValue("$result", result.ToByteArray());
                statement.Parameters.AddWithValue("$created", createdAt);
                statement.ExecuteNonQuery();
            }
            using (var history = connection.CreateCommand())
            {
                history.Transaction = transaction;
                history.CommandText = "INSERT INTO review_stacks(draft_ref,undo_payload,redo_payload,updated_at) VALUES($draft,$undo,$redo,$updated) "
                    + "ON CONFLICT(draft_ref) DO UPDATE SET undo_payload=excluded.undo_payload,redo_payload=excluded.redo_payload,updated_at=excluded.updated_at";
                history.Parameters.AddWithValue("$draft", draft.MapDraftRef);
                history.Parameters.AddWithValue("$undo", SerializeStack(undo));
                history.Parameters.AddWithValue("$redo", SerializeStack(redo));
                history.Parameters.AddWithValue("$updated", createdAt);
                history.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public void SaveRevision(UiMapRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var canonical = UiMapCanonicalizer.SerializeCanonical(revision);
        var hash = Hash(canonical);
        if (!string.Equals(hash, revision.CanonicalPayloadHash, StringComparison.Ordinal)
            || !string.Equals(revision.MapRevisionRef, "uimaps:map-revision:sha256:" + hash, StringComparison.Ordinal))
            throw new UiMapException("MAP_REVISION_INTEGRITY_FAILED", "Map revision identity does not match its canonical payload.");

        var directory = Path.Combine(_root, "revisions", "sha256", hash[..2]);
        Directory.CreateDirectory(directory);
        var finalPath = EnsureChildPath(Path.Combine(directory, hash + ".uimap.pb"));
        if (!File.Exists(finalPath))
        {
            var temporary = EnsureChildPath(finalPath + ".partial-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(temporary, revision.ToByteArray());
            File.Move(temporary, finalPath, overwrite: false);
        }

        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Upsert(connection, transaction, "revisions", "revision_ref", revision.MapRevisionRef, revision.ToByteArray(), revision.PublishedAt);
            transaction.Commit();
        }
    }

    public UiMapRevision LoadRevision(string revisionRef)
    {
        var revision = UiMapRevision.Parser.ParseFrom(Load("revisions", "revision_ref", revisionRef, "MAP_REVISION_NOT_FOUND"));
        var canonical = UiMapCanonicalizer.SerializeCanonical(revision);
        var hash = Hash(canonical);
        if (!string.Equals(hash, revision.CanonicalPayloadHash, StringComparison.Ordinal)
            || !string.Equals(revisionRef, "uimaps:map-revision:sha256:" + hash, StringComparison.Ordinal))
            throw new UiMapException("MAP_REVISION_INTEGRITY_FAILED", "Stored Map revision failed its integrity check.");
        return revision;
    }

    public IReadOnlyList<UiMapRevision> ListRevisions()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM revisions ORDER BY updated_at DESC, revision_ref";
            using var reader = command.ExecuteReader();
            var values = new List<UiMapRevision>();
            while (reader.Read()) values.Add(UiMapRevision.Parser.ParseFrom((byte[])reader[0]));
            return values;
        }
    }

    public IReadOnlySet<string> ListArchivedLibraryEntryRefs()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT subject_ref FROM archived_library_entries";
            using var reader = command.ExecuteReader();
            var values = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) values.Add(reader.GetString(0));
            return values;
        }
    }

    public IReadOnlyDictionary<(string ApplicationRef, string ApplicationVersion), string> ListReadyMapSelections()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT application_ref,application_version,revision_ref FROM ready_map_selections";
            using var reader = command.ExecuteReader();
            var values = new Dictionary<(string ApplicationRef, string ApplicationVersion), string>();
            while (reader.Read()) values[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
            return values;
        }
    }

    public SetMapLibraryReadinessResponse SetLibraryReadiness(
        UiMapRevision revision,
        bool ready,
        string changedBy,
        string changedAt)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var applicationRef = revision.ApplicationRef;
        var applicationVersion = revision.Target?.ApplicationVersion?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(applicationRef) || string.IsNullOrWhiteSpace(revision.MapRevisionRef))
            throw new UiMapException("MAP_LIBRARY_READINESS_INVALID", "A stored Map revision with an application identity is required.");

        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            string displaced = string.Empty;
            using (var current = connection.CreateCommand())
            {
                current.Transaction = transaction;
                current.CommandText = "SELECT revision_ref FROM ready_map_selections WHERE application_ref=$app AND application_version=$version";
                current.Parameters.AddWithValue("$app", applicationRef);
                current.Parameters.AddWithValue("$version", applicationVersion);
                displaced = current.ExecuteScalar() as string ?? string.Empty;
            }

            if (ready)
            {
                using var select = connection.CreateCommand();
                select.Transaction = transaction;
                select.CommandText = "INSERT INTO ready_map_selections(application_ref,application_version,revision_ref,changed_at,changed_by) "
                    + "VALUES($app,$version,$revision,$at,$by) ON CONFLICT(application_ref,application_version) DO UPDATE SET "
                    + "revision_ref=excluded.revision_ref,changed_at=excluded.changed_at,changed_by=excluded.changed_by";
                select.Parameters.AddWithValue("$app", applicationRef);
                select.Parameters.AddWithValue("$version", applicationVersion);
                select.Parameters.AddWithValue("$revision", revision.MapRevisionRef);
                select.Parameters.AddWithValue("$at", changedAt);
                select.Parameters.AddWithValue("$by", changedBy);
                select.ExecuteNonQuery();
            }
            else
            {
                using var clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM ready_map_selections WHERE application_ref=$app AND application_version=$version AND revision_ref=$revision";
                clear.Parameters.AddWithValue("$app", applicationRef);
                clear.Parameters.AddWithValue("$version", applicationVersion);
                clear.Parameters.AddWithValue("$revision", revision.MapRevisionRef);
                clear.ExecuteNonQuery();
            }
            transaction.Commit();

            return new SetMapLibraryReadinessResponse
            {
                ApplicationRef = applicationRef,
                ApplicationVersion = applicationVersion,
                SelectedMapRevisionRef = ready ? revision.MapRevisionRef : string.Empty,
                DisplacedMapRevisionRef = ready && !string.Equals(displaced, revision.MapRevisionRef, StringComparison.Ordinal) ? displaced : string.Empty,
                Ready = ready,
                ChangedAt = changedAt,
            };
        }
    }

    public ArchiveMapLibraryEntriesResponse ArchiveLibraryEntries(
        IReadOnlyCollection<MapLibraryEntry> entries,
        string archivedBy,
        string archivedAt)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            throw new UiMapException("MAP_LIBRARY_SELECTION_REQUIRED", "At least one Map Library entry is required.");

        lock (_gate)
        {
            using var connection = Open();
            var sessionRefs = new HashSet<string>(StringComparer.Ordinal);
            var revisionRefs = new HashSet<string>(StringComparer.Ordinal);
            var readinessRevisionRefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.MappingSessionRef))
                {
                    var session = LoadMessage<MappingSessionDescriptor>(connection, "sessions", "session_ref", entry.MappingSessionRef, "MAP_SESSION_NOT_FOUND");
                    sessionRefs.Add(entry.MappingSessionRef);
                    if (!string.IsNullOrWhiteSpace(session.PublishedMapRevisionRef))
                        readinessRevisionRefs.Add(session.PublishedMapRevisionRef);
                }
                else if (!string.IsNullOrWhiteSpace(entry.LatestMapRevisionRef))
                {
                    _ = LoadMessage<UiMapRevision>(connection, "revisions", "revision_ref", entry.LatestMapRevisionRef, "MAP_REVISION_NOT_FOUND");
                    revisionRefs.Add(entry.LatestMapRevisionRef);
                    readinessRevisionRefs.Add(entry.LatestMapRevisionRef);
                }
                else
                {
                    throw new UiMapException("MAP_LIBRARY_ENTRY_INVALID", "A selected Map Library entry has no durable identifier.");
                }
            }

            using var transaction = connection.BeginTransaction();
            DeleteWhereIn(connection, transaction, "ready_map_selections", "revision_ref", readinessRevisionRefs);
            foreach (var subjectRef in sessionRefs.Concat(revisionRefs))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO archived_library_entries(subject_ref,archived_at,archived_by) VALUES($subject,$at,$by) "
                    + "ON CONFLICT(subject_ref) DO UPDATE SET archived_at=excluded.archived_at,archived_by=excluded.archived_by";
                command.Parameters.AddWithValue("$subject", subjectRef);
                command.Parameters.AddWithValue("$at", archivedAt);
                command.Parameters.AddWithValue("$by", archivedBy);
                command.ExecuteNonQuery();
            }
            transaction.Commit();

            var response = new ArchiveMapLibraryEntriesResponse
            {
                ArchivedEntryCount = sessionRefs.Count + revisionRefs.Count,
                ArchivedAt = archivedAt,
            };
            response.ArchivedMappingSessionRefs.AddRange(sessionRefs.Order(StringComparer.Ordinal));
            response.ArchivedMapRevisionRefs.AddRange(revisionRefs.Order(StringComparer.Ordinal));
            return response;
        }
    }

    public RestoreMapLibraryEntriesResponse RestoreLibraryEntries(
        IReadOnlyCollection<MapLibraryEntry> entries,
        string restoredAt)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            throw new UiMapException("MAP_LIBRARY_SELECTION_REQUIRED", "At least one Map Library entry is required.");

        lock (_gate)
        {
            using var connection = Open();
            var sessionRefs = new HashSet<string>(StringComparer.Ordinal);
            var revisionRefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.MappingSessionRef))
                {
                    _ = LoadMessage<MappingSessionDescriptor>(connection, "sessions", "session_ref", entry.MappingSessionRef, "MAP_SESSION_NOT_FOUND");
                    sessionRefs.Add(entry.MappingSessionRef);
                }
                else if (!string.IsNullOrWhiteSpace(entry.LatestMapRevisionRef))
                {
                    _ = LoadMessage<UiMapRevision>(connection, "revisions", "revision_ref", entry.LatestMapRevisionRef, "MAP_REVISION_NOT_FOUND");
                    revisionRefs.Add(entry.LatestMapRevisionRef);
                }
                else
                {
                    throw new UiMapException("MAP_LIBRARY_ENTRY_INVALID", "A selected Map Library entry has no durable identifier.");
                }
            }

            var restoredSessions = new List<string>();
            var restoredRevisions = new List<string>();
            using var transaction = connection.BeginTransaction();
            foreach (var (subjectRef, destination) in sessionRefs.Select(value => (value, restoredSessions))
                         .Concat(revisionRefs.Select(value => (value, restoredRevisions))))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM archived_library_entries WHERE subject_ref=$subject";
                command.Parameters.AddWithValue("$subject", subjectRef);
                if (command.ExecuteNonQuery() > 0) destination.Add(subjectRef);
            }
            transaction.Commit();

            var response = new RestoreMapLibraryEntriesResponse
            {
                RestoredEntryCount = restoredSessions.Count + restoredRevisions.Count,
                RestoredAt = restoredAt,
            };
            response.RestoredMappingSessionRefs.AddRange(restoredSessions.Order(StringComparer.Ordinal));
            response.RestoredMapRevisionRefs.AddRange(restoredRevisions.Order(StringComparer.Ordinal));
            return response;
        }
    }

    public DeleteMapLibraryEntriesResponse DeleteLibraryEntries(IReadOnlyCollection<MapLibraryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            throw new UiMapException("MAP_LIBRARY_SELECTION_REQUIRED", "At least one Map Library entry is required.");

        lock (_gate)
        {
            using var connection = Open();
            var sessions = entries
                .Where(value => !string.IsNullOrWhiteSpace(value.MappingSessionRef))
                .Select(value => LoadMessage<MappingSessionDescriptor>(connection, "sessions", "session_ref", value.MappingSessionRef, "MAP_SESSION_NOT_FOUND"))
                .GroupBy(value => value.MappingSessionRef, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            var sessionRefs = sessions.Select(value => value.MappingSessionRef).ToHashSet(StringComparer.Ordinal);
            var draftRefs = sessions.Select(value => value.MapDraftRef).Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.Ordinal);
            var revisionRefs = sessions.Select(value => value.PublishedMapRevisionRef).Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in entries.Where(value => string.IsNullOrWhiteSpace(value.MappingSessionRef)))
            {
                if (string.IsNullOrWhiteSpace(entry.LatestMapRevisionRef))
                    throw new UiMapException("MAP_LIBRARY_ENTRY_INVALID", "A selected Map Library entry has no durable identifier.");
                _ = LoadMessage<UiMapRevision>(connection, "revisions", "revision_ref", entry.LatestMapRevisionRef, "MAP_REVISION_NOT_FOUND");
                revisionRefs.Add(entry.LatestMapRevisionRef);
            }

            var validationRefs = SelectMessages<MapValidationReport>(connection, "validations")
                .Where(value => draftRefs.Contains(value.MapDraftRef))
                .Select(value => value.ValidationRunRef)
                .ToArray();
            var compatibilityRefs = SelectMessages<CompatibilityReport>(connection, "compatibility_reports")
                .Where(value => revisionRefs.Contains(value.OldMapRevisionRef) || revisionRefs.Contains(value.NewMapRevisionRef))
                .Select(value => value.CompatibilityReportRef)
                .ToArray();

            using var transaction = connection.BeginTransaction();
            DeleteWhereIn(connection, transaction, "review_commands", "draft_ref", draftRefs);
            DeleteWhereIn(connection, transaction, "review_stacks", "draft_ref", draftRefs);
            DeleteWhereIn(connection, transaction, "validations", "validation_ref", validationRefs);
            DeleteWhereIn(connection, transaction, "compatibility_reports", "report_ref", compatibilityRefs);
            DeleteWhereIn(connection, transaction, "sessions", "session_ref", sessionRefs);
            DeleteWhereIn(connection, transaction, "drafts", "draft_ref", draftRefs);
            DeleteWhereIn(connection, transaction, "revisions", "revision_ref", revisionRefs);
            DeleteWhereIn(connection, transaction, "archived_library_entries", "subject_ref", sessionRefs.Concat(revisionRefs).ToHashSet(StringComparer.Ordinal));
            transaction.Commit();

            foreach (var revisionRef in revisionRefs) DeleteRevisionFile(revisionRef);
            var response = new DeleteMapLibraryEntriesResponse { DeletedEntryCount = entries.Count };
            response.DeletedMappingSessionRefs.AddRange(sessionRefs.Order(StringComparer.Ordinal));
            response.DeletedMapDraftRefs.AddRange(draftRefs.Order(StringComparer.Ordinal));
            response.DeletedMapRevisionRefs.AddRange(revisionRefs.Order(StringComparer.Ordinal));
            return response;
        }
    }

    public void SaveCompatibility(CompatibilityReport report)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Upsert(connection, transaction, "compatibility_reports", "report_ref", report.CompatibilityReportRef, report.ToByteArray(), report.CompletedAt);
            transaction.Commit();
        }
    }

    public void AppendAudit(string eventRef, string eventKind, string subjectRef, string principal, string createdAt, string safeSummary)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO audit_events(event_ref,event_kind,subject_ref,principal,created_at,safe_summary) VALUES($ref,$kind,$subject,$principal,$created,$summary)";
            command.Parameters.AddWithValue("$ref", eventRef);
            command.Parameters.AddWithValue("$kind", eventKind);
            command.Parameters.AddWithValue("$subject", subjectRef);
            command.Parameters.AddWithValue("$principal", principal);
            command.Parameters.AddWithValue("$created", createdAt);
            command.Parameters.AddWithValue("$summary", safeSummary);
            command.ExecuteNonQuery();
        }
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private byte[] Load(string table, string keyColumn, string key, string missingCode)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new UiMapException(missingCode, "The requested Map reference is required.");
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT payload FROM {table} WHERE {keyColumn}=$key";
            command.Parameters.AddWithValue("$key", key);
            var value = command.ExecuteScalar() as byte[];
            return value ?? throw new UiMapException(missingCode, "The requested Map artifact was not found.");
        }
    }

    private static T LoadMessage<T>(SqliteConnection connection, string table, string keyColumn, string key, string missingCode)
        where T : IMessage<T>, new()
    {
        if (string.IsNullOrWhiteSpace(key)) throw new UiMapException(missingCode, "The requested Map reference is required.");
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM {table} WHERE {keyColumn}=$key";
        command.Parameters.AddWithValue("$key", key);
        var value = command.ExecuteScalar() as byte[]
            ?? throw new UiMapException(missingCode, "The requested Map artifact was not found.");
        return new MessageParser<T>(() => new T()).ParseFrom(value);
    }

    private static IReadOnlyList<T> SelectMessages<T>(SqliteConnection connection, string table)
        where T : IMessage<T>, new()
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM {table}";
        using var reader = command.ExecuteReader();
        var parser = new MessageParser<T>(() => new T());
        var values = new List<T>();
        while (reader.Read()) values.Add(parser.ParseFrom((byte[])reader[0]));
        return values;
    }

    private static void DeleteWhereIn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        IEnumerable<string> values)
    {
        var distinct = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = distinct.Select((_, index) => "$value" + index).ToArray();
        command.CommandText = $"DELETE FROM {table} WHERE {column} IN ({string.Join(',', parameters)})";
        for (var index = 0; index < distinct.Length; index++) command.Parameters.AddWithValue(parameters[index], distinct[index]);
        command.ExecuteNonQuery();
    }

    private void DeleteRevisionFile(string revisionRef)
    {
        const string prefix = "uimaps:map-revision:sha256:";
        if (!revisionRef.StartsWith(prefix, StringComparison.Ordinal)) return;
        var hash = revisionRef[prefix.Length..];
        if (hash.Length != 64 || hash.Any(value => !Uri.IsHexDigit(value))) return;
        var path = EnsureChildPath(Path.Combine(_root, "revisions", "sha256", hash[..2], hash + ".uimap.pb"));
        if (File.Exists(path)) File.Delete(path);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS schema_info(version INTEGER NOT NULL);
                INSERT INTO schema_info(version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM schema_info);
                CREATE TABLE IF NOT EXISTS sessions(session_ref TEXT PRIMARY KEY, payload BLOB NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS drafts(draft_ref TEXT PRIMARY KEY, payload BLOB NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS validations(validation_ref TEXT PRIMARY KEY, payload BLOB NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS revisions(revision_ref TEXT PRIMARY KEY, payload BLOB NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS review_commands(
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    command_ref TEXT UNIQUE NOT NULL,
                    draft_ref TEXT NOT NULL,
                    command_payload BLOB NOT NULL,
                    result_payload BLOB NOT NULL,
                    created_at TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS review_commands_draft ON review_commands(draft_ref, sequence);
                CREATE TABLE IF NOT EXISTS review_stacks(
                    draft_ref TEXT PRIMARY KEY,
                    undo_payload BLOB NOT NULL,
                    redo_payload BLOB NOT NULL,
                    updated_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS compatibility_reports(report_ref TEXT PRIMARY KEY, payload BLOB NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS audit_events(
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_ref TEXT UNIQUE NOT NULL,
                    event_kind TEXT NOT NULL,
                    subject_ref TEXT NOT NULL,
                    principal TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    safe_summary TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS audit_events_subject ON audit_events(subject_ref, sequence);
                CREATE TABLE IF NOT EXISTS archived_library_entries(
                    subject_ref TEXT PRIMARY KEY,
                    archived_at TEXT NOT NULL,
                    archived_by TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS ready_map_selections(
                    application_ref TEXT NOT NULL,
                    application_version TEXT NOT NULL,
                    revision_ref TEXT NOT NULL,
                    changed_at TEXT NOT NULL,
                    changed_by TEXT NOT NULL,
                    PRIMARY KEY(application_ref, application_version));
                CREATE UNIQUE INDEX IF NOT EXISTS ready_map_selections_revision ON ready_map_selections(revision_ref);
                """;
            command.ExecuteNonQuery();
            using var version = connection.CreateCommand();
            version.CommandText = "SELECT version FROM schema_info LIMIT 1";
            if (Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new UiMapException("MAP_STORE_SCHEMA_UNSUPPORTED", "The Map store schema version is unsupported.");
        }
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction transaction, string table, string keyColumn, string key, byte[] payload, string updatedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {table}({keyColumn},payload,updated_at) VALUES($key,$payload,$updated) ON CONFLICT({keyColumn}) DO UPDATE SET payload=excluded.payload,updated_at=excluded.updated_at";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$updated", updatedAt);
        command.ExecuteNonQuery();
    }

    private static byte[] SerializeStack(Stack<UiMapDraft> stack)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(stack.Count);
        foreach (var draft in stack)
        {
            var bytes = draft.ToByteArray();
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        writer.Flush();
        return output.ToArray();
    }

    private static Stack<UiMapDraft> DeserializeStack(byte[] payload)
    {
        if (payload.Length == 0) return new Stack<UiMapDraft>();
        using var input = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: false);
        var count = reader.ReadInt32();
        if (count < 0 || count > 100_000) throw new UiMapException("MAP_STORE_HISTORY_INVALID", "Stored review history is invalid.");
        var topFirst = new List<UiMapDraft>(count);
        for (var index = 0; index < count; index++)
        {
            var length = reader.ReadInt32();
            if (length < 0 || length > input.Length - input.Position)
                throw new UiMapException("MAP_STORE_HISTORY_INVALID", "Stored review history is truncated.");
            topFirst.Add(UiMapDraft.Parser.ParseFrom(reader.ReadBytes(length)));
        }
        var result = new Stack<UiMapDraft>();
        for (var index = topFirst.Count - 1; index >= 0; index--) result.Push(topFirst[index]);
        return result;
    }

    private string EnsureChildPath(string path)
    {
        var fullRoot = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new UiMapException("MAP_STORE_PATH_INVALID", "A Map store path escaped the configured data root.");
        return fullPath;
    }
}

public static class UiMapCanonicalizer
{
    public static byte[] SerializeCanonical(UiMapRevision source)
    {
        var revision = source.Clone();
        revision.MapRevisionRef = "";
        revision.CanonicalPayloadHash = "";
        Sort(revision);
        return revision.ToByteArray();
    }

    public static string HashDraft(UiMapDraft source)
    {
        var draft = source.Clone();
        draft.Etag = "";
        draft.UpdatedAt = "";
        Sort(draft);
        return UiMapStore.Hash(draft.ToByteArray());
    }

    public static void Sort(UiMapDraft draft)
    {
        Replace(draft.Sources, draft.Sources.OrderBy(x => x.SourceObservationSetRef, StringComparer.Ordinal));
        Replace(draft.Entities, draft.Entities.OrderBy(x => x.MapEntityRef, StringComparer.Ordinal));
        Replace(draft.Relationships, draft.Relationships.OrderBy(x => x.RelationshipKind).ThenBy(x => x.FromEntityRef, StringComparer.Ordinal).ThenBy(x => x.ToEntityRef, StringComparer.Ordinal));
        Replace(draft.Dispositions, draft.Dispositions.OrderBy(x => x.SourceObservationSetRef, StringComparer.Ordinal).ThenBy(x => x.SourceWorldObjectRef, StringComparer.Ordinal));
        Replace(draft.ReviewItems, draft.ReviewItems.OrderByDescending(x => x.Blocking).ThenBy(x => x.ReviewItemRef, StringComparer.Ordinal));
        Replace(draft.Interactions, draft.Interactions.OrderBy(x => x.SequenceNumber).ThenBy(x => x.InteractionRef, StringComparer.Ordinal));
        Replace(draft.GridHostDeclarations, draft.GridHostDeclarations.OrderBy(x => x.DeclarationRef, StringComparer.Ordinal));
        Replace(draft.EvidenceStreamSelections, draft.EvidenceStreamSelections.OrderBy(x => x.StreamKind));
        Replace(draft.HostedGridObservations, draft.HostedGridObservations.OrderBy(x => x.GridObservationRef, StringComparer.Ordinal));
        foreach (var source in draft.Sources) Sort(source);
        foreach (var declaration in draft.GridHostDeclarations) Sort(declaration);
        foreach (var observation in draft.HostedGridObservations) Sort(observation);
        foreach (var entity in draft.Entities) Sort(entity);
    }

    public static void Sort(UiMapRevision revision)
    {
        Replace(revision.ParentRevisionRefs, revision.ParentRevisionRefs.Order(StringComparer.Ordinal));
        Replace(revision.Sources, revision.Sources.OrderBy(x => x.SourceObservationSetRef, StringComparer.Ordinal));
        Replace(revision.Entities, revision.Entities.OrderBy(x => x.MapEntityRef, StringComparer.Ordinal));
        Replace(revision.Relationships, revision.Relationships.OrderBy(x => x.RelationshipKind).ThenBy(x => x.FromEntityRef, StringComparer.Ordinal).ThenBy(x => x.ToEntityRef, StringComparer.Ordinal));
        Replace(revision.DispositionSummary, revision.DispositionSummary.OrderBy(x => x.SourceObservationSetRef, StringComparer.Ordinal).ThenBy(x => x.SourceWorldObjectRef, StringComparer.Ordinal));
        Replace(revision.Interactions, revision.Interactions.OrderBy(x => x.SequenceNumber).ThenBy(x => x.InteractionRef, StringComparer.Ordinal));
        Replace(revision.GridHostDeclarations, revision.GridHostDeclarations.OrderBy(x => x.DeclarationRef, StringComparer.Ordinal));
        Replace(revision.EvidenceStreamSelections, revision.EvidenceStreamSelections.OrderBy(x => x.StreamKind));
        foreach (var source in revision.Sources) Sort(source);
        foreach (var declaration in revision.GridHostDeclarations) Sort(declaration);
        foreach (var entity in revision.Entities) Sort(entity);
    }

    private static void Sort(SourceObservationSet source)
    {
        Replace(source.EvidenceRefs, source.EvidenceRefs.Order(StringComparer.Ordinal));
        Replace(source.ProviderVersions, source.ProviderVersions.Order(StringComparer.Ordinal));
        Replace(source.CoverageLimitations, source.CoverageLimitations.Order(StringComparer.Ordinal));
        Replace(source.SurfaceObservations, source.SurfaceObservations.OrderBy(x => x.SourceWorldObjectRef, StringComparer.Ordinal));
        Replace(source.EvidenceStreamSummaries, source.EvidenceStreamSummaries.OrderBy(x => x.StreamKind));
        foreach (var summary in source.EvidenceStreamSummaries)
            Replace(summary.Limitations, summary.Limitations.Order(StringComparer.Ordinal));
    }

    private static void Sort(DataGridHostDeclaration declaration)
    {
        Replace(declaration.SurfaceVariantRefs, declaration.SurfaceVariantRefs.Order(StringComparer.Ordinal));
        Replace(declaration.EvidenceLinkRefs, declaration.EvidenceLinkRefs.Order(StringComparer.Ordinal));
        Replace(declaration.Limitations, declaration.Limitations.Order(StringComparer.Ordinal));
    }

    private static void Sort(HostedDataGridObservation observation)
    {
        Replace(observation.ProviderCapabilities, observation.ProviderCapabilities.Order());
        Replace(observation.Coverage, observation.Coverage.Order());
        Replace(observation.Limitations, observation.Limitations.Order(StringComparer.Ordinal));
    }

    private static void Sort(MapEntity entity)
    {
        Replace(entity.Assertions, entity.Assertions.OrderBy(x => x.AssertionRef, StringComparer.Ordinal));
        Replace(entity.Matchers, entity.Matchers.OrderBy(x => x.MatcherRef, StringComparer.Ordinal));
        Replace(entity.EvidenceLinks, entity.EvidenceLinks.OrderBy(x => x.EvidenceLinkRef, StringComparer.Ordinal));
        Replace(entity.Limitations, entity.Limitations.Order(StringComparer.Ordinal));
        if (entity.HostedDataGrid?.ExplorationReceipt is { } exploration)
        {
            Replace(exploration.Coverage, exploration.Coverage.Order());
            Replace(exploration.Limitations, exploration.Limitations.Order(StringComparer.Ordinal));
        }
    }

    private static void Replace<T>(Google.Protobuf.Collections.RepeatedField<T> field, IEnumerable<T> values)
    {
        var materialized = values.ToList();
        field.Clear();
        field.AddRange(materialized);
    }
}
