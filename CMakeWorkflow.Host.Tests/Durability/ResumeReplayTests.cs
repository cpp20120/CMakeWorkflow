using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow;
using CMakeWorkflow.Host;

internal static class ResumeReplayTests
{
    private static void Check(bool valid, string why)
    {
        if (!valid)
        {
            throw new System.Diagnostics.UnreachableException(why);
        }
    }

    public static async Task RunAsync(string parent)
    {
        string root = Directory.CreateDirectory(Path.Combine(parent, "resume-replay")).FullName;
        string manifest = Path.Combine(root, "workflow.json");
        byte[] bytes = Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"schemaVersion\":1}");
        await File.WriteAllBytesAsync(manifest, bytes, CancellationToken.None);
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        string needs, unknown;
        using (DurableAcceptanceStore store = new(root))
        {
            needs = store.Accept("resume-needed", manifest, sha, 1, root).OperationId;
            unknown = store.Accept("resume-unknown", manifest, sha, 1, root).OperationId;
            _ = store.AuthorizeDispatch(needs);
            _ = store.AuthorizeDispatch(unknown);
            _ = store.Reconcile(needs, 0, "needs_attention", "repair completed");
            _ = store.Reconcile(unknown, 0, "outcome_unknown", "dispatch result unknown");
            try
            {
                _ = store.AdmitResume(unknown, 1);
                throw new System.Diagnostics.UnreachableException("unknown admitted");
            }
            catch (InvalidOperationException e) when (e.Message == "outcome_unknown_requires_resolution")
            {
            }

            try
            {
                _ = store.AdmitResume(needs, 0);
                throw new System.Diagnostics.UnreachableException("stale CAS admitted");
            }
            catch (InvalidOperationException e) when (e.Message == "operation_revision_conflict")
            {
            }

            Check(store.AdmitResume(needs, 1).Revision == 2, "resume admission revision");
            Check(store.AdmitResume(needs, 2).Revision == 2, "resume retry");
        }

        using (DurableAcceptanceStore store = new(root, readOnly: true))
        {
            Check(store.InspectRecovery(needs).Revision == 2, "resume admission restart");
        }

        try
        {
            using DurableAcceptanceStore store = new(root);
            _ = store.ClaimResumeAttempt(needs, root);
            throw new System.Diagnostics.UnreachableException("missing checkpoint accepted");
        }
        catch (FileNotFoundException)
        {
        }
        catch (InvalidOperationException e) when (e.Message is "resume_checkpoint_not_safe"
                                                      or "resume_not_admitted_or_bound")
        {
        }

        string run = Directory.CreateDirectory(Path.Combine(root, "run")).FullName;
        // A real runner checkpoint must be bound before an attempt can be claimed.
        WorkflowReport checkpoint = new()
        {
            Workflow = manifest,
            ManifestHash = sha,
            Status = "running",
            Nodes = [new NodeResult { Id = "n", Kind = "invoke", Status = "running" }]
        };
        await File.WriteAllTextAsync(Path.Combine(run, "run.json"),
            JsonSerializer.Serialize(checkpoint, WorkflowJson.Default.WorkflowReport));
        using (DurableAcceptanceStore store = new(root))
        {
            _ = store.ConfirmRunnerCheckpoint(needs, run);
        }

        checkpoint.Status = "needs_attention";
        checkpoint.Nodes[0].Status = "needs_attention";
        await File.WriteAllTextAsync(Path.Combine(run, "run.json"),
            JsonSerializer.Serialize(checkpoint, WorkflowJson.Default.WorkflowReport));
        string attempt;
        using (DurableAcceptanceStore store = new(root))
        {
            attempt = store.ClaimResumeAttempt(needs, run);
            Check(attempt.Length == 32, "attempt identity");
            try
            {
                _ = store.ClaimResumeAttempt(needs, run);
                throw new System.Diagnostics.UnreachableException("attempt repeated");
            }
            catch (InvalidOperationException e) when (e.Message == "resume_attempt_already_claimed")
            {
            }
        }

        using (DurableAcceptanceStore restarted = new(root))
        {
            try
            {
                _ = restarted.ClaimResumeAttempt(needs, run);
                throw new System.Diagnostics.UnreachableException("restart repeated attempt");
            }
            catch (InvalidOperationException e) when (e.Message == "resume_attempt_already_claimed")
            {
            }
        }

        List<WorkflowEvent> events = [];
        for (int i = 1; i <= 3; i++)
        {
            events.Add(new WorkflowEvent(1, i, DateTimeOffset.UtcNow, "node_finished", run,
                "n" + i, "invoke", "succeeded", 0, null, []));
        }

        string journal = Path.Combine(run, "events.jsonl");
        await File.WriteAllLinesAsync(journal,
            events.Select(e => JsonSerializer.Serialize(e, WorkflowProtocolJson.Default.WorkflowEvent)));
        OperationEventsPage first = await OperationEventReplay.ReadJournalAsync(journal, needs, 0, 2, default);
        Check(first.Events.Count == 2 && first.NextSequence == 2 && first.HasMore, "first page");
        OperationEventsPage second = await OperationEventReplay.ReadJournalAsync(journal, needs, 2, 2, default);
        Check(second.Events.Count == 1 && second.NextSequence == 3 && !second.HasMore, "second page");
        Check((await OperationEventReplay.ReadJournalAsync(journal, needs, 3, 2, default)).Events.Count == 0,
            "empty tail");
        Console.WriteLine("Resume admission/replay checks passed: CAS, unknown refusal, restart and event cursor.");
    }
}