using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Host;

internal static class DurableRecoveryTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException(message);
        }
    }

    public static void Run(string parent)
    {
        string workspace = Path.Combine(parent, "recovery-workspace");
        _ = Directory.CreateDirectory(workspace);
        string manifest = Path.Combine(workspace, "workflow.json");
        byte[] bytes = Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"schemaVersion\":1}");
        File.WriteAllBytes(manifest, bytes);
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        string op;
        using (DurableAcceptanceStore store = new(workspace))
        {
            op = store.Accept("recovery-1", manifest, sha, 1, workspace).OperationId;
            DurableAcceptanceStore.RecoveryInspection before = store.InspectRecovery(op);
            Check(before.AcceptanceState == "accepted_not_dispatched" && before.Revision == 0 &&
                  before.Decision is null && before.RunDirectory is null, "inspection of accepted operation");
            try
            {
                _ = store.Reconcile(op, 0, "outcome_unknown", "premature");
                throw new System.Diagnostics.UnreachableException("reconciled without authorization");
            }
            catch (InvalidOperationException e) when (e.Message == "operation_not_dispatch_authorized")
            {
            }

            _ = store.AuthorizeDispatch(op);
            DurableAcceptanceStore.RecoveryInspection inspection = store.InspectRecovery(op);
            Check(inspection.AcceptanceState == "dispatch_authorized" && inspection.Decision is null,
                "inspection without runner checkpoint");
            DurableAcceptanceStore.RecoveryInspection decided = store.Reconcile(op, 0, "outcome_unknown", "Host died after authorization");
            Check(decided.Revision == 1 && decided.Decision == "outcome_unknown", "durable decision");
            DurableAcceptanceStore.RecoveryInspection again = store.Reconcile(op, 1, "outcome_unknown", "Host died after authorization");
            Check(again.Revision == 1, "idempotent reconciliation");
            try
            {
                _ = store.Reconcile(op, 0, "needs_attention", "overwrite");
                throw new System.Diagnostics.UnreachableException("CAS allowed overwrite");
            }
            catch (InvalidOperationException e) when (e.Message == "operation_revision_conflict")
            {
            }

            Check(store.Get(op)?.State == "dispatch_authorized", "reconciliation must not rewrite admission state");
        }

        using (DurableAcceptanceStore reopened = new(workspace, readOnly: true))
        {
            DurableAcceptanceStore.RecoveryInspection inspected = reopened.InspectRecovery(op);
            Check(inspected.Revision == 1 && inspected.Decision == "outcome_unknown" &&
                  inspected.Note == "Host died after authorization", "restart persistence");
        }

        using (DurableAcceptanceStore reopened = new(workspace))
        {
            Check(reopened.AuthorizeDispatch(op).State == "dispatch_authorized", "authorization idempotence");
            Check(reopened.InspectRecovery(op).Decision == "outcome_unknown", "decision preserved");
        }

        Console.WriteLine("Durable recovery checks passed: inspection, CAS decision, retry and restart; no resume.");
    }
}