using System.Globalization;
using CMakeWorkflow.Contracts.History;
using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Host.Workflows;

public sealed partial class DurableAcceptanceStore
{
    private void CreateHistoryIntentTable()
    {
        using SqliteTransaction tx = connection.BeginTransaction();
        using SqliteCommand schema = connection.CreateCommand();
        schema.Transaction = tx;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS workflow_history_intents (
              operation_id TEXT NOT NULL PRIMARY KEY REFERENCES accepted_workflows(operation_id),
              database_path TEXT NOT NULL,
              policy_mode TEXT NOT NULL CHECK(policy_mode IN ('Default', 'ExplicitDatabase')),
              state TEXT NOT NULL CHECK(state IN ('Pending', 'Indexing', 'Indexed', 'Failed')),
              updated_at TEXT NOT NULL,
              diagnostic_code TEXT,
              attempt_id TEXT
            );
            """;
        _ = schema.ExecuteNonQuery();

        // Existing interrupted attempts have no owner token. They can be claimed
        // normally once the previous Host releases the operation lease.
        using SqliteCommand columns = connection.CreateCommand();
        columns.Transaction = tx;
        columns.CommandText = "PRAGMA table_info(workflow_history_intents);";
        bool hasAttemptId = false;
        using (SqliteDataReader reader = columns.ExecuteReader())
        {
            while (reader.Read())
            {
                hasAttemptId |= reader.GetString(1) == "attempt_id";
            }
        }

        if (!hasAttemptId)
        {
            schema.CommandText = "ALTER TABLE workflow_history_intents ADD COLUMN attempt_id TEXT;";
            _ = schema.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private void InsertHistoryIntent(SqliteTransaction tx, string operationId, string database, HistoryPolicyMode mode)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO workflow_history_intents (operation_id,database_path,policy_mode,state,updated_at)
            VALUES ($id,$database,$mode,'Pending',$now);
            """;
        _ = command.Parameters.AddWithValue("$id", operationId);
        _ = command.Parameters.AddWithValue("$database", database);
        _ = command.Parameters.AddWithValue("$mode", mode.ToString());
        _ = command.Parameters.AddWithValue("$now", HistoryTimestamp());
        _ = command.ExecuteNonQuery();
    }

    // The finalization row is the activation record. A pending intent is not
    // runnable until the finalization transaction commits, including on resume.
    internal HistoryIndexStatus GetHistoryIndexStatus(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "N", out _))
        {
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        }

        if (!HasHistoryIntents())
        {
            return Get(operationId) is null ? throw new KeyNotFoundException("Unknown operation.")
                : new(operationId, HistoryIndexState.Disabled, null, null, null);
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.database_path,h.state,h.updated_at,h.diagnostic_code,f.operation_id
            FROM accepted_workflows a
            LEFT JOIN workflow_history_intents h ON h.operation_id=a.operation_id
            LEFT JOIN operation_finalizations f ON f.operation_id=a.operation_id
            WHERE a.operation_id=$id;
            """;
        _ = command.Parameters.AddWithValue("$id", operationId);
        using SqliteDataReader reader = command.ExecuteReader();
        return !reader.Read() ? throw new KeyNotFoundException("Unknown operation.")
            : reader.IsDBNull(0) ? new(operationId, HistoryIndexState.Disabled, null, null, null)
            : new(operationId, reader.IsDBNull(4) ? HistoryIndexState.AwaitingFinalization : reader.GetString(1) switch
            {
                "Pending" => HistoryIndexState.Pending,
                "Indexing" => HistoryIndexState.Indexing,
                "Indexed" => HistoryIndexState.Indexed,
                "Failed" => HistoryIndexState.Failed,
                _ => throw new InvalidDataException("Invalid history indexing state.")
            }, reader.GetString(0), DateTimeOffset.ParseExact(reader.GetString(2), "O", CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    internal IReadOnlyList<string> PendingHistoryOperations()
    {
        if (!HasHistoryIntents())
        {
            return [];
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.operation_id FROM workflow_history_intents h
            JOIN operation_finalizations f ON f.operation_id=h.operation_id
            WHERE h.state IN ('Pending','Indexing') ORDER BY h.operation_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> ids = [];
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private void ActivateHistoryOnFinalization(SqliteTransaction tx, string operationId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE workflow_history_intents SET updated_at=$now WHERE operation_id=$id;";
        _ = command.Parameters.AddWithValue("$id", operationId);
        _ = command.Parameters.AddWithValue("$now", HistoryTimestamp());
        _ = command.ExecuteNonQuery();
    }

    // The file lease serializes imports. The persisted attempt fences completion
    // even when a recovered claim transitions Indexing back to Indexing.
    internal string? ClaimHistoryIndex(string operationId, bool retry)
    {
        string attemptId = Guid.NewGuid().ToString("N");
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE workflow_history_intents
            SET state='Indexing',attempt_id=$attempt,updated_at=$now,diagnostic_code=NULL
            WHERE operation_id=$id
              AND (state IN ('Pending','Indexing') OR ($retry AND state='Failed'))
              AND EXISTS (SELECT 1 FROM operation_finalizations WHERE operation_id=$id);
            """;
        _ = command.Parameters.AddWithValue("$id", operationId);
        _ = command.Parameters.AddWithValue("$attempt", attemptId);
        _ = command.Parameters.AddWithValue("$now", HistoryTimestamp());
        _ = command.Parameters.AddWithValue("$retry", retry);
        return command.ExecuteNonQuery() == 1 ? attemptId : null;
    }

    internal bool CompleteHistoryIndex(string operationId, string attemptId, string? diagnosticCode = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE workflow_history_intents
            SET state=$next,attempt_id=NULL,updated_at=$now,diagnostic_code=$diagnostic
            WHERE operation_id=$id AND state='Indexing' AND attempt_id=$attempt
              AND EXISTS (SELECT 1 FROM operation_finalizations WHERE operation_id=$id);
            """;
        _ = command.Parameters.AddWithValue("$id", operationId);
        _ = command.Parameters.AddWithValue("$attempt", attemptId);
        _ = command.Parameters.AddWithValue("$next", diagnosticCode is null ? "Indexed" : "Failed");
        _ = command.Parameters.AddWithValue("$now", HistoryTimestamp());
        _ = command.Parameters.AddWithValue("$diagnostic", (object?)diagnosticCode ?? DBNull.Value);
        return command.ExecuteNonQuery() == 1;
    }

    private bool HasHistoryIntents()
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='workflow_history_intents';";
        return command.ExecuteScalar() is not null;
    }

    private static string HistoryTimestamp() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
