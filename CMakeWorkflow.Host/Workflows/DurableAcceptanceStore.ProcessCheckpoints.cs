using System.Text.Json;
using CMakeWorkflow.CMake.Recovery;
using CMakeWorkflow.Core.Processes;
using CMakeWorkflow.Host.Execution;
using CMakeWorkflow.Host.Sessions;
using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Host.Workflows;

public sealed partial class DurableAcceptanceStore
{
    private const string NativeProcessCheckpointSchema = """

        CREATE TABLE process_native_checkpoints (
            operation_id TEXT PRIMARY KEY REFERENCES accepted_workflows(operation_id),
            generation INTEGER NOT NULL CHECK(generation>0),
            event_cursor INTEGER NOT NULL CHECK(event_cursor>=0),
            sha256 TEXT NOT NULL CHECK(length(sha256)=64),
            frame BLOB NOT NULL CHECK(length(frame) BETWEEN 16 AND 16777224)
        ) STRICT, WITHOUT ROWID;
        CREATE TABLE process_terminal_evidence (
            operation_id TEXT PRIMARY KEY REFERENCES operation_finalizations(operation_id),
            sha256 TEXT NOT NULL CHECK(length(sha256)=64),
            frame BLOB NOT NULL CHECK(length(frame) BETWEEN 16 AND 16777224)
        ) STRICT, WITHOUT ROWID;
        CREATE TRIGGER process_terminal_no_update BEFORE UPDATE ON process_terminal_evidence
        BEGIN SELECT raise(ABORT,'Process terminal evidence is immutable'); END;
        CREATE TRIGGER process_terminal_no_delete BEFORE DELETE ON process_terminal_evidence
        BEGIN SELECT raise(ABORT,'Process terminal evidence is immutable'); END;
        CREATE TABLE process_launch_intents (
            operation_id TEXT NOT NULL REFERENCES process_native_checkpoints(operation_id),
            node_id TEXT NOT NULL,
            attempt INTEGER NOT NULL CHECK(attempt>=0),
            checkpoint_sha256 TEXT NOT NULL CHECK(length(checkpoint_sha256)=64),
            utc_ticks INTEGER NOT NULL,
            PRIMARY KEY(operation_id,node_id,attempt)
        ) STRICT, WITHOUT ROWID;
        CREATE TABLE process_lifecycle (
            operation_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            attempt INTEGER NOT NULL,
            stage INTEGER NOT NULL CHECK(stage BETWEEN 1 AND 6),
            sequence INTEGER NOT NULL CHECK(sequence>0),
            utc_ticks INTEGER NOT NULL,
            pid INTEGER,
            start_utc_ticks INTEGER,
            exit_code INTEGER,
            CHECK((stage=2 AND pid IS NOT NULL AND pid>0 AND start_utc_ticks IS NOT NULL AND start_utc_ticks>0)
                OR (stage<>2 AND pid IS NULL AND start_utc_ticks IS NULL)),
            PRIMARY KEY(operation_id,node_id,attempt,stage),
            UNIQUE(operation_id,node_id,attempt,sequence),
            FOREIGN KEY(operation_id,node_id,attempt) REFERENCES process_launch_intents(operation_id,node_id,attempt)
        ) STRICT, WITHOUT ROWID;
        CREATE TRIGGER process_intent_no_update BEFORE UPDATE ON process_launch_intents
        BEGIN SELECT raise(ABORT,'Process launch intent is immutable'); END;
        CREATE TRIGGER process_intent_no_delete BEFORE DELETE ON process_launch_intents
        BEGIN SELECT raise(ABORT,'Process launch intent is immutable'); END;
        CREATE TRIGGER process_lifecycle_no_update BEFORE UPDATE ON process_lifecycle
        BEGIN SELECT raise(ABORT,'Process lifecycle is append-only'); END;
        CREATE TRIGGER process_lifecycle_no_delete BEFORE DELETE ON process_lifecycle
        BEGIN SELECT raise(ABORT,'Process lifecycle is append-only'); END;
        """;

