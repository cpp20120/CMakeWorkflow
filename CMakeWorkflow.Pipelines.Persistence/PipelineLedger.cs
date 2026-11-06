using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Pipelines.Persistence;

/// <summary>Local SQLite journal of immutable pipeline admissions and stage transitions.</summary>
/// <remarks>
/// This store does not execute, validate external evidence, or contact remote providers. Only a trusted
/// Host adapter should record outcomes after checking their authoritative source. One ledger instance
/// is not safe for concurrent method calls; separate connections coordinate via SQLite transactions.
/// Before reconciling interrupted dispatches, the caller must establish exclusive Host ownership.
/// </remarks>
public sealed class PipelineLedger : IDisposable
{
    private const int MaxManifestBytes = 1024 * 1024;
    private readonly SqliteConnection db;

    public PipelineLedger(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        SQLitePCL.Batteries.Init();
        string absolutePath = Path.GetFullPath(databasePath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = absolutePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        try
        {
            db.Open();
            Execute("PRAGMA busy_timeout=5000;");
            Execute("PRAGMA foreign_keys=ON;");
            using (SqliteCommand journal = Command("PRAGMA journal_mode=WAL;"))
            {
                if (!string.Equals(journal.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Pipeline ledger requires SQLite WAL mode.");
                }
            }

            Execute("PRAGMA synchronous=FULL;");
            using SqliteCommand version = Command("PRAGMA user_version;");
            long schema = (long)(version.ExecuteScalar() ?? -1L);
            if (schema is not 0 and not 1)
            {
                throw new InvalidDataException($"Unsupported pipeline ledger schema: {schema}.");
            }

            if (schema == 0)
            {
                using SqliteCommand existing = Command("""
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE type='table' AND name NOT LIKE 'sqlite_%';
                    """);
                if ((long)(existing.ExecuteScalar() ?? 0L) != 0)
                {
                    throw new InvalidDataException("Refusing to claim an existing non-pipeline SQLite database.");
                }
            }

            using SqliteTransaction tx = db.BeginTransaction();
            Execute("""
                CREATE TABLE IF NOT EXISTS pipeline_runs (
                    operation_id TEXT NOT NULL PRIMARY KEY,
                    client_request_id TEXT NOT NULL UNIQUE,
                    manifest_json TEXT NOT NULL,
                    manifest_sha256 TEXT NOT NULL,
                    name TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK (revision >= 0),
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS pipeline_stages (
                    operation_id TEXT NOT NULL,
                    stage_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    status TEXT NOT NULL CHECK (status IN (
                        'pending', 'dispatch_intent', 'waiting_external', 'succeeded',
                        'failed', 'outcome_unknown')),
                    revision INTEGER NOT NULL CHECK (revision >= 0),
                    child_request_id TEXT,
                    child_operation_id TEXT,
                    external_reference TEXT,
                    evidence_reference TEXT,
                    PRIMARY KEY (operation_id, stage_id),
                    FOREIGN KEY (operation_id) REFERENCES pipeline_runs(operation_id)
                );
                CREATE TABLE IF NOT EXISTS pipeline_events (
                    operation_id TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK (revision >= 0),
                    stage_id TEXT,
                    event_kind TEXT NOT NULL,
                    recorded_at TEXT NOT NULL,
                    detail TEXT,
                    PRIMARY KEY (operation_id, revision),
                    FOREIGN KEY (operation_id) REFERENCES pipeline_runs(operation_id)
                );
                """, tx);
            if (schema == 0)
            {
                Execute("PRAGMA user_version=1;", tx);
            }

            tx.Commit();
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>Atomically accepts one immutable validated manifest; identical request retries return the original run.</summary>
    /// <remarks>A reused clientRequestId with any different serialized manifest is refused; it never creates another run.</remarks>
    public PipelineRunSnapshot Accept(string clientRequestId, PipelineDocument document)
    {
        Identifier(clientRequestId, nameof(clientRequestId));
        ArgumentNullException.ThrowIfNull(document);
        // Serialize *before* planning so later mutation of the caller's DTO cannot change the admitted bytes.
        string manifestJson = PipelineManifest.ToJson(document);
        int bytes = Encoding.UTF8.GetByteCount(manifestJson);
        if (bytes > MaxManifestBytes)
        {
            throw new ArgumentException("Pipeline manifest exceeds 1 MiB.", nameof(document));
        }

        PipelineDocument frozen = PipelineManifest.ParseJson(manifestJson);
        PipelinePlan plan = PipelinePlanner.Plan(frozen);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson)));

        using SqliteTransaction tx = db.BeginTransaction();
        string? previousId = FindOperationId(clientRequestId, tx);
        if (previousId is not null)
        {
            PipelineRunSnapshot previous = Read(previousId, tx);
            if (previous.ManifestSha256 != hash || previous.ManifestJson != manifestJson)
            {
                throw new InvalidOperationException("clientRequestId already bound to another immutable pipeline manifest.");
            }

            tx.Commit();
            return previous;
        }

        string operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        using (SqliteCommand cmd = Command("""
            INSERT INTO pipeline_runs
              (operation_id,client_request_id,manifest_json,manifest_sha256,name,revision,created_at)
            VALUES ($op,$client,$json,$sha,$name,0,$created);
            """, tx))
        {
            Add(cmd, "$op", operationId);
            Add(cmd, "$client", clientRequestId);
            Add(cmd, "$json", manifestJson);
            Add(cmd, "$sha", hash);
            Add(cmd, "$name", plan.Name);
            Add(cmd, "$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            _ = cmd.ExecuteNonQuery();
        }

        foreach (PipelinePlanStage stage in plan.Stages)
        {
            using SqliteCommand cmd = Command("""
                INSERT INTO pipeline_stages (operation_id,stage_id,kind,status,revision)
                VALUES ($op,$stage,$kind,'pending',0);
                """, tx);
            Add(cmd, "$op", operationId);
            Add(cmd, "$stage", stage.Id);
            Add(cmd, "$kind", stage.Kind);
            _ = cmd.ExecuteNonQuery();
        }

        AppendEvent(operationId, 0, null, "accepted", hash, tx);
        PipelineRunSnapshot created = Read(operationId, tx);
        tx.Commit();
        return created;
    }

    /// <summary>Find the accepted operation by request identity, including after a lost response or restart.</summary>
    public PipelineRunSnapshot? Find(string clientRequestId)
    {
        Identifier(clientRequestId, nameof(clientRequestId));
        using SqliteTransaction tx = db.BeginTransaction();
        string? id = FindOperationId(clientRequestId, tx);
        PipelineRunSnapshot? snapshot = id is null ? null : Read(id, tx);
        tx.Commit();
        return snapshot;
    }

    /// <summary>Read the immutable manifest, stage states and monotonic revision as one consistent snapshot.</summary>
    public PipelineRunSnapshot Get(string operationId)
    {
        Identifier(operationId, nameof(operationId));
        using SqliteTransaction tx = db.BeginTransaction();
        PipelineRunSnapshot result = Read(operationId, tx);
        tx.Commit();
        return result;
    }

    /// <summary>Durably reserve a workflow child request *before* any external dispatch is attempted.</summary>
    /// <remarks>After a crash this intent is uncertain; lookup by ChildRequestId, never dispatch again blindly.</remarks>
    public PipelineRunSnapshot ReserveWorkflow(string operationId, string stageId, long expectedRevision) =>
        Transition(operationId, stageId, expectedRevision, "workflow", PipelineStageStates.Pending,
            PipelineStageStates.DispatchIntent, "reserved", childRequestId: $"pipeline:{operationId}:{stageId}");

    /// <summary>Bind an already accepted child operation by ID; only a reserved workflow stage may bind.</summary>
    public PipelineRunSnapshot BindChild(string operationId, string stageId, long expectedRevision,
        string childOperationId)
    {
        Identifier(childOperationId, nameof(childOperationId));
        return Transition(operationId, stageId, expectedRevision, "workflow", PipelineStageStates.DispatchIntent,
            PipelineStageStates.WaitingExternal, "child_bound", childOperationId: childOperationId);
    }

    /// <summary>Record the pinned identity of a pre-existing GitHub run to watch; no dispatch or lookup is performed.</summary>
    public PipelineRunSnapshot WatchExistingGitHub(string operationId, string stageId, long expectedRevision)
    {
        Identifier(operationId, nameof(operationId));
        Identifier(stageId, nameof(stageId));
        // The binding is derived only from the exact admitted snapshot, never mutable caller options.
        PipelineRunSnapshot snapshot = Get(operationId);
        PipelineDocument frozen = PipelineManifest.ParseJson(snapshot.ManifestJson);
        PipelineGitHubActionsStage github = frozen.Stages.Single(s => s.Id == stageId).GitHubActions
            ?? throw new InvalidOperationException("Stage is not a GitHub Actions attachment.");
        string binding = $"github:{github.Repository}:{github.Workflow}:{github.RunId}:{github.RunAttempt}:{github.ExpectedHeadSha}";
        return Transition(operationId, stageId, expectedRevision, "github.actions", PipelineStageStates.Pending,
            PipelineStageStates.WaitingExternal, "github_watch_recorded", externalReference: binding);
    }

    /// <summary>Record a trusted adapter's verified terminal outcome, retaining a nonempty evidence reference.</summary>
    /// <remarks>This method does not independently verify the evidence reference. The future Host adapter must do so.</remarks>
    public PipelineRunSnapshot RecordOutcome(string operationId, string stageId, long expectedRevision,
        bool succeeded, string evidenceReference)
    {
        return string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Length > 2048
            ? throw new ArgumentException("Nonempty bounded authoritative evidence reference required.",
                nameof(evidenceReference))
            : Transition(operationId, stageId, expectedRevision, null, PipelineStageStates.WaitingExternal,
            succeeded ? PipelineStageStates.Succeeded : PipelineStageStates.Failed,
            succeeded ? "succeeded" : "failed", evidenceReference: evidenceReference);
    }

    /// <summary>Under exclusive Host ownership, quarantine an unacknowledged dispatch without replaying effects.</summary>
    /// <remarks>Does not infer quiescence. Reconciliation is explicit, and the resulting state is not retryable.</remarks>
    public PipelineRunSnapshot QuarantineInterruptedDispatch(string operationId, string stageId,
        long expectedRevision, bool confirmedOwnerQuiescent)
    {
        return !confirmedOwnerQuiescent
            ? throw new InvalidOperationException("Exclusive prior-owner quiescence must be confirmed.")
            : Transition(operationId, stageId, expectedRevision, "workflow", PipelineStageStates.DispatchIntent,
            PipelineStageStates.OutcomeUnknown, "dispatch_outcome_unknown");
    }

    /// <summary>Read the append-only operation journal after the given revision (exclusive).</summary>
    public IReadOnlyList<PipelineLedgerEvent> Events(string operationId, long afterRevision = -1)
    {
        Identifier(operationId, nameof(operationId));
        using SqliteTransaction tx = db.BeginTransaction();
        _ = Read(operationId, tx); // Require an existing, internally consistent operation.
        using SqliteCommand cmd = Command("""
            SELECT revision,stage_id,event_kind,recorded_at,detail FROM pipeline_events
            WHERE operation_id=$op AND revision>$after ORDER BY revision;
            """, tx);
        Add(cmd, "$op", operationId);
        Add(cmd, "$after", afterRevision);
        List<PipelineLedgerEvent> events = [];
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                events.Add(new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        tx.Commit();
        return events.AsReadOnly();
    }

    private PipelineRunSnapshot Transition(string operationId, string stageId, long expectedRevision,
        string? kind, string requiredState, string nextState, string eventKind,
        string? childRequestId = null, string? childOperationId = null,
        string? externalReference = null, string? evidenceReference = null)
    {
        Identifier(operationId, nameof(operationId));
        Identifier(stageId, nameof(stageId));
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);

        using SqliteTransaction tx = db.BeginTransaction();
        PipelineRunSnapshot previous = Read(operationId, tx);
        if (previous.Revision != expectedRevision)
        {
            throw new InvalidOperationException("stale_pipeline_revision");
        }

        PipelineStageSnapshot stage = previous.Stages.SingleOrDefault(s => s.Id == stageId)
            ?? throw new ArgumentException($"Unknown pipeline stage '{stageId}'.", nameof(stageId));
        if (stage.Status != requiredState || (kind is not null && stage.Kind != kind))
        {
            throw new InvalidOperationException("invalid_pipeline_stage_transition");
        }

        if (requiredState == PipelineStageStates.Pending)
        {
            // An unresolved dispatch anywhere in this operation halts *all* further admissions.
            if (previous.Stages.Any(s => s.Status == PipelineStageStates.OutcomeUnknown))
            {
                throw new InvalidOperationException("pipeline_needs_reconciliation");
            }

            PipelineDocument doc = PipelineManifest.ParseJson(previous.ManifestJson);
            PipelineStage plannedStage = doc.Stages.Single(s => s.Id == stageId);
            foreach (string parentId in plannedStage.DependsOn)
            {
                PipelineStageSnapshot prerequisite = previous.Stages.Single(s => s.Id == parentId);
                if (prerequisite.Status != PipelineStageStates.Succeeded)
                {
                    throw new InvalidOperationException("pipeline_prerequisite_not_succeeded");
                }
            }
        }

        if (requiredState == PipelineStageStates.WaitingExternal &&
            stage.ChildOperationId is null && stage.ExternalReference is null)
        {
            throw new InvalidDataException("Cannot finish a stage without a persisted external binding.");
        }

        using (SqliteCommand cmd = Command("""
            UPDATE pipeline_stages SET status=$status, revision=revision+1,
                child_request_id=COALESCE($request,child_request_id),
                child_operation_id=COALESCE($child,child_operation_id),
                external_reference=COALESCE($external,external_reference),
                evidence_reference=COALESCE($evidence,evidence_reference)
            WHERE operation_id=$op AND stage_id=$stage AND status=$previous AND revision=$stageRevision;
            """, tx))
        {
            Add(cmd, "$status", nextState);
            Add(cmd, "$request", childRequestId);
            Add(cmd, "$child", childOperationId);
            Add(cmd, "$external", externalReference);
            Add(cmd, "$evidence", evidenceReference);
            Add(cmd, "$op", operationId);
            Add(cmd, "$stage", stageId);
            Add(cmd, "$previous", requiredState);
            Add(cmd, "$stageRevision", stage.Revision);
            if (cmd.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("stale_pipeline_stage_revision");
            }
        }

        using (SqliteCommand cmd = Command("""
            UPDATE pipeline_runs SET revision=revision+1
            WHERE operation_id=$op AND revision=$revision;
            """, tx))
        {
            Add(cmd, "$op", operationId);
            Add(cmd, "$revision", expectedRevision);
            if (cmd.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("stale_pipeline_revision");
            }
        }

        AppendEvent(operationId, expectedRevision + 1, stageId, eventKind,
            evidenceReference ?? childOperationId ?? externalReference ?? childRequestId, tx);
        PipelineRunSnapshot result = Read(operationId, tx);
        tx.Commit();
        return result;
    }

    private PipelineRunSnapshot Read(string operationId, SqliteTransaction tx)
    {
        string requestId;
        string manifestJson;
        string digest;
        string name;
        long revision;
        using (SqliteCommand cmd = Command("""
            SELECT client_request_id,manifest_json,manifest_sha256,name,revision
            FROM pipeline_runs WHERE operation_id=$op;
            """, tx))
        {
            Add(cmd, "$op", operationId);
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                throw new KeyNotFoundException($"Unknown pipeline operation '{operationId}'.");
            }

            requestId = reader.GetString(0);
            manifestJson = reader.GetString(1);
            digest = reader.GetString(2);
            name = reader.GetString(3);
            revision = reader.GetInt64(4);
        }

        if (!string.Equals(digest,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson))),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Pipeline manifest snapshot SHA-256 mismatch.");
        }

