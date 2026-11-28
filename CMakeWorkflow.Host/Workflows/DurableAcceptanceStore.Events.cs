using CMakeWorkflow.CMake.Artifacts;
using CMakeWorkflow.CMake.Recovery;
using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Host.Workflows;

public sealed partial class DurableAcceptanceStore
{
    private const string NativeWorkflowEventSchema = """

        CREATE TABLE workflow_events (
            operation_id TEXT NOT NULL REFERENCES workflow_binary_authorizations(operation_id),
            sequence INTEGER NOT NULL CHECK(sequence>0),
            utc_ticks INTEGER NOT NULL CHECK(utc_ticks BETWEEN 0 AND 3155378975999999999),
            type TEXT NOT NULL CHECK(type IN ('workflow_started','workflow_resumed','workflow_finished','node_started','node_finished')),
            run_directory TEXT NOT NULL,
            node_id TEXT,
            node_kind TEXT,
            status TEXT,
            exit_code INTEGER,
            error TEXT,
            artifact_count INTEGER NOT NULL CHECK(artifact_count>=0),
            PRIMARY KEY(operation_id,sequence)
        ) STRICT, WITHOUT ROWID;
        CREATE TABLE workflow_event_artifacts (
            operation_id TEXT NOT NULL,
            sequence INTEGER NOT NULL,
            position INTEGER NOT NULL CHECK(position>=0),
            type TEXT NOT NULL,
            path TEXT NOT NULL,
            sha256 TEXT,
            PRIMARY KEY(operation_id,sequence,position),
            FOREIGN KEY(operation_id,sequence) REFERENCES workflow_events(operation_id,sequence) DEFERRABLE INITIALLY DEFERRED
        ) STRICT, WITHOUT ROWID;
        CREATE TRIGGER workflow_events_next BEFORE INSERT ON workflow_events
        WHEN new.sequence != (SELECT coalesce(max(sequence),0)+1 FROM workflow_events WHERE operation_id=new.operation_id)
        BEGIN SELECT raise(ABORT,'Workflow event sequence must advance by one'); END;
        CREATE TRIGGER workflow_events_artifact_count BEFORE INSERT ON workflow_events
        WHEN new.artifact_count != (SELECT count(*) FROM workflow_event_artifacts
            WHERE operation_id=new.operation_id AND sequence=new.sequence)
        BEGIN SELECT raise(ABORT,'Workflow event artifact count differs'); END;
        CREATE TRIGGER workflow_events_no_update BEFORE UPDATE ON workflow_events
        BEGIN SELECT raise(ABORT,'Workflow events are append-only'); END;
        CREATE TRIGGER workflow_events_no_delete BEFORE DELETE ON workflow_events
        BEGIN SELECT raise(ABORT,'Workflow events are append-only'); END;
        CREATE TRIGGER workflow_event_artifacts_no_late_insert BEFORE INSERT ON workflow_event_artifacts
        WHEN EXISTS (SELECT 1 FROM workflow_events WHERE operation_id=new.operation_id AND sequence=new.sequence)
        BEGIN SELECT raise(ABORT,'Workflow event artifacts are sealed'); END;
        CREATE TRIGGER workflow_event_artifacts_no_update BEFORE UPDATE ON workflow_event_artifacts
        BEGIN SELECT raise(ABORT,'Workflow event artifacts are append-only'); END;
        CREATE TRIGGER workflow_event_artifacts_no_delete BEFORE DELETE ON workflow_event_artifacts
        BEGIN SELECT raise(ABORT,'Workflow event artifacts are append-only'); END;
        """;