    private const string StableProcessIdentitySchema = """

        CREATE TABLE process_birth_tokens (
            operation_id TEXT NOT NULL,
            node_id TEXT NOT NULL,
            attempt INTEGER NOT NULL,
            stage INTEGER NOT NULL DEFAULT 2 CHECK(stage=2),
            token TEXT NOT NULL CHECK(length(token) BETWEEN 1 AND 64),
            PRIMARY KEY(operation_id,node_id,attempt),
            FOREIGN KEY(operation_id,node_id,attempt,stage) REFERENCES process_lifecycle(operation_id,node_id,attempt,stage)
        ) STRICT, WITHOUT ROWID;
        CREATE TRIGGER process_birth_token_no_update BEFORE UPDATE ON process_birth_tokens
        BEGIN SELECT raise(ABORT,'Process birth evidence is immutable'); END;
        CREATE TRIGGER process_birth_token_no_delete BEFORE DELETE ON process_birth_tokens
        BEGIN SELECT raise(ABORT,'Process birth evidence is immutable'); END;
        """;

    private bool IsNativeProcess(string operationId) => UsesNativeProcessCheckpoints && GetDomainKind(operationId) == "process";

    internal ProcessCheckpointEvidenceCodec ProcessEvidenceCodec => UsesNativeProcessCheckpoints
        ? ProcessCheckpointEvidenceCodec.Native : ProcessCheckpointEvidenceCodec.Instance;

