using CMakeWorkflow.CMake.Reporting;
using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Host.Workflows;

internal sealed record BoundNativeCheckpoint(NativeExecutionCheckpoint State, string Digest);

public sealed partial class DurableAcceptanceStore
{
    private const string NativeWorkflowCheckpointSchema = """

        CREATE TABLE workflow_native_checkpoints (
            operation_id TEXT PRIMARY KEY REFERENCES workflow_binary_authorizations(operation_id),
            generation INTEGER NOT NULL CHECK(generation>0),
            event_cursor INTEGER NOT NULL CHECK(event_cursor>=0),
            sha256 TEXT NOT NULL CHECK(length(sha256)=64),
            frame BLOB NOT NULL CHECK(length(frame) BETWEEN 16 AND 16777224)
        ) STRICT, WITHOUT ROWID;
        """;

    private bool UsesNativeCheckpoint(string operationId) =>
        UsesNativeWorkflowCheckpoints && ReadBinaryInput(operationId) is not null;

    // A caller owns the execution lease. The row and its identity change together.
    internal void SaveNativeCheckpoint(string operationId, WorkflowReport report)
    {
        using SqliteTransaction tx = connection.BeginTransaction();
        WriteNativeCheckpoint(operationId, report, expected: null, tx);
        tx.Commit();
    }

    private void WriteNativeCheckpoint(string operationId, WorkflowReport report, string? expected, SqliteTransaction tx)
    {
        (NativeExecutionCheckpoint identity, string manifestDigest, string? source) = ReadCheckpointIdentity(operationId, tx);
        if (!string.Equals(report.ManifestHash, manifestDigest, StringComparison.OrdinalIgnoreCase) ||
            report.SourceSnapshotId != source)
        {
            throw new InvalidDataException("Native checkpoint differs from the admitted snapshot.");
        }

        long generation;
        using (SqliteCommand previous = connection.CreateCommand())
        {
            previous.Transaction = tx;
            previous.CommandText = "SELECT generation,sha256 FROM workflow_native_checkpoints WHERE operation_id=$operation;";
            _ = previous.Parameters.AddWithValue("$operation", operationId);
            using SqliteDataReader row = previous.ExecuteReader();
            bool exists = row.Read();
            if (expected is not null && (!exists || !expected.Equals(row.GetString(1), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("native_checkpoint_conflict");
            }

            generation = exists ? checked(row.GetInt64(0) + 1) : 1;
        }

        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = "SELECT coalesce(max(sequence),0) FROM workflow_events WHERE operation_id=$operation;";
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        long cursor = (long)sql.ExecuteScalar()!;
        byte[] bytes = NativeExecutionReportCodec.EncodeCheckpoint(identity with
        {
            Generation = generation,
            EventCursor = cursor,
            Report = report
        });
        sql.CommandText = """
            INSERT INTO workflow_native_checkpoints(operation_id,generation,event_cursor,sha256,frame)
            VALUES($operation,$generation,$cursor,$digest,$frame)
            ON CONFLICT(operation_id) DO UPDATE SET generation=excluded.generation,
                event_cursor=excluded.event_cursor,sha256=excluded.sha256,frame=excluded.frame;
            """;
        _ = sql.Parameters.AddWithValue("$generation", generation);
        _ = sql.Parameters.AddWithValue("$cursor", cursor);
        _ = sql.Parameters.AddWithValue("$digest", Hex(bytes));
        _ = sql.Parameters.AddWithValue("$frame", bytes);
        _ = sql.ExecuteNonQuery();
    }

    internal BoundNativeCheckpoint? FindNativeCheckpointByResults(string directory)
    {
        if (!UsesNativeWorkflowCheckpoints)
        {
            return null;
        }

        string? operation;
        using (SqliteCommand sql = connection.CreateCommand())
        {
            sql.CommandText = """
                SELECT reservation.operation_id FROM operation_results_reservations reservation
                JOIN workflow_native_checkpoints checkpoint ON checkpoint.operation_id=reservation.operation_id
                WHERE reservation.run_directory=$directory;
                """;
            _ = sql.Parameters.AddWithValue("$directory", directory);
            operation = (string?)sql.ExecuteScalar();
        }

        return operation is null ? null : ReadNativeCheckpoint(operation);
    }

    internal BoundNativeCheckpoint ReadNativeCheckpoint(string operationId)
    {
        using SqliteTransaction tx = connection.BeginTransaction(deferred: true);
        BoundNativeCheckpoint checkpoint = ReadNativeCheckpoint(operationId, tx);
        tx.Commit();
        return checkpoint;
    }

    private BoundNativeCheckpoint ReadNativeCheckpoint(string operationId, SqliteTransaction tx)
    {
        (NativeExecutionCheckpoint identity, string manifestDigest, string? source) = ReadCheckpointIdentity(operationId, tx);
        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = """
            SELECT generation,event_cursor,sha256,frame,
                (SELECT coalesce(max(sequence),0) FROM workflow_events WHERE operation_id=$operation)
            FROM workflow_native_checkpoints WHERE operation_id=$operation;
            """;
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        using SqliteDataReader row = sql.ExecuteReader();
        if (!row.Read())
        {
            throw new InvalidOperationException("native_checkpoint_missing");
        }

        byte[] bytes = (byte[])row.GetValue(3);
        string digest = row.GetString(2);
        if (Hex(bytes) != digest)
        {
            throw new InvalidDataException("Native checkpoint bytes failed hash verification.");
        }

        NativeExecutionCheckpoint state = NativeExecutionReportCodec.DecodeCheckpoint(bytes);
        return state with { Generation = 0, EventCursor = 0, Report = identity.Report } != identity ||
               state.Generation != row.GetInt64(0) || state.EventCursor != row.GetInt64(1) || state.EventCursor > row.GetInt64(4) ||
               !string.Equals(state.Report.ManifestHash, manifestDigest, StringComparison.OrdinalIgnoreCase) ||
               state.Report.SourceSnapshotId != source
            ? throw new InvalidDataException("Native checkpoint differs from its ledger binding.")
            : new(state, digest);
    }

    private (NativeExecutionCheckpoint Identity, string ManifestDigest, string? Source) ReadCheckpointIdentity(
        string operationId, SqliteTransaction tx)
    {
        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = """
            SELECT a.request_digest,a.manifest_sha256,input.document_id,auth.plan_document_id,auth.identity,
                reservation.run_directory,snapshot.snapshot_id
            FROM accepted_workflows a
            JOIN workflow_binary_inputs input USING(operation_id)
            JOIN workflow_binary_authorizations auth USING(operation_id)
            JOIN operation_results_reservations reservation USING(operation_id)
            LEFT JOIN operation_source_snapshots snapshot USING(operation_id)
            WHERE a.operation_id=$operation AND a.state='dispatch_authorized';
            """;
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        using SqliteDataReader row = sql.ExecuteReader();
        if (!row.Read())
        {
            throw new InvalidOperationException("Native checkpoint requires an authorized execution and reserved results.");
        }

        NativeExecutionCheckpoint identity = new(WorkspaceId, operationId, row.GetString(0), row.GetString(2),
            row.GetString(3), row.GetString(4), row.GetString(5), 0, 0, new());
        return (identity, row.GetString(1), row.IsDBNull(6) ? null : row.GetString(6));
    }
}