        PipelineDocument frozen = PipelineManifest.ParseJson(manifestJson);
        PipelinePlan plan = PipelinePlanner.Plan(frozen);
        if (plan.Name != name)
        {
            throw new InvalidDataException("Pipeline manifest name differs from persisted name.");
        }

        Dictionary<string, PipelineStageSnapshot> stages = new(StringComparer.Ordinal);
        using (SqliteCommand cmd = Command("""
            SELECT stage_id,kind,status,revision,child_request_id,child_operation_id,
                   external_reference,evidence_reference
            FROM pipeline_stages WHERE operation_id=$op;
            """, tx))
        {
            Add(cmd, "$op", operationId);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                PipelineStageSnapshot stage = new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt64(3), Optional(reader, 4), Optional(reader, 5), Optional(reader, 6),
                    Optional(reader, 7));
                if (!stages.TryAdd(stage.Id, stage))
                {
                    throw new InvalidDataException("Duplicate pipeline stage in persisted state.");
                }
            }
        }

        if (stages.Count != plan.Stages.Count)
        {
            throw new InvalidDataException("Pipeline stage count disagrees with immutable manifest.");
        }

        List<PipelineStageSnapshot> ordered = new(plan.Stages.Count);
        foreach (PipelinePlanStage node in plan.Stages)
        {
            if (!stages.TryGetValue(node.Id, out PipelineStageSnapshot? stage) || stage.Kind != node.Kind)
            {
                throw new InvalidDataException($"Missing or mismatched persisted pipeline stage '{node.Id}'.");
            }

            ordered.Add(stage);
        }

        string status = ordered.Any(s => s.Status == PipelineStageStates.OutcomeUnknown) ? "needs_attention"
            : ordered.Any(s => s.Status == PipelineStageStates.DispatchIntent) ? "dispatch_intent"
            : ordered.Any(s => s.Status == PipelineStageStates.WaitingExternal) ? "waiting_external"
            : ordered.Any(s => s.Status == PipelineStageStates.Failed) ? "failed"
            : ordered.All(s => s.Status == PipelineStageStates.Succeeded) ? "succeeded"
            : "pending";
        return new(operationId, requestId, name, digest, manifestJson, revision, status,
            ordered.AsReadOnly());
    }

    private string? FindOperationId(string clientRequestId, SqliteTransaction tx)
    {
        using SqliteCommand cmd = Command("""
            SELECT operation_id FROM pipeline_runs WHERE client_request_id=$client;
            """, tx);
        Add(cmd, "$client", clientRequestId);
        return cmd.ExecuteScalar() as string;
    }

    private void AppendEvent(string operationId, long revision, string? stageId, string kind,
        string? detail, SqliteTransaction tx)
    {
        using SqliteCommand cmd = Command("""
            INSERT INTO pipeline_events (operation_id,revision,stage_id,event_kind,recorded_at,detail)
            VALUES ($op,$rev,$stage,$kind,$time,$detail);
            """, tx);
        Add(cmd, "$op", operationId);
        Add(cmd, "$rev", revision);
        Add(cmd, "$stage", stageId);
        Add(cmd, "$kind", kind);
        Add(cmd, "$time", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Add(cmd, "$detail", detail);
        _ = cmd.ExecuteNonQuery();
    }

    [SuppressMessage("Security", "CA2100",
        Justification = "Every SQL statement is a constant literal; values are bound as SQLite parameters.")]
    private SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private void Execute(string sql, SqliteTransaction? tx = null)
    {
        using SqliteCommand cmd = Command(sql, tx);
        _ = cmd.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? Optional(SqliteDataReader reader, int position) =>
        reader.IsDBNull(position) ? null : reader.GetString(position);

    private static void Identifier(string value, string parameter)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 ||
            !value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            throw new ArgumentException($"Invalid pipeline identifier '{value}'.", parameter);
        }
    }

    public void Dispose() => db.Dispose();
}