    internal byte[] SaveProcessCheckpoint(string operationId, ProcessOperationReport report, string? expected = null,
        bool validateResume = false, IReadOnlyList<WorkflowEvent>? events = null)
    {
        using SqliteTransaction tx = connection.BeginTransaction();
        (ProcessCheckpointBinding binding, string manifestSha) = ProcessBinding(operationId, tx);
        if (!report.SnapshotSha256.Equals(manifestSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Process checkpoint differs from the accepted snapshot.");
        }

        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        sql.CommandText = """
            SELECT coalesce((SELECT generation FROM process_native_checkpoints WHERE operation_id=$operation),0),
                coalesce((SELECT max(sequence) FROM workflow_events WHERE operation_id=$operation),0),
                (SELECT sha256 FROM process_native_checkpoints WHERE operation_id=$operation);
            """;
        long generation;
        long cursor;
        using (SqliteDataReader row = sql.ExecuteReader())
        {
            _ = row.Read();
            generation = checked(row.GetInt64(0) + 1);
            cursor = row.GetInt64(1);
            if (expected is not null && (row.IsDBNull(2) || row.GetString(2) != expected))
            {
                throw new InvalidOperationException("process_checkpoint_conflict");
            }
        }

        foreach (WorkflowEvent item in events ?? [])
        {
            if (item.Sequence != checked(++cursor))
            {
                throw new InvalidDataException("Process event cursor is discontinuous.");
            }

            AppendWorkflowEvent(operationId, item, tx);
        }

        byte[] frame = NativeProcessCheckpointCodec.Encode(new(binding, generation, cursor, report));
        if (validateResume)
        {
            _ = ProcessEvidenceCodec.ForResume(frame);
        }

        sql.CommandText = """
            INSERT INTO process_native_checkpoints(operation_id,generation,event_cursor,sha256,frame)
            VALUES($operation,$generation,$cursor,$hash,$frame)
            ON CONFLICT(operation_id) DO UPDATE SET generation=excluded.generation,event_cursor=excluded.event_cursor,
                sha256=excluded.sha256,frame=excluded.frame;
            """;
        _ = sql.Parameters.AddWithValue("$generation", generation);
        _ = sql.Parameters.AddWithValue("$cursor", cursor);
        _ = sql.Parameters.AddWithValue("$hash", Hex(frame));
        _ = sql.Parameters.AddWithValue("$frame", frame);
        _ = sql.ExecuteNonQuery();
        tx.Commit();
        return frame;
    }

    internal async Task<byte[]> ReadProcessEvidenceAsync(WorkspaceReadPaths paths, string operationId,
        string directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (UsesNativeProcessCheckpoints)
        {
            return ReadProcessCheckpoint(operationId, directory);
        }

        string file = paths.Require(Path.Combine(directory, "run.json"));
        paths.Read(file);
        return await File.ReadAllBytesAsync(file, ct);
    }

    internal byte[] ReadProcessCheckpoint(string operationId, string directory, bool terminal = false)
    {
        using SqliteTransaction tx = connection.BeginTransaction(deferred: true);
        byte[] bytes = ReadProcessCheckpoint(operationId, directory, tx, terminal);
        tx.Commit();
        return bytes;
    }

    private byte[] ReadProcessCheckpoint(string operationId, string directory, SqliteTransaction tx, bool terminal = false)
    {
        (ProcessCheckpointBinding binding, string manifestSha) = ProcessBinding(operationId, tx);
        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = """
            SELECT sha256,frame,NULL,NULL FROM process_terminal_evidence WHERE operation_id=$operation AND $terminal=1
            UNION ALL
            SELECT sha256,frame,generation,event_cursor FROM process_native_checkpoints WHERE operation_id=$operation AND $terminal=0;
            """;
        _ = sql.Parameters.AddWithValue("$terminal", terminal);
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        using SqliteDataReader row = sql.ExecuteReader();
        if (!row.Read())
        {
            throw new InvalidDataException("Missing native process evidence.");
        }

        byte[] bytes = (byte[])row.GetValue(1);
        if (Hex(bytes) != row.GetString(0))
        {
            throw new InvalidDataException("Native process evidence failed hash verification.");
        }

        NativeProcessCheckpoint checkpoint = NativeProcessCheckpointCodec.Decode(bytes);
        long committed = LastProcessEventCursor(operationId, tx);
        return checkpoint.EventCursor <= committed && checkpoint.Binding == binding && PathComparer.Equals(directory, binding.RunDirectory) &&
            checkpoint.Report.SnapshotSha256.Equals(manifestSha, StringComparison.OrdinalIgnoreCase) &&
            (terminal || (checkpoint.Generation == row.GetInt64(2) && checkpoint.EventCursor == row.GetInt64(3)))
            ? bytes
            : throw new InvalidDataException("Native process evidence differs from its operation binding.");
    }

    private long LastProcessEventCursor(string operationId, SqliteTransaction tx)
    {
        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = "SELECT coalesce(max(sequence),0) FROM workflow_events WHERE operation_id=$operation;";
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        return (long)sql.ExecuteScalar()!;
    }

    internal void VerifyProcessRetryCandidates(string operationId, ProcessOperationReport report)
    {
        if (!UsesNativeProcessCheckpoints)
        {
            return;
        }

        using SqliteTransaction tx = connection.BeginTransaction(deferred: true);
        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = """
            SELECT intent.node_id, lifecycle.exit_code FROM process_launch_intents intent
            LEFT JOIN process_lifecycle lifecycle
                ON lifecycle.operation_id=intent.operation_id AND lifecycle.node_id=intent.node_id
                    AND lifecycle.attempt=intent.attempt AND lifecycle.stage=3
            WHERE intent.operation_id=$operation;
            """;
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        HashSet<string> eligible = new(report.Nodes.Where(ProcessCheckpointEvidenceCodec.Eligible).Select(node => node.Id),
            StringComparer.Ordinal);
        HashSet<string> succeeded = new(report.Nodes.Where(node => node.Status == "succeeded").Select(node => node.Id),
            StringComparer.Ordinal);
        using SqliteDataReader rows = sql.ExecuteReader();
        while (rows.Read())
        {
            string node = rows.GetString(0);
            if (eligible.Contains(node))
            {
                throw new InvalidOperationException("Process launch intent prevents retry.");
            }

            if (!rows.IsDBNull(1) && rows.GetInt32(1) == 0)
            {
                _ = succeeded.Remove(node);
            }
        }

        if (succeeded.Count > 0)
        {
            throw new InvalidDataException("A completed process checkpoint lacks a matching successful exit observation.");
        }
    }

    private (ProcessCheckpointBinding Binding, string ManifestSha) ProcessBinding(string operationId, SqliteTransaction tx)
    {
        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = """
            SELECT a.request_digest,a.manifest_sha256,r.run_directory
            FROM accepted_workflows a JOIN operation_results_reservations r USING(operation_id)
            JOIN operation_domains d USING(operation_id)
            WHERE a.operation_id=$operation AND a.state='dispatch_authorized' AND d.domain_kind='process';
            """;
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        using SqliteDataReader row = sql.ExecuteReader();
        return row.Read()
            ? (new ProcessCheckpointBinding(WorkspaceId, operationId, row.GetString(0), row.GetString(2)), row.GetString(1))
            : throw new InvalidDataException("Missing process authorization or result reservation.");
    }

    private void BindProcessLaunchIntents(string operationId, string hash, ProcessOperationReport report, SqliteTransaction tx)
    {
        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = """
            INSERT INTO process_launch_intents(operation_id,node_id,attempt,checkpoint_sha256,utc_ticks)
            VALUES($operation,$node,$attempt,$hash,$time) ON CONFLICT(operation_id,node_id,attempt) DO NOTHING;
            """;
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        _ = sql.Parameters.AddWithValue("$attempt", report.ResumeCount);
        _ = sql.Parameters.AddWithValue("$hash", hash);
        _ = sql.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.UtcTicks);
        SqliteParameter nodeId = sql.Parameters.Add("$node", SqliteType.Text);
        foreach (ProcessNodeReport node in report.Nodes.Where(node => node.Status == "running"))
        {
            nodeId.Value = node.Id;
            _ = sql.ExecuteNonQuery();
        }
    }

