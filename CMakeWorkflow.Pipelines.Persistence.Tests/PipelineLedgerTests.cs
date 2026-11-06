using System.Globalization;
using CMakeWorkflow.Pipelines;
using CMakeWorkflow.Pipelines.Persistence;
using Microsoft.Data.Sqlite;

internal static class PipelineLedgerTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "cmwf-pipeline-ledger-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(root);
        string db = Path.Combine(root, "pipeline.sqlite");
        try
        {
            ExerciseAdmissionAndRecovery(db);
            ExerciseSuccessfulPipeline(db);
            ExerciseFailedPipeline(db);
            ExerciseIntegrityCheck(db);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine("Pipeline v1 durable ledger, CAS, restart and recovery checks passed.");
    }

    private static PipelineDocument Document() => new()
    {
        SchemaVersion = 1,
        Name = "release",
        Stages =
        [
            new PipelineStage
            {
                Id = "ci", Kind = "github.actions", DependsOn = ["debug"],
                GitHubActions = new PipelineGitHubActionsStage
                {
                    Mode = "attach", Repository = "cpp20120/CMakeWorkflow", Workflow = "ci.yml",
                    RunId = 123456, RunAttempt = 2,
                    ExpectedHeadSha = "0123456789abcdef0123456789abcdef01234567"
                }
            },
            new PipelineStage
            {
                Id = "debug", Kind = "workflow",
                Workflow = new PipelineWorkflowStage { Manifest = "workflows/debug.json" }
            }
        ]
    };

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException(description);
        }
    }

    private static void Refused(Action action, string description)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new System.Diagnostics.UnreachableException(description);
    }

    private static void ExerciseAdmissionAndRecovery(string path)
    {
        string operationId;
        string childRequestId;
        using (PipelineLedger ledger = new(path))
        {
            PipelineRunSnapshot accepted = ledger.Accept("recover-01", Document());
            operationId = accepted.OperationId;
            Check(accepted.Revision == 0 && accepted.Status == "pending" &&
                  accepted.Stages.Select(s => s.Id).SequenceEqual(["debug", "ci"]),
                "Pipeline admission and dependency order");
            Check(ledger.Find("missing-01") is null, "Unknown request must not be accepted");
            Check(ledger.Find("recover-01")?.OperationId == operationId, "Find by durable request ID");
            Check(ledger.Accept("recover-01", Document()).OperationId == operationId,
                "Repeat admission must return the original operation");
            using (PipelineLedger secondConnection = new(path))
            {
                Check(secondConnection.Accept("recover-01", Document()).OperationId == operationId,
                    "Cross-connection admission must preserve the existing operation ID");
            }

            PipelineDocument drifted = new()
            {
                SchemaVersion = 1,
                Name = "changed",
                Stages = Document().Stages
            };
            Refused(() => ledger.Accept("recover-01", drifted), "Manifest drift accepted under same request ID");
            Refused(() => ledger.WatchExistingGitHub(operationId, "ci", 0),
                "GitHub watch must await successful prerequisites");
            Refused(() => ledger.RecordOutcome(operationId, "debug", 0, true, "invented"),
                "A pending stage must not be reported successful");

            PipelineRunSnapshot reserved = ledger.ReserveWorkflow(operationId, "debug", 0);
            childRequestId = reserved.Stages[0].ChildRequestId!;
            Check(reserved.Revision == 1 && reserved.Stages[0].Status == PipelineStageStates.DispatchIntent &&
                  childRequestId.StartsWith("pipeline:", StringComparison.Ordinal),
                "Reservation must persist dispatch intent and deterministic child request ID");
            Refused(() => ledger.ReserveWorkflow(operationId, "debug", 0),
                "Stale retry must not reserve a second child");
            Refused(() => ledger.BindChild(operationId, "debug", 0, "child-01"),
                "Stale binding must not overwrite newer state");
            Refused(() => ledger.QuarantineInterruptedDispatch(operationId, "debug", 1, false),
                "Unknown outcome may not be reconciled without owner-quiescence assertion");
        }

        // A reopened Host can inspect the exact dispatch intent; it does not redispatch implicitly.
        using PipelineLedger reopened = new(path);
        PipelineRunSnapshot recovered = reopened.Find("recover-01")!;
        Check(recovered.OperationId == operationId && recovered.Revision == 1 &&
              recovered.Stages[0].ChildRequestId == childRequestId &&
              recovered.Stages[0].Status == PipelineStageStates.DispatchIntent,
            "Interrupted dispatch lost durable acceptance data");
        PipelineRunSnapshot uncertain = reopened.QuarantineInterruptedDispatch(operationId, "debug", 1, true);
        Check(uncertain.Revision == 2 && uncertain.Status == "needs_attention" &&
              uncertain.Stages[0].Status == PipelineStageStates.OutcomeUnknown &&
              uncertain.Stages[0].ChildRequestId == childRequestId,
            "Quarantine must preserve correlation without re-execution");
        Refused(() => reopened.ReserveWorkflow(operationId, "debug", 2),
            "Uncertain stage was made retryable");
        Refused(() => reopened.WatchExistingGitHub(operationId, "ci", 2),
            "Uncertain prerequisite allowed downstream admission");
        Check(reopened.Events(operationId).Select(e => e.Revision).SequenceEqual([0L, 1L, 2L]),
            "Accepted, reserved and uncertain events must be contiguous");
    }

    private static void ExerciseSuccessfulPipeline(string path)
    {
        using PipelineLedger first = new(path);
        using PipelineLedger concurrent = new(path);
        PipelineRunSnapshot accepted = first.Accept("success-01", Document());
        PipelineRunSnapshot reserved = first.ReserveWorkflow(accepted.OperationId, "debug", accepted.Revision);
        Refused(() => concurrent.ReserveWorkflow(accepted.OperationId, "debug", accepted.Revision),
            "Two independent connections both reserved from stale revision");
        PipelineRunSnapshot bound = concurrent.BindChild(accepted.OperationId, "debug", reserved.Revision, "child-op-1");
        Check(bound.Stages[0].ChildRequestId == reserved.Stages[0].ChildRequestId &&
              bound.Stages[0].ChildOperationId == "child-op-1" &&
              bound.Stages[0].Status == PipelineStageStates.WaitingExternal,
            "Child binding changed request identity");

        Refused(() => first.WatchExistingGitHub(accepted.OperationId, "ci", bound.Revision),
            "GitHub watch began before local completion");
        PipelineRunSnapshot done = first.RecordOutcome(accepted.OperationId, "debug", bound.Revision,
            true, "child-report:sha256:123");
        PipelineRunSnapshot watching = first.WatchExistingGitHub(accepted.OperationId, "ci", done.Revision);
        Check(watching.Status == "waiting_external" &&
              watching.Stages[1].ExternalReference ==
              "github:cpp20120/CMakeWorkflow:ci.yml:123456:2:0123456789abcdef0123456789abcdef01234567",
            "Attached GitHub run must retain exact pinned identity");
        Refused(() => first.RecordOutcome(accepted.OperationId, "ci", done.Revision,
            true, "github-run:fake"), "Stale provider callback accepted");
        PipelineRunSnapshot finished = first.RecordOutcome(accepted.OperationId, "ci", watching.Revision,
            true, "github-run:verified-result");
        Check(finished.Status == "succeeded" && finished.Revision == 5 &&
              finished.Stages.All(s => s.Status == PipelineStageStates.Succeeded),
            "Terminal success state or revision is wrong");
        Check(concurrent.Find("success-01")?.Revision == finished.Revision &&
              concurrent.Accept("success-01", Document()).OperationId == finished.OperationId,
            "Idempotent acceptance lost operation after stage changes");
        Check(first.Events(finished.OperationId).Select(e => e.Revision).SequenceEqual(
                [0L, 1L, 2L, 3L, 4L, 5L]),
            "Transition event sequence lost or duplicated entries");
        Check(first.Events(finished.OperationId, 3).Select(e => e.Kind)
                .SequenceEqual(["github_watch_recorded", "succeeded"]),
            "Event cursor returned wrong transitions");
    }

    private static void ExerciseFailedPipeline(string path)
    {
        using PipelineLedger ledger = new(path);
        PipelineRunSnapshot accepted = ledger.Accept("failure-01", Document());
        PipelineRunSnapshot reserved = ledger.ReserveWorkflow(accepted.OperationId, "debug", accepted.Revision);
        PipelineRunSnapshot bound = ledger.BindChild(accepted.OperationId, "debug", reserved.Revision, "child-op-fail");
        PipelineRunSnapshot failed = ledger.RecordOutcome(accepted.OperationId, "debug", bound.Revision,
            false, "child-report:failed");
        Check(failed.Status == "failed" && failed.Stages[0].Status == PipelineStageStates.Failed,
            "Failed child was not preserved as failure");
        Refused(() => ledger.WatchExistingGitHub(accepted.OperationId, "ci", failed.Revision),
            "Failed dependency allowed downstream execution");
        Refused(() => ledger.RecordOutcome(accepted.OperationId, "debug", failed.Revision,
            true, "overwrite"), "Failed terminal state was overwritten");
    }

    private static void ExerciseIntegrityCheck(string path)
    {
        string id;
        using (PipelineLedger ledger = new(path))
        {
            id = ledger.Accept("corrupt-01", Document()).OperationId;
        }

        using (SqliteConnection connection = new($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE pipeline_runs SET manifest_json='{}' WHERE operation_id=$id;";
            _ = cmd.Parameters.AddWithValue("$id", id);
            _ = cmd.ExecuteNonQuery();
        }

        using PipelineLedger reopened = new(path);
        bool rejected = false;
        try
        {
            _ = reopened.Get(id);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Check(rejected, "Tampered manifest snapshot bypassed hash verification");
        // Ensure a healthy, unrelated accepted operation remains readable.
        Check(reopened.Find("success-01")?.Status == "succeeded", "Corruption leaked into another operation");
    }
}
