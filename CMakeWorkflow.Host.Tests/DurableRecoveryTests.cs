using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Host;

internal static class DurableRecoveryTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run(string parent)
    {
        var workspace = Path.Combine(parent, "recovery-workspace");
        Directory.CreateDirectory(workspace);
        var manifest = Path.Combine(workspace, "workflow.json");
        var bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
        File.WriteAllBytes(manifest, bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        string op;
        using (var store = new DurableAcceptanceStore(workspace))
        {
            op = store.Accept("recovery-1", manifest, sha, 1, workspace).OperationId;
            var before = store.InspectRecovery(op);
            Check(before.AcceptanceState == "accepted_not_dispatched" && before.Revision == 0 &&
                before.Decision is null && before.RunDirectory is null, "inspection of accepted operation");
            try { store.Reconcile(op, 0, "outcome_unknown", "premature"); throw new Exception("reconciled without authorization"); }
            catch (InvalidOperationException e) when (e.Message == "operation_not_dispatch_authorized") { }
            store.AuthorizeDispatch(op);
            var inspection = store.InspectRecovery(op);
            Check(inspection.AcceptanceState == "dispatch_authorized" && inspection.Decision is null,
                "inspection without runner checkpoint");
            var decided = store.Reconcile(op, 0, "outcome_unknown", "Host died after authorization");
            Check(decided.Revision == 1 && decided.Decision == "outcome_unknown", "durable decision");
            var again = store.Reconcile(op, 1, "outcome_unknown", "Host died after authorization");
            Check(again.Revision == 1, "idempotent reconciliation");
            try { store.Reconcile(op, 0, "needs_attention", "overwrite"); throw new Exception("CAS allowed overwrite"); }
            catch (InvalidOperationException e) when (e.Message == "operation_revision_conflict") { }
            Check(store.Get(op)?.State == "dispatch_authorized", "reconciliation must not rewrite admission state");
        }
        using (var reopened = new DurableAcceptanceStore(workspace, readOnly: true))
        {
            var inspected = reopened.InspectRecovery(op);
            Check(inspected.Revision == 1 && inspected.Decision == "outcome_unknown" &&
                inspected.Note == "Host died after authorization", "restart persistence");
        }
        using (var reopened = new DurableAcceptanceStore(workspace))
        {
            Check(reopened.AuthorizeDispatch(op).State == "dispatch_authorized", "authorization idempotence");
            Check(reopened.InspectRecovery(op).Decision == "outcome_unknown", "decision preserved");
        }
        Console.WriteLine("Durable recovery checks passed: inspection, CAS decision, retry and restart; no resume.");
    }
}