    internal void AppendProcessLifecycle(string operationId, string node, int attempt,
        WorkflowProcessBackend.LifecycleObservation observation)
    {
        using SqliteTransaction tx = connection.BeginTransaction();
        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = """
            WITH prior AS (
                SELECT sequence,stage FROM process_lifecycle
                WHERE operation_id=$operation AND node_id=$node AND attempt=$attempt
            )
            INSERT INTO process_lifecycle(operation_id,node_id,attempt,stage,sequence,utc_ticks,pid,start_utc_ticks,exit_code)
            SELECT $operation,$node,$attempt,$stage,coalesce((SELECT max(sequence) FROM prior),0)+1,
                $time,$pid,$start,$exit
            WHERE EXISTS (
                SELECT 1 FROM process_launch_intents
                WHERE operation_id=$operation AND node_id=$node AND attempt=$attempt
            ) AND NOT EXISTS(SELECT 1 FROM operation_finalizations WHERE operation_id=$operation)
              AND ($stage IN (1,4,6) OR EXISTS(SELECT 1 FROM prior WHERE stage=1))
              AND ($stage<>5 OR EXISTS(SELECT 1 FROM prior WHERE stage=4))
              AND NOT EXISTS(SELECT 1 FROM prior WHERE stage IN (5,6));
            """;
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        _ = sql.Parameters.AddWithValue("$node", node);
        _ = sql.Parameters.AddWithValue("$attempt", attempt);
        _ = sql.Parameters.AddWithValue("$stage", (int)observation.Stage);
        _ = sql.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.UtcTicks);
        _ = sql.Parameters.AddWithValue("$pid", observation.ProcessId is { } pid ? pid : DBNull.Value);
        _ = sql.Parameters.AddWithValue("$start", observation.StartUtcTicks is { } start ? start : DBNull.Value);
        _ = sql.Parameters.AddWithValue("$exit", observation.ExitCode is { } exit ? exit : DBNull.Value);
        if (sql.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Process lifecycle observation lacks an authorized attempt or predecessor.");
        }

        if (UsesStableProcessIdentity && observation.Stage == WorkflowProcessBackend.LifecycleStage.IdentityObserved)
        {
            using SqliteCommand identity = connection.CreateCommand();
            identity.Transaction = tx;
            identity.CommandText = """
                INSERT INTO process_birth_tokens(operation_id,node_id,attempt,token) VALUES($operation,$node,$attempt,$token);
                """;
            _ = identity.Parameters.AddWithValue("$operation", operationId);
            _ = identity.Parameters.AddWithValue("$node", node);
            _ = identity.Parameters.AddWithValue("$attempt", attempt);
            _ = identity.Parameters.AddWithValue("$token", observation.BirthToken ??
                throw new InvalidDataException("Process identity observation does not have a birth token."));
            _ = identity.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private void InsertProcessTerminal(string operationId, byte[] frame, SqliteTransaction tx)
    {
        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = "INSERT INTO process_terminal_evidence(operation_id,sha256,frame) VALUES($operation,$sha,$frame);";
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        _ = sql.Parameters.AddWithValue("$sha", Hex(frame));
        _ = sql.Parameters.AddWithValue("$frame", frame);
        _ = sql.ExecuteNonQuery();
    }

    internal static byte[] ProjectProcessReport(ProcessOperationReport report) =>
        JsonSerializer.SerializeToUtf8Bytes(report, ProcessWorkflowJson.Default.ProcessOperationReport);
}