    // Called by the single runner holding the operation lease.
    internal void AppendWorkflowEvent(string operationId, WorkflowEvent item)
    {
        if (item.SchemaVersion != 1)
        {
            throw new InvalidDataException("Unsupported workflow event schema.");
        }

        using SqliteTransaction tx = connection.BeginTransaction();
        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        if (item.Artifacts.Count > 0)
        {
            sql.CommandText = """
                INSERT INTO workflow_event_artifacts(operation_id,sequence,position,type,path,sha256)
                VALUES($operation,$sequence,$position,$type,$path,$sha);
                """;
            _ = sql.Parameters.AddWithValue("$operation", operationId);
            _ = sql.Parameters.AddWithValue("$sequence", item.Sequence);
            SqliteParameter position = sql.Parameters.Add("$position", SqliteType.Integer);
            SqliteParameter type = sql.Parameters.Add("$type", SqliteType.Text);
            SqliteParameter path = sql.Parameters.Add("$path", SqliteType.Text);
            SqliteParameter sha = sql.Parameters.Add("$sha", SqliteType.Text);
            for (int i = 0; i < item.Artifacts.Count; i++)
            {
                WorkflowArtifactView artifact = item.Artifacts[i];
                position.Value = i;
                type.Value = artifact.Type;
                path.Value = artifact.Path;
                sha.Value = (object?)artifact.Sha256 ?? DBNull.Value;
                _ = sql.ExecuteNonQuery();
            }
        }

        sql.Parameters.Clear();
        sql.CommandText = """
            INSERT INTO workflow_events(operation_id,sequence,utc_ticks,type,run_directory,node_id,node_kind,status,exit_code,error,artifact_count)
            SELECT $operation,$sequence,$timestamp,$type,$run,$node,$kind,$status,$exit,$error,$count
            WHERE EXISTS (
                SELECT 1 FROM workflow_binary_authorizations authorization
                JOIN operation_results_reservations reservation USING(operation_id)
                WHERE authorization.operation_id=$operation AND reservation.run_directory=$run
            );
            """;
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        _ = sql.Parameters.AddWithValue("$sequence", item.Sequence);
        _ = sql.Parameters.AddWithValue("$timestamp", item.Timestamp.UtcTicks);
        _ = sql.Parameters.AddWithValue("$type", item.Type);
        _ = sql.Parameters.AddWithValue("$run", item.RunDirectory);
        _ = sql.Parameters.AddWithValue("$node", (object?)item.NodeId ?? DBNull.Value);
        _ = sql.Parameters.AddWithValue("$kind", (object?)item.NodeKind ?? DBNull.Value);
        _ = sql.Parameters.AddWithValue("$status", (object?)item.Status ?? DBNull.Value);
        _ = sql.Parameters.AddWithValue("$exit", item.ExitCode is { } code ? code : DBNull.Value);
        _ = sql.Parameters.AddWithValue("$error", (object?)item.Error ?? DBNull.Value);
        _ = sql.Parameters.AddWithValue("$count", item.Artifacts.Count);
        if (sql.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Workflow event requires authorized results ownership.");
        }

        tx.Commit();
    }

    internal long LastWorkflowEventSequence(string operationId)
    {
        using SqliteCommand sql = connection.CreateCommand();
        sql.CommandText = "SELECT coalesce(max(sequence),0) FROM workflow_events WHERE operation_id=$operation;";
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        return (long)sql.ExecuteScalar()!;
    }

    internal async Task<OperationEventsPage> ReadWorkflowEventsAsync(OperationEventsRequest request, CancellationToken ct)
    {
        await using SqliteCommand sql = connection.CreateCommand();
        sql.CommandText = """
            WITH page AS (
                SELECT * FROM workflow_events WHERE operation_id=$operation AND sequence>$after
                ORDER BY sequence LIMIT $take
            )
            SELECT page.sequence,page.utc_ticks,page.type,page.run_directory,page.node_id,page.node_kind,page.status,
                page.exit_code,page.error,page.artifact_count,artifact.position,artifact.type,artifact.path,artifact.sha256
            FROM page LEFT JOIN workflow_event_artifacts artifact USING(operation_id,sequence)
            ORDER BY page.sequence,artifact.position;
            """;
        _ = sql.Parameters.AddWithValue("$operation", request.OperationId);
        _ = sql.Parameters.AddWithValue("$after", request.AfterSequence);
        _ = sql.Parameters.AddWithValue("$take", request.Limit + 1);
        await using SqliteDataReader reader = await sql.ExecuteReaderAsync(ct);
        List<WorkflowEvent> events = [];
        long last = request.AfterSequence;
        int expectedArtifacts = 0;
        while (await reader.ReadAsync(ct))
        {
            long sequence = reader.GetInt64(0);
            if (sequence != last)
            {
                RequireArtifactCount();
                if (events.Count == request.Limit)
                {
                    return new(request.OperationId, request.AfterSequence, last, true, events);
                }

                if (sequence != last + 1)
                {
                    throw new InvalidDataException("Workflow event sequence has a gap.");
                }

                last = sequence;
                expectedArtifacts = reader.GetInt32(9);
                events.Add(new(1, sequence, new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                    reader.GetString(2), reader.GetString(3), Text(4), Text(5), Text(6),
                    await reader.IsDBNullAsync(7, ct) ? null : reader.GetInt32(7), Text(8), []));
            }

            if (!await reader.IsDBNullAsync(10, ct))
            {
                List<WorkflowArtifactView> artifacts = events[^1].Artifacts;
                if (reader.GetInt32(10) != artifacts.Count)
                {
                    throw new InvalidDataException("Workflow event artifact sequence has a gap.");
                }

                artifacts.Add(new(reader.GetString(11), reader.GetString(12), Text(13)));
            }
        }

        RequireArtifactCount();
        return new(request.OperationId, request.AfterSequence, last, false, events);

        void RequireArtifactCount()
        {
            if (events.Count > 0 && events[^1].Artifacts.Count != expectedArtifacts)
            {
                throw new InvalidDataException("Workflow event artifacts are incomplete.");
            }
        }

        string? Text(int ordinal) => reader.GetValue(ordinal) as string;
    }
}
