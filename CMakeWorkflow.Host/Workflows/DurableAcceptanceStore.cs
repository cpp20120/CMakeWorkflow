using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Host;

public sealed class DurableAcceptanceStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly IOperationCheckpointEvidenceCodec checkpointCodec;
    private readonly string workspace;
    public string WorkspaceId { get; }

    public DurableAcceptanceStore(string workspaceDirectory, bool readOnly = false)
        : this(workspaceDirectory, readOnly, CMakeWorkflowCheckpointEvidenceCodec.Instance)
    {
    }

    internal DurableAcceptanceStore(string workspaceDirectory, bool readOnly,
        IOperationCheckpointEvidenceCodec checkpointCodec)
    {
        this.checkpointCodec = checkpointCodec ?? throw new ArgumentNullException(nameof(checkpointCodec));
        if (!Path.IsPathFullyQualified(workspaceDirectory) || !Directory.Exists(workspaceDirectory))
            throw new ArgumentException("An existing absolute workspace directory is required.",
                nameof(workspaceDirectory));
        workspace = new WorkspaceReadPaths(workspaceDirectory).WorkspaceDirectory;
        var storeDirectory = Path.Combine(workspace, ".cmakeworkflow");
        if (!readOnly) Directory.CreateDirectory(storeDirectory);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(storeDirectory, "operations.sqlite"),
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        connection.Open();
        try
        {
            if (!readOnly) Execute("PRAGMA journal_mode=DELETE;");
            if (!readOnly) Execute("PRAGMA synchronous=FULL;");
            Execute("PRAGMA busy_timeout=30000;");
            if (!readOnly) Execute("CREATE TABLE IF NOT EXISTS workspace_identity (id TEXT NOT NULL PRIMARY KEY);");
            if (!readOnly)
                Execute("""
                        CREATE TABLE IF NOT EXISTS accepted_workflows (
                          request_id TEXT NOT NULL PRIMARY KEY,
                          operation_id TEXT NOT NULL UNIQUE,
                          request_digest TEXT NOT NULL,
                          manifest_sha256 TEXT NOT NULL,
                          manifest_base_directory TEXT NOT NULL,
                          snapshot BLOB NOT NULL,
                          state TEXT NOT NULL CHECK(state IN ('accepted_not_dispatched', 'dispatch_authorized'))
                        );
                        """);
            // Additive migration. Old accepted_workflows remain unmodified and
            // mean 'workflow'; new non-CMake operations carry a durable domain tag.
            if (!readOnly)
                Execute("""
                        CREATE TABLE IF NOT EXISTS operation_domains (
                          operation_id TEXT NOT NULL PRIMARY KEY,
                          domain_kind TEXT NOT NULL,
                          FOREIGN KEY(operation_id) REFERENCES accepted_workflows(operation_id)
                        );
                        """);
            if (!readOnly)
                Execute("""
                        CREATE TABLE IF NOT EXISTS dispatch_checkpoints (
                          operation_id TEXT NOT NULL PRIMARY KEY,
                          run_directory TEXT NOT NULL,
                          checkpoint_sha256 TEXT NOT NULL,
                          FOREIGN KEY(operation_id) REFERENCES accepted_workflows(operation_id)
                        );
                        """);
            if (!readOnly)
                Execute("""
                        CREATE TABLE IF NOT EXISTS operation_results_reservations (
                          run_directory TEXT NOT NULL PRIMARY KEY,
                          operation_id TEXT NOT NULL UNIQUE,
                          FOREIGN KEY(operation_id) REFERENCES accepted_workflows(operation_id)
                        );
                        """);
            if (!readOnly)
                Execute("""
                        CREATE TABLE IF NOT EXISTS operation_finalizations (
                          operation_id TEXT NOT NULL PRIMARY KEY,
                          run_directory TEXT NOT NULL,
                          report_sha256 TEXT NOT NULL,
                          terminal_state TEXT NOT NULL CHECK(terminal_state IN ('succeeded','failed','canceled')),
                          exit_code INTEGER NOT NULL,
                          FOREIGN KEY(operation_id) REFERENCES accepted_workflows(operation_id)
                        );
                        """);
            if (!readOnly)
                Execute("""
                        CREATE TABLE IF NOT EXISTS operation_cancellations (
                          operation_id TEXT NOT NULL PRIMARY KEY,
                          revision INTEGER NOT NULL CHECK(revision = 1),
                          requested_at TEXT NOT NULL,
                          FOREIGN KEY(operation_id) REFERENCES accepted_workflows(operation_id)
                        );
                        """);
            if (!readOnly)
                Execute("""
                        CREATE TABLE IF NOT EXISTS operation_reconciliations (
                          operation_id TEXT NOT NULL PRIMARY KEY,
                          revision INTEGER NOT NULL CHECK(revision = 1),
                          decision TEXT NOT NULL CHECK(decision IN ('needs_attention','outcome_unknown')),
                          note TEXT NOT NULL,
                          decided_at TEXT NOT NULL,
                          FOREIGN KEY(operation_id) REFERENCES accepted_workflows(operation_id)
                        );
                        """);
            if (!readOnly)
                Execute("""
                        CREATE TABLE IF NOT EXISTS operation_resume_admissions (
                          operation_id TEXT NOT NULL PRIMARY KEY,
                          reconciliation_revision INTEGER NOT NULL,
                          revision INTEGER NOT NULL CHECK(revision = 2),
                          admitted_at TEXT NOT NULL,
                          FOREIGN KEY(operation_id) REFERENCES accepted_workflows(operation_id)
                        );
                        """);
            if (!readOnly)
                Execute("""
                        CREATE TABLE IF NOT EXISTS operation_resume_attempts (
                          operation_id TEXT NOT NULL PRIMARY KEY,
                          attempt_id TEXT NOT NULL UNIQUE,
                          checkpoint_sha256 TEXT NOT NULL,
                          started_at TEXT NOT NULL,
                          FOREIGN KEY(operation_id) REFERENCES accepted_workflows(operation_id)
                        );
                        """);
            // Upgrade the original acceptance-only schema in place, without dropping
            // accepted requests or immutable snapshots. Schema change is transactional.
            if (!readOnly) MigrateAcceptanceStateConstraint();
            using var transaction = readOnly ? null : connection.BeginTransaction();
            if (!readOnly)
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT OR IGNORE INTO workspace_identity(id) VALUES ($id);";
                    insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                    insert.ExecuteNonQuery();
                }

            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT id FROM workspace_identity LIMIT 1;";
                WorkspaceId = (string?)read.ExecuteScalar() ??
                              throw new InvalidDataException("Workspace identity missing.");
            }

            transaction?.Commit();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void MigrateAcceptanceStateConstraint()
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='accepted_workflows';";
        var definition = (string?)inspect.ExecuteScalar() ??
                         throw new InvalidDataException("Missing acceptance table.");
        if (definition.Contains("'dispatch_authorized'", StringComparison.Ordinal)) return;
        if (!definition.Contains("CHECK(state = 'accepted_not_dispatched')", StringComparison.Ordinal))
            throw new InvalidDataException("Unknown accepted_workflows schema; migration refused.");
        using var tx = connection.BeginTransaction();
        using var migrate = connection.CreateCommand();
        migrate.Transaction = tx;
        migrate.CommandText = """
                              CREATE TABLE accepted_workflows_next (
                                request_id TEXT NOT NULL PRIMARY KEY,
                                operation_id TEXT NOT NULL UNIQUE,
                                request_digest TEXT NOT NULL,
                                manifest_sha256 TEXT NOT NULL,
                                manifest_base_directory TEXT NOT NULL,
                                snapshot BLOB NOT NULL,
                                state TEXT NOT NULL CHECK(state IN ('accepted_not_dispatched', 'dispatch_authorized'))
                              );
                              INSERT INTO accepted_workflows_next SELECT * FROM accepted_workflows;
                              DROP TABLE accepted_workflows;
                              ALTER TABLE accepted_workflows_next RENAME TO accepted_workflows;
                              """;
        migrate.ExecuteNonQuery();
        tx.Commit();
    }

    // Caller must have authorized the workspace. Digest is derived entirely from the
    // original typed request, never from current manifest contents on a retry.
    // Legacy CMake admission adapter: retain the exact v1 digest and path semantics.
    // The storage layer below accepts a caller-derived immutable request identity.
    public AcceptedWorkflow Accept(string clientRequestId, string manifestPath, string expectedManifestSha256,
        int jobs, string resultsDirectory)
    {
        if (string.IsNullOrWhiteSpace(clientRequestId) || clientRequestId.Length > 128 || jobs < 1
            || !Path.IsPathFullyQualified(manifestPath) || !Path.IsPathFullyQualified(resultsDirectory)
            || !IsSha256(expectedManifestSha256))
            throw new ArgumentException("Invalid workflow admission request.");
        var paths = new WorkspaceReadPaths(workspace);
        var source = paths.Require(manifestPath);
        var result = paths.Require(resultsDirectory);
        var digest = Hex(Encoding.UTF8.GetBytes(string.Join("\n", "workflow/start:v1", source,
            expectedManifestSha256.ToUpperInvariant(), jobs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            result)));
        // Do not resolve paths again between digest derivation and admission.
        return AcceptImmutableCanonical(new DurableImmutableRequest(clientRequestId, source,
            expectedManifestSha256, result, digest));
    }

    // Neutral immutable admission: the trusted domain adapter owns the request digest.
    // Idempotent retries compare the digest BEFORE reading potentially changed bytes.
    internal AcceptedWorkflow AcceptImmutable(DurableImmutableRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientRequestId) || request.ClientRequestId.Length > 128 ||
            !Path.IsPathFullyQualified(request.SnapshotPath) ||
            !Path.IsPathFullyQualified(request.ResultsDirectory) ||
            !IsSha256(request.SnapshotSha256) || !IsSha256(request.RequestDigest) ||
            string.IsNullOrWhiteSpace(request.DomainKind) || request.DomainKind.Length > 64)
            throw new ArgumentException("Invalid immutable operation admission request.");
        var paths = new WorkspaceReadPaths(workspace);
        var source = paths.Require(request.SnapshotPath);
        var result = paths.Require(request.ResultsDirectory);
        return AcceptImmutableCanonical(request with { SnapshotPath = source, ResultsDirectory = result });
    }

    private AcceptedWorkflow AcceptImmutableCanonical(DurableImmutableRequest request)
    {
        var source = request.SnapshotPath;
        var result = request.ResultsDirectory;
        var baseDirectory = Path.GetDirectoryName(source)!;
        var clientRequestId = request.ClientRequestId;
        var expectedManifestSha256 = request.SnapshotSha256;
        var digest = request.RequestDigest;
        // BEGIN IMMEDIATE serialization prevents two writers accepting the same ID.
        using var transaction = connection.BeginTransaction();
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText =
                "SELECT a.operation_id,a.request_digest,a.manifest_sha256,a.manifest_base_directory,COALESCE(f.terminal_state, CASE WHEN c.operation_id IS NOT NULL THEN 'cancel_requested' ELSE a.state END) FROM accepted_workflows a LEFT JOIN operation_finalizations f ON f.operation_id=a.operation_id LEFT JOIN operation_cancellations c ON c.operation_id=a.operation_id WHERE a.request_id=$id;";
            find.Parameters.AddWithValue("$id", clientRequestId);
            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                if (!string.Equals(reader.GetString(1), digest, StringComparison.Ordinal))
                    throw new InvalidOperationException("request_id_conflict");
                var existing = new AcceptedWorkflow(WorkspaceId, reader.GetString(0), clientRequestId,
                    reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
                return existing;
            }
        }

        // Only a new acceptance needs to validate current manifest bytes.
        var snapshot = File.ReadAllBytes(source);
        if (!string.Equals(Hex(snapshot), expectedManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("stale_manifest");
        var operationId = Guid.NewGuid().ToString("N");
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                                 INSERT INTO accepted_workflows(request_id, operation_id, request_digest, manifest_sha256,
                                   manifest_base_directory, snapshot, state)
                                 VALUES ($request,$operation,$digest,$sha,$base,$snapshot,'accepted_not_dispatched');
                                 """;
            insert.Parameters.AddWithValue("$request", clientRequestId);
            insert.Parameters.AddWithValue("$operation", operationId);
            insert.Parameters.AddWithValue("$digest", digest);
            insert.Parameters.AddWithValue("$sha", expectedManifestSha256.ToUpperInvariant());
            insert.Parameters.AddWithValue("$base", baseDirectory);
            insert.Parameters.Add("$snapshot", SqliteType.Blob).Value = snapshot;
            insert.ExecuteNonQuery();
        }

        if (request.DomainKind != "workflow")
        {
            using var tag = connection.CreateCommand();
            tag.Transaction = transaction;
            tag.CommandText =
                "INSERT INTO operation_domains(operation_id,domain_kind) VALUES($id,$kind);";
            tag.Parameters.AddWithValue("$id", operationId);
            tag.Parameters.AddWithValue("$kind", request.DomainKind);
            tag.ExecuteNonQuery();
        }

        transaction.Commit();
        return new(WorkspaceId, operationId, clientRequestId, digest, expectedManifestSha256.ToUpperInvariant(),
            baseDirectory, "accepted_not_dispatched");
    }

    // An old database may not have operation_domains. Such operations always
    // belong to the original CMake workflow domain.
    public string GetDomainKind(string operationId)
    {
        using var table = connection.CreateCommand();
        table.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name='operation_domains';";
        if (table.ExecuteScalar() is null) return "workflow";
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT domain_kind FROM operation_domains WHERE operation_id=$id;";
        read.Parameters.AddWithValue("$id", operationId);
        return (string?)read.ExecuteScalar() ?? "workflow";
    }

    internal static void ValidateListQuery(OperationListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > 200 ||
            (request.State is not null && request.State is not
                ("accepted_not_dispatched" or "dispatch_authorized" or "cancel_requested" or
                 "succeeded" or "failed" or "canceled")) ||
            (request.Domain is not null && (request.Domain.Length is < 1 or > 64 ||
                request.Domain.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))) ||
            (request.Cursor is not null && request.Cursor.Length is < 1 or > 512))
            throw new ArgumentException("Invalid operation list query.");
    }

    /// <summary>Read-only keyset traversal of admitted operations, ordered by SQLite rowid descending.</summary>
    /// <remarks>The first cursor captures an admission high-water mark, not a snapshot of mutable states.
    /// Cursor tokens are tied to the workspace and exact filters, but are not authorization tokens.</remarks>
    public OperationListPage ListOperations(OperationListRequest request, CancellationToken ct = default)
    {
        ValidateListQuery(request);
        ct.ThrowIfCancellationRequested();
        long watermark, before;
        if (request.Cursor is not null)
        {
            (watermark, before) = DecodeOperationCursor(request.Cursor, request.State, request.Domain);
        }
        else
        {
            using var max = connection.CreateCommand();
            max.CommandText = "SELECT COALESCE(MAX(rowid), 0) FROM accepted_workflows;";
            watermark = (long)max.ExecuteScalar()!;
            before = long.MaxValue;
        }
        if (watermark == 0) return new(1, [], null);

        // Legacy databases may not have operation_domains; all their operations are CMake workflows.
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='operation_domains';";
        var hasDomains = check.ExecuteScalar() is not null;
        const string effectiveState = "COALESCE(f.terminal_state, CASE WHEN c.operation_id IS NOT NULL THEN 'cancel_requested' ELSE a.state END)";
        var domain = hasDomains ? "COALESCE(d.domain_kind, 'workflow')" : "'workflow'";
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT a.rowid, a.operation_id, a.request_id, a.manifest_sha256,
                   a.manifest_base_directory, {effectiveState}, {domain}
            FROM accepted_workflows a
            LEFT JOIN operation_finalizations f ON f.operation_id = a.operation_id
            LEFT JOIN operation_cancellations c ON c.operation_id = a.operation_id
            {(hasDomains ? "LEFT JOIN operation_domains d ON d.operation_id = a.operation_id" : "")}
            WHERE a.rowid <= $watermark AND a.rowid < $before
              AND ($state IS NULL OR {effectiveState} = $state)
              AND ($domain IS NULL OR {domain} = $domain)
            ORDER BY a.rowid DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$watermark", watermark);
        cmd.Parameters.AddWithValue("$before", before);
        cmd.Parameters.AddWithValue("$state", (object?)request.State ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$domain", (object?)request.Domain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", request.Limit + 1);
        var items = new List<OperationReference>(request.Limit);
        long lastRowId = 0;
        bool hasMore = false;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (items.Count == request.Limit) { hasMore = true; break; }
                lastRowId = reader.GetInt64(0);
                items.Add(new OperationReference(WorkspaceId, reader.GetString(1), reader.GetString(2),
                    reader.GetString(6), reader.GetString(5), reader.GetString(3), reader.GetString(4)));
            }
        }
        return new(1, items, hasMore
            ? EncodeOperationCursor(watermark, lastRowId, request.State, request.Domain) : null);
    }

    private string EncodeOperationCursor(long watermark, long before, string? state, string? domain)
    {
        var value = string.Join("|", "v1", WorkspaceId,
            watermark.ToString(System.Globalization.CultureInfo.InvariantCulture),
            before.ToString(System.Globalization.CultureInfo.InvariantCulture), state ?? "", domain ?? "");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');
    }

    private (long Watermark, long Before) DecodeOperationCursor(string cursor, string? state, string? domain)
    {
        try
        {
            if (cursor.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
                throw new FormatException();
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            var padding = (4 - encoded.Length % 4) % 4;
            var fields = Encoding.UTF8.GetString(Convert.FromBase64String(encoded + new string('=', padding)))
                .Split('|');
            if (fields.Length != 6 || fields[0] != "v1" || fields[1] != WorkspaceId ||
                fields[4] != (state ?? "") || fields[5] != (domain ?? "") ||
                !long.TryParse(fields[2], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var watermark) ||
                !long.TryParse(fields[3], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var before) ||
                watermark < 1 || before < 1 || before > watermark)
                throw new FormatException();
            return (watermark, before);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or DecoderFallbackException)
        {
            throw new ArgumentException("Invalid or mismatched operation list cursor.", nameof(cursor), e);
        }
    }

    public AcceptedWorkflow? Find(string clientRequestId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT a.operation_id,a.request_digest,a.manifest_sha256,a.manifest_base_directory,COALESCE(f.terminal_state, CASE WHEN c.operation_id IS NOT NULL THEN 'cancel_requested' ELSE a.state END) FROM accepted_workflows a LEFT JOIN operation_finalizations f ON f.operation_id=a.operation_id LEFT JOIN operation_cancellations c ON c.operation_id=a.operation_id WHERE a.request_id=$id;";
        command.Parameters.AddWithValue("$id", clientRequestId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(WorkspaceId, reader.GetString(0), clientRequestId, reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4))
            : null;
    }

    public AcceptedWorkflow? Get(string operationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT a.request_id,a.request_digest,a.manifest_sha256,a.manifest_base_directory,COALESCE(f.terminal_state, CASE WHEN c.operation_id IS NOT NULL THEN 'cancel_requested' ELSE a.state END) FROM accepted_workflows a LEFT JOIN operation_finalizations f ON f.operation_id=a.operation_id LEFT JOIN operation_cancellations c ON c.operation_id=a.operation_id WHERE a.operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(WorkspaceId, operationId, reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4))
            : null;
    }

    // Inspection does not read mutable run.json or infer whether a process is alive.
    // A dispatch authorization is not proof of a completed external effect.
    public sealed record RecoveryInspection(
        string OperationId,
        string AcceptanceState,
        string? TerminalState,
        bool CancellationRequested,
        string? RunDirectory,
        string? CheckpointSha256,
        string? Decision,
        long Revision,
        string? Note);

    public RecoveryInspection InspectRecovery(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        // Old stores opened read-only may not have the reconciliation table yet.
        using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='operation_reconciliations';";
        var hasDecisions = schema.ExecuteScalar() is not null;
        schema.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='operation_resume_admissions';";
        var hasResume = schema.ExecuteScalar() is not null;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = hasDecisions && hasResume ? """
                                                      SELECT a.state, f.terminal_state, c.operation_id, d.run_directory,
                                                             d.checkpoint_sha256, r.decision, COALESCE(x.revision,r.revision), r.note
                                                      FROM accepted_workflows a
                                                      LEFT JOIN operation_finalizations f ON f.operation_id=a.operation_id
                                                      LEFT JOIN operation_cancellations c ON c.operation_id=a.operation_id
                                                      LEFT JOIN dispatch_checkpoints d ON d.operation_id=a.operation_id
                                                      LEFT JOIN operation_reconciliations r ON r.operation_id=a.operation_id
                                                      LEFT JOIN operation_resume_admissions x ON x.operation_id=a.operation_id
                                                      WHERE a.operation_id=$id;
                                                      """ :
            hasDecisions ? """
                           SELECT a.state, f.terminal_state, c.operation_id, d.run_directory,
                                  d.checkpoint_sha256, r.decision, r.revision, r.note
                           FROM accepted_workflows a
                           LEFT JOIN operation_finalizations f ON f.operation_id=a.operation_id
                           LEFT JOIN operation_cancellations c ON c.operation_id=a.operation_id
                           LEFT JOIN dispatch_checkpoints d ON d.operation_id=a.operation_id
                           LEFT JOIN operation_reconciliations r ON r.operation_id=a.operation_id
                           WHERE a.operation_id=$id;
                           """ : """
                                 SELECT a.state, f.terminal_state, c.operation_id, d.run_directory,
                                        d.checkpoint_sha256, NULL, NULL, NULL
                                 FROM accepted_workflows a
                                 LEFT JOIN operation_finalizations f ON f.operation_id=a.operation_id
                                 LEFT JOIN operation_cancellations c ON c.operation_id=a.operation_id
                                 LEFT JOIN dispatch_checkpoints d ON d.operation_id=a.operation_id
                                 WHERE a.operation_id=$id;
                                 """;
        cmd.Parameters.AddWithValue("$id", operationId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Unknown operation.");
        return new(operationId, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            !reader.IsDBNull(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    // Deliberately does NOT authorize dispatch, alter acceptance, or release results ownership.
    // Requires an operator-supplied nonempty rationale; a decision is immutable in this stage.
    public RecoveryInspection Reconcile(string operationId, long expectedRevision, string decision, string note)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128 || expectedRevision < 0 ||
            decision is not ("needs_attention" or "outcome_unknown") ||
            string.IsNullOrWhiteSpace(note) || note.Length > 4096)
            throw new ArgumentException("Invalid reconciliation decision.");
        using var tx = connection.BeginTransaction();
        string acceptance;
        bool terminal;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                              SELECT a.state, f.operation_id FROM accepted_workflows a
                              LEFT JOIN operation_finalizations f ON f.operation_id=a.operation_id
                              WHERE a.operation_id=$id;
                              """;
            cmd.Parameters.AddWithValue("$id", operationId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException("Unknown operation.");
            acceptance = reader.GetString(0);
            terminal = !reader.IsDBNull(1);
        }

        if (terminal) throw new InvalidOperationException("operation_already_terminal");
        if (acceptance != "dispatch_authorized")
            throw new InvalidOperationException("operation_not_dispatch_authorized");
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText =
                "SELECT revision,decision,note FROM operation_reconciliations WHERE operation_id=$id;";
            existing.Parameters.AddWithValue("$id", operationId);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var revision = reader.GetInt64(0);
                if (expectedRevision == revision && reader.GetString(1) == decision && reader.GetString(2) == note)
                {
                    tx.Commit();
                    return InspectRecovery(operationId);
                }

                throw new InvalidOperationException("operation_revision_conflict");
            }
        }

        if (expectedRevision != 0) throw new InvalidOperationException("operation_revision_conflict");
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                              INSERT INTO operation_reconciliations(operation_id,revision,decision,note,decided_at)
                              VALUES($id,1,$decision,$note,$at);
                              """;
            cmd.Parameters.AddWithValue("$id", operationId);
            cmd.Parameters.AddWithValue("$decision", decision);
            cmd.Parameters.AddWithValue("$note", note);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return InspectRecovery(operationId);
    }

    // Resume admission is deliberately NOT external dispatch. The runner must later
    // validate each pending node against its durable checkpoint before execution.
    // Once admitted, a repeated RPC is idempotent; the reconciliation decision stays immutable.
    public RecoveryInspection AdmitResume(string operationId, long expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128 || expectedRevision < 0)
            throw new ArgumentException("Invalid resume request.");
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                              SELECT r.revision,r.decision,a.state,f.operation_id,c.operation_id,
                                     x.revision
                              FROM accepted_workflows a
                              LEFT JOIN operation_reconciliations r ON r.operation_id=a.operation_id
                              LEFT JOIN operation_finalizations f ON f.operation_id=a.operation_id
                              LEFT JOIN operation_cancellations c ON c.operation_id=a.operation_id
                              LEFT JOIN operation_resume_admissions x ON x.operation_id=a.operation_id
                              WHERE a.operation_id=$id;
                              """;
            cmd.Parameters.AddWithValue("$id", operationId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException("Unknown operation.");
            if (!reader.IsDBNull(3) || !reader.IsDBNull(4))
                throw new InvalidOperationException("operation_terminal_or_cancel_requested");
            if (reader.GetString(2) != "dispatch_authorized" || reader.IsDBNull(0))
                throw new InvalidOperationException("operation_not_reconciled");
            var revision = reader.GetInt64(0);
            if (!reader.IsDBNull(5))
            {
                if (expectedRevision != reader.GetInt64(5))
                    throw new InvalidOperationException("operation_revision_conflict");
                tx.Commit();
                return InspectRecovery(operationId);
            }

            if (reader.GetString(1) == "outcome_unknown")
                throw new InvalidOperationException("outcome_unknown_requires_resolution");
            if (expectedRevision != revision)
                throw new InvalidOperationException("operation_revision_conflict");
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                              INSERT INTO operation_resume_admissions(operation_id,reconciliation_revision,revision,admitted_at)
                              VALUES($id,$expected,2,$at);
                              """;
            cmd.Parameters.AddWithValue("$id", operationId);
            cmd.Parameters.AddWithValue("$expected", expectedRevision);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return InspectRecovery(operationId);
    }

    // One-way, non-retryable claim. A crash after this commit cannot implicitly
    // start another runner, even if no process was launched yet.
    public string ClaimResumeAttempt(string operationId, string runDirectory)
    {
        var directory = new WorkspaceReadPaths(workspace).Require(runDirectory);
        var bytes = File.ReadAllBytes(new WorkspaceReadPaths(workspace).Require(Path.Combine(directory, "run.json")));
        var evidence = checkpointCodec.ForResume(bytes);
        var hash = Hex(bytes);
        using var tx = connection.BeginTransaction();
        using (var verify = connection.CreateCommand())
        {
            verify.Transaction = tx;
            verify.CommandText = """
                                 SELECT a.state,a.manifest_sha256,d.run_directory,r.decision,x.revision,
                                        f.operation_id,c.operation_id,d.checkpoint_sha256
                                 FROM accepted_workflows a
                                 LEFT JOIN dispatch_checkpoints d ON d.operation_id=a.operation_id
                                 LEFT JOIN operation_reconciliations r ON r.operation_id=a.operation_id
                                 LEFT JOIN operation_resume_admissions x ON x.operation_id=a.operation_id
                                 LEFT JOIN operation_finalizations f ON f.operation_id=a.operation_id
                                 LEFT JOIN operation_cancellations c ON c.operation_id=a.operation_id
                                 WHERE a.operation_id=$id;
                                 """;
            verify.Parameters.AddWithValue("$id", operationId);
            using var reader = verify.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException("Unknown operation.");
            if (reader.GetString(0) != "dispatch_authorized" ||
                !string.Equals(reader.GetString(1), evidence.SnapshotSha256, StringComparison.OrdinalIgnoreCase) ||
                reader.IsDBNull(2) || !PathComparer.Equals(reader.GetString(2), directory) ||
                reader.IsDBNull(3) || reader.GetString(3) != "needs_attention" ||
                reader.IsDBNull(4) || reader.GetInt64(4) != 2 ||
                !reader.IsDBNull(5) || !reader.IsDBNull(6) || reader.IsDBNull(7))
                throw new InvalidOperationException("resume_not_admitted_or_bound");
        }

        // This insert is deliberately NOT an upsert. Existing attempt = no redispatch.
        var id = Guid.NewGuid().ToString("N");
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                                 INSERT INTO operation_resume_attempts(operation_id,attempt_id,checkpoint_sha256,started_at)
                                 VALUES($id,$attempt,$hash,$at);
                                 """;
            insert.Parameters.AddWithValue("$id", operationId);
            insert.Parameters.AddWithValue("$attempt", id);
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            try
            {
                insert.ExecuteNonQuery();
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException("resume_attempt_already_claimed", e);
            }
        }

        tx.Commit();
        return id;
    }

    public sealed record CancellationDecision(string OperationId, string State, long Revision, bool Requested);

    // ExpectedRevision is a CAS against the cancellation-intent revision (0 -> 1).
    // This is a request to stop, never proof that an external process has stopped.
    public CancellationDecision RequestCancellation(string operationId, long expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128 || expectedRevision < 0)
            throw new ArgumentException("Invalid cancellation request.");
        using var tx = connection.BeginTransaction();
        string state;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                               SELECT COALESCE(f.terminal_state,a.state)
                               FROM accepted_workflows a LEFT JOIN operation_finalizations f ON f.operation_id=a.operation_id
                               WHERE a.operation_id=$id;
                               """;
            read.Parameters.AddWithValue("$id", operationId);
            state = (string?)read.ExecuteScalar() ?? throw new KeyNotFoundException("Unknown operation.");
        }

        long revision;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT revision FROM operation_cancellations WHERE operation_id=$id;";
            read.Parameters.AddWithValue("$id", operationId);
            revision = (long?)read.ExecuteScalar() ?? 0;
        }

        if (state is "succeeded" or "failed" or "canceled")
        {
            tx.Commit();
            return new(operationId, state, revision, false);
        }

        if (expectedRevision != revision)
            throw new InvalidOperationException("operation_revision_conflict");
        if (revision == 0)
        {
            using var write = connection.CreateCommand();
            write.Transaction = tx;
            write.CommandText =
                "INSERT INTO operation_cancellations(operation_id,revision,requested_at) VALUES($id,1,$at);";
            write.Parameters.AddWithValue("$id", operationId);
            write.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            write.ExecuteNonQuery();
            revision = 1;
        }

        tx.Commit();
        return new(operationId, "cancel_requested", revision, true);
    }

    // An operation owns its results directory durably, across processes and restarts.
    // The reservation is deliberately NOT released on a failed or interrupted run:
    // reconciliation must decide whether the external effect occurred first.
    public void ReserveResultsDirectory(string operationId, string resultsDirectory)
    {
        if (string.IsNullOrWhiteSpace(operationId) || !Path.IsPathFullyQualified(resultsDirectory))
            throw new ArgumentException("Invalid results reservation.");
        var canonical = new WorkspaceReadPaths(workspace).Require(resultsDirectory);
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        using var transaction = connection.BeginTransaction();
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT run_directory FROM operation_results_reservations WHERE operation_id=$id;";
            existing.Parameters.AddWithValue("$id", operationId);
            var previous = (string?)existing.ExecuteScalar();
            if (previous is not null)
            {
                if (!string.Equals(previous, canonical, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                    throw new InvalidOperationException("operation_results_directory_changed");
                transaction.Commit();
                return;
            }
        }

        using (var valid = connection.CreateCommand())
        {
            valid.Transaction = transaction;
            valid.CommandText = "SELECT COUNT(*) FROM accepted_workflows WHERE operation_id=$id;";
            valid.Parameters.AddWithValue("$id", operationId);
            if ((long)valid.ExecuteScalar()! != 1L)
                throw new KeyNotFoundException("Unknown operation.");
        }

        using (var collision = connection.CreateCommand())
        {
            collision.Transaction = transaction;
            collision.CommandText = "SELECT operation_id FROM operation_results_reservations WHERE run_directory=$dir;";
            collision.Parameters.AddWithValue("$dir", canonical);
            if (collision.ExecuteScalar() is not null)
                throw new InvalidOperationException("results_directory_reserved");
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO operation_results_reservations(run_directory,operation_id) VALUES($dir,$id);";
            insert.Parameters.AddWithValue("$dir", canonical);
            insert.Parameters.AddWithValue("$id", operationId);
            try
            {
                insert.ExecuteNonQuery();
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException("results_directory_reserved", e);
            }
        }

        transaction.Commit();
    }

    // Admission-only transition: this method does NOT invoke WorkflowRunner or any external process.
    // It must never be confused with the runner's write-ahead dispatch checkpoint.
    // For an existing SQLite store built before this transition, the CHECK constraint
    // must be migrated explicitly rather than silently bypassed.
    public AcceptedWorkflow AuthorizeDispatch(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        // BeginTransaction uses BEGIN IMMEDIATE by default in Microsoft.Data.Sqlite;
        // serialize authorization across independent connections/processes.
        using var transaction = connection.BeginTransaction();
        string requestId, digest, sha, baseDirectory, state;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText =
                "SELECT request_id,request_digest,manifest_sha256,manifest_base_directory,state FROM accepted_workflows WHERE operation_id=$id;";
            find.Parameters.AddWithValue("$id", operationId);
            using var reader = find.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException("Unknown operation.");
            requestId = reader.GetString(0);
            digest = reader.GetString(1);
            sha = reader.GetString(2);
            baseDirectory = reader.GetString(3);
            state = reader.GetString(4);
        }

        if (state != "accepted_not_dispatched" && state != "dispatch_authorized")
            throw new InvalidDataException("Unexpected acceptance state.");
        // Verify snapshot integrity BEFORE authorizing dispatch.
        using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = "SELECT snapshot FROM accepted_workflows WHERE operation_id=$id;";
            verify.Parameters.AddWithValue("$id", operationId);
            var snapshot = (byte[]?)verify.ExecuteScalar() ??
                           throw new InvalidDataException("Missing manifest snapshot.");
            if (!string.Equals(Hex(snapshot), sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Admission snapshot integrity failure.");
        }

        if (state == "accepted_not_dispatched")
        {
            using (var canceled = connection.CreateCommand())
            {
                canceled.Transaction = transaction;
                canceled.CommandText = "SELECT 1 FROM operation_cancellations WHERE operation_id=$id;";
                canceled.Parameters.AddWithValue("$id", operationId);
                if (canceled.ExecuteScalar() is not null)
                    throw new InvalidOperationException("operation_cancel_requested");
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE accepted_workflows SET state='dispatch_authorized' WHERE operation_id=$id AND state='accepted_not_dispatched';";
            update.Parameters.AddWithValue("$id", operationId);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("dispatch_authorization_conflict");
        }

        transaction.Commit();
        return new(WorkspaceId, operationId, requestId, digest, sha, baseDirectory, "dispatch_authorized");
    }

    // Called by the host-owned runner's beforeExternalDispatch gate, AFTER its
    // durable write-ahead checkpoint but BEFORE starting external processes.
    // This method never authorizes an operation or starts a runner by itself.
    public string ConfirmRunnerCheckpoint(string operationId, string runDirectory)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128 ||
            !Path.IsPathFullyQualified(runDirectory))
            throw new ArgumentException("Invalid dispatch checkpoint binding.");
        var directory = new WorkspaceReadPaths(workspace).Require(runDirectory);
        var reportPath = new WorkspaceReadPaths(workspace).Require(Path.Combine(directory, "run.json"));
        // The runner holds its exclusive run lease. Reading this file is safe only
        // under that lease; callers MUST NOT use this as an independent dispatcher.
        var bytes = File.ReadAllBytes(reportPath);
        var evidence = checkpointCodec.ForDispatch(bytes);
        var hash = Hex(bytes);
        using var transaction = connection.BeginTransaction();
        using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText =
                "SELECT manifest_sha256,state,snapshot FROM accepted_workflows WHERE operation_id=$id;";
            verify.Parameters.AddWithValue("$id", operationId);
            using var reader = verify.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException("Unknown operation.");
            if (reader.GetString(1) != "dispatch_authorized" ||
                !string.Equals(reader.GetString(0), evidence.SnapshotSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Hex((byte[])reader.GetValue(2)), reader.GetString(0),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("dispatch_checkpoint_mismatch");
        }

        using (var prior = connection.CreateCommand())
        {
            prior.Transaction = transaction;
            prior.CommandText = "SELECT run_directory FROM dispatch_checkpoints WHERE operation_id=$id;";
            prior.Parameters.AddWithValue("$id", operationId);
            var existing = (string?)prior.ExecuteScalar();
            if (existing is not null && !PathComparer.Equals(existing, directory))
                throw new InvalidOperationException("dispatch_run_directory_conflict");
        }

        using (var bind = connection.CreateCommand())
        {
            bind.Transaction = transaction;
            bind.CommandText = """
                               INSERT INTO dispatch_checkpoints(operation_id,run_directory,checkpoint_sha256)
                               VALUES ($id,$dir,$hash)
                               ON CONFLICT(operation_id) DO UPDATE SET checkpoint_sha256=excluded.checkpoint_sha256;
                               """;
            bind.Parameters.AddWithValue("$id", operationId);
            bind.Parameters.AddWithValue("$dir", directory);
            bind.Parameters.AddWithValue("$hash", hash);
            bind.ExecuteNonQuery();
        }

        transaction.Commit();
        return hash;
    }


    public sealed record Finalization(string RunDirectory, string ReportSha256, string State, int ExitCode);

    public Finalization? GetFinalization(string operationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT run_directory,report_sha256,terminal_state,exit_code FROM operation_finalizations WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3))
            : null;
    }

    // Only a normal runner return and a durable terminal report can finalize.
    public Finalization FinalizeExecution(string operationId, string runDirectory, int exitCode)
    {
        var paths = new WorkspaceReadPaths(workspace);
        var directory = paths.Require(runDirectory);
        var bytes = File.ReadAllBytes(paths.Require(Path.Combine(directory, "run.json")));
        var evidence = checkpointCodec.ForFinalization(bytes, exitCode);
        var terminalState = evidence.TerminalState ??
                            throw new InvalidDataException("Missing terminal operation evidence.");
        var hash = Hex(bytes);
        using var tx = connection.BeginTransaction();
        using (var check = connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = """
                                SELECT a.state,a.manifest_sha256,r.run_directory,d.run_directory
                                FROM accepted_workflows a
                                LEFT JOIN operation_results_reservations r ON r.operation_id=a.operation_id
                                LEFT JOIN dispatch_checkpoints d ON d.operation_id=a.operation_id
                                WHERE a.operation_id=$id;
                                """;
            check.Parameters.AddWithValue("$id", operationId);
            using var reader = check.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(2) || reader.IsDBNull(3) ||
                reader.GetString(0) != "dispatch_authorized" ||
                !string.Equals(reader.GetString(1), evidence.SnapshotSha256, StringComparison.OrdinalIgnoreCase) ||
                !PathComparer.Equals(reader.GetString(2), directory) ||
                !PathComparer.Equals(reader.GetString(3), directory))
                throw new InvalidOperationException("operation_report_binding_mismatch");
        }

        using (var decision = connection.CreateCommand())
        {
            decision.Transaction = tx;
            decision.CommandText = "SELECT 1 FROM operation_reconciliations WHERE operation_id=$id;";
            decision.Parameters.AddWithValue("$id", operationId);
            if (decision.ExecuteScalar() is not null)
            {
                using var attempt = connection.CreateCommand();
                attempt.Transaction = tx;
                attempt.CommandText = "SELECT 1 FROM operation_resume_attempts WHERE operation_id=$id;";
                attempt.Parameters.AddWithValue("$id", operationId);
                if (attempt.ExecuteScalar() is null || evidence.ResumeCount < 1)
                    throw new InvalidOperationException("operation_reconciliation_conflict");
            }
        }

        using (var prior = connection.CreateCommand())
        {
            prior.Transaction = tx;
            prior.CommandText =
                "SELECT run_directory,report_sha256,terminal_state,exit_code FROM operation_finalizations WHERE operation_id=$id;";
            prior.Parameters.AddWithValue("$id", operationId);
            using var reader = prior.ExecuteReader();
            if (reader.Read())
            {
                if (!PathComparer.Equals(reader.GetString(0), directory) ||
                    reader.GetString(1) != hash || reader.GetString(2) != terminalState ||
                    reader.GetInt32(3) != exitCode)
                    throw new InvalidOperationException("operation_finalization_conflict");
                tx.Commit();
                return new(directory, hash, terminalState, exitCode);
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText =
                "INSERT INTO operation_finalizations(operation_id,run_directory,report_sha256,terminal_state,exit_code) VALUES($id,$dir,$hash,$status,$exit);";
            insert.Parameters.AddWithValue("$id", operationId);
            insert.Parameters.AddWithValue("$dir", directory);
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$status", terminalState);
            insert.Parameters.AddWithValue("$exit", exitCode);
            insert.ExecuteNonQuery();
        }

        tx.Commit();
        return new(directory, hash, terminalState, exitCode);
    }

    public (string RunDirectory, string CheckpointSha256)? GetDispatchCheckpoint(string operationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT run_directory,checkpoint_sha256 FROM dispatch_checkpoints WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    public byte[] GetSnapshot(string operationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot,manifest_sha256 FROM accepted_workflows WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Unknown operation.");
        var bytes = (byte[])reader.GetValue(0);
        if (!string.Equals(Hex(bytes), reader.GetString(1), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Admission snapshot integrity failure.");
        return bytes;
    }

    private void Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    public void Dispose() => connection.Dispose();
}