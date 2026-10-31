using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow;
using CMakeWorkflow.Host;

internal static class ResumeReplayTests
{
    private static void Check(bool valid, string why)
    { if (!valid) throw new Exception(why); }

    public static async Task RunAsync(string parent)
    {
        var root = Directory.CreateDirectory(Path.Combine(parent, "resume-replay")).FullName;
        var manifest = Path.Combine(root, "workflow.json");
        var bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
        File.WriteAllBytes(manifest, bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        string needs, unknown;
        using (var store = new DurableAcceptanceStore(root))
        {
            needs = store.Accept("resume-needed", manifest, sha, 1, root).OperationId;
            unknown = store.Accept("resume-unknown", manifest, sha, 1, root).OperationId;
            store.AuthorizeDispatch(needs);
            store.AuthorizeDispatch(unknown);
            store.Reconcile(needs, 0, "needs_attention", "repair completed");
            store.Reconcile(unknown, 0, "outcome_unknown", "dispatch result unknown");
            try { store.AdmitResume(unknown, 1); throw new Exception("unknown admitted"); }
            catch (InvalidOperationException e) when (e.Message == "outcome_unknown_requires_resolution") { }
            try { store.AdmitResume(needs, 0); throw new Exception("stale CAS admitted"); }
            catch (InvalidOperationException e) when (e.Message == "operation_revision_conflict") { }
            Check(store.AdmitResume(needs, 1).Revision == 2, "resume admission revision");
            Check(store.AdmitResume(needs, 2).Revision == 2, "resume retry");
        }
        using (var store = new DurableAcceptanceStore(root, readOnly: true))
            Check(store.InspectRecovery(needs).Revision == 2, "resume admission restart");
        try { using var store = new DurableAcceptanceStore(root); store.ClaimResumeAttempt(needs, root); throw new Exception("missing checkpoint accepted"); }
        catch (FileNotFoundException) { }
        catch (InvalidOperationException e) when (e.Message is "resume_checkpoint_not_safe" or "resume_not_admitted_or_bound") { }
        var run = Directory.CreateDirectory(Path.Combine(root, "run")).FullName;
        // A real runner checkpoint must be bound before an attempt can be claimed.
        var checkpoint = new WorkflowReport
        {
            Workflow = manifest, ManifestHash = sha, Status = "running",
            Nodes = [new NodeResult { Id = "n", Kind = "invoke", Status = "running" }]
        };
        await File.WriteAllTextAsync(Path.Combine(run, "run.json"),
            JsonSerializer.Serialize(checkpoint, WorkflowJson.Default.WorkflowReport));
        using (var store = new DurableAcceptanceStore(root))
            store.ConfirmRunnerCheckpoint(needs, run);
        checkpoint.Status = "needs_attention";
        checkpoint.Nodes[0].Status = "needs_attention";
        await File.WriteAllTextAsync(Path.Combine(run, "run.json"),
            JsonSerializer.Serialize(checkpoint, WorkflowJson.Default.WorkflowReport));
        string attempt;
        using (var store = new DurableAcceptanceStore(root))
        {
            attempt = store.ClaimResumeAttempt(needs, run);
            Check(attempt.Length == 32, "attempt identity");
            try { store.ClaimResumeAttempt(needs, run); throw new Exception("attempt repeated"); }
            catch (InvalidOperationException e) when (e.Message == "resume_attempt_already_claimed") { }
        }
        using (var restarted = new DurableAcceptanceStore(root))
        {
            try { restarted.ClaimResumeAttempt(needs, run); throw new Exception("restart repeated attempt"); }
            catch (InvalidOperationException e) when (e.Message == "resume_attempt_already_claimed") { }
        }
        var events = new List<WorkflowEvent>();
        for (var i = 1; i <= 3; i++)
            events.Add(new WorkflowEvent(1, i, DateTimeOffset.UtcNow, "node_finished", run,
                "n" + i, "invoke", "succeeded", 0, null, []));
        var journal = Path.Combine(run, "events.jsonl");
        await File.WriteAllLinesAsync(journal,
            events.Select(e => JsonSerializer.Serialize(e, WorkflowProtocolJson.Default.WorkflowEvent)));
        var first = await OperationEventReplay.ReadJournalAsync(journal, needs, 0, 2, default);
        Check(first.Events.Count == 2 && first.NextSequence == 2 && first.HasMore, "first page");
        var second = await OperationEventReplay.ReadJournalAsync(journal, needs, 2, 2, default);
        Check(second.Events.Count == 1 && second.NextSequence == 3 && !second.HasMore, "second page");
        Check((await OperationEventReplay.ReadJournalAsync(journal, needs, 3, 2, default)).Events.Count == 0,
            "empty tail");
        Console.WriteLine("Resume admission/replay checks passed: CAS, unknown refusal, restart and event cursor.");
    }
}
