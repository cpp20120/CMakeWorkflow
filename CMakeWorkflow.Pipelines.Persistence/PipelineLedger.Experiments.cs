using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Pipelines.Persistence;

public sealed partial class PipelineLedger
{
    private const string ExperimentApprovalSchema = """
        CREATE TABLE IF NOT EXISTS experiment_controller_approvals (
            plan_id TEXT PRIMARY KEY CHECK(length(plan_id)=64),
            approved INTEGER NOT NULL CHECK(approved IN (0,1)),
            dependency_approval TEXT CHECK(length(dependency_approval)=64),
            dependency_receipt TEXT CHECK(length(dependency_receipt)=64),
            CHECK(dependency_receipt IS NULL OR dependency_approval IS NOT NULL)
        ) STRICT, WITHOUT ROWID;
        """;

    internal bool IsExperimentApproved(string planId)
    {
        Execute(ExperimentApprovalSchema);
        using SqliteCommand read = Command("SELECT approved FROM experiment_controller_approvals WHERE plan_id=$plan;");
        _ = read.Parameters.AddWithValue("$plan", planId);
        return read.ExecuteScalar() is 1L;
    }

    internal void RecordExperimentApproval(string planId, bool approve, string? dependency = null, string? receipt = null)
    {
        using SqliteTransaction tx = db.BeginTransaction();
        Execute(ExperimentApprovalSchema, tx);
        using SqliteCommand row = Command("""
            INSERT INTO experiment_controller_approvals(plan_id,approved,dependency_approval,dependency_receipt)
            VALUES($plan,$approved,$dependency,$receipt)
            ON CONFLICT(plan_id) DO UPDATE SET
                approved=max(approved,excluded.approved),
                dependency_approval=coalesce(dependency_approval,excluded.dependency_approval),
                dependency_receipt=coalesce(dependency_receipt,excluded.dependency_receipt)
            WHERE (dependency_approval IS NULL OR excluded.dependency_approval IS NULL
                OR dependency_approval=excluded.dependency_approval)
              AND (dependency_receipt IS NULL OR excluded.dependency_receipt IS NULL
                OR dependency_receipt=excluded.dependency_receipt);
            """, tx);
        _ = row.Parameters.AddWithValue("$plan", planId);
        _ = row.Parameters.AddWithValue("$approved", approve);
        _ = row.Parameters.AddWithValue("$dependency", dependency is { } digest ? digest : DBNull.Value);
        _ = row.Parameters.AddWithValue("$receipt", receipt is { } id ? id : DBNull.Value);
        if (row.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("Conflicting native experiment approval or dependency receipt.");
        }

        tx.Commit();
    }
}
