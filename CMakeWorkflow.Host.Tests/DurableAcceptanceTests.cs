using CMakeWorkflow;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Host;

internal static class DurableAcceptanceTests
{
    public static void Run(string root)
    {
        var workspace = Path.Combine(root, "durable-workspace");
        Directory.CreateDirectory(workspace);
        var manifest = Path.Combine(workspace, "workflow.json");
        var data = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
        File.WriteAllBytes(manifest, data);
        var hash = Convert.ToHexString(SHA256.HashData(data));
        string id, workspaceId;
        using (var store = new DurableAcceptanceStore(workspace))
        {
            var first = store.Accept("client-1", manifest, hash, 4, workspace);
            id = first.OperationId;
            workspaceId = first.WorkspaceId;
            if (!store.GetSnapshot(id).SequenceEqual(data) || first.State != "accepted_not_dispatched")
                throw new Exception("snapshot persistence");
        }
        File.WriteAllText(manifest, "changed after acceptance");
        // The planner must read supplied snapshot bytes, never the current file.
        // The accepted example is schema v1 without nodes: it must fail with the
        // snapshot's semantic validation error, NOT a JSON parser error from disk.
        try
        {
            WorkflowPlanner.LoadSnapshotAsync(manifest, data, [], null, CancellationToken.None)
                .GetAwaiter().GetResult();
            throw new Exception("incomplete snapshot was treated as runnable");
        }
        catch (ArgumentException e) when (e.Message.Contains("contexts and a nonempty nodes", StringComparison.Ordinal)) { }

        using (var reopened = new DurableAcceptanceStore(workspace))
        {
            var repeat = reopened.Accept("client-1", manifest, hash, 4, workspace);
            if (repeat.OperationId != id || repeat.WorkspaceId != workspaceId ||
                reopened.Find("client-1")?.OperationId != id || !reopened.GetSnapshot(id).SequenceEqual(data))
                throw new Exception("idempotent retry after manifest changed / reopen");
            try { reopened.Accept("client-1", manifest, hash, 8, workspace); throw new Exception("missing request ID conflict"); }
            catch (InvalidOperationException e) when (e.Message == "request_id_conflict") { }
            try { reopened.Accept("client-2", manifest, hash, 4, workspace); throw new Exception("missing stale manifest"); }
            catch (InvalidOperationException e) when (e.Message == "stale_manifest") { }
        }
        // Authorization is durable and idempotent, but DOES NOT launch a process.
        using (var firstWriter = new DurableAcceptanceStore(workspace))
        using (var secondWriter = new DurableAcceptanceStore(workspace))
        {
            var authorized = firstWriter.AuthorizeDispatch(id);
            if (authorized.State != "dispatch_authorized" ||
                secondWriter.AuthorizeDispatch(id).OperationId != id ||
                firstWriter.Find("client-1")?.State != "dispatch_authorized")
                throw new Exception("monotonic idempotent authorization");
            try { firstWriter.AuthorizeDispatch("missing"); throw new Exception("unknown operation was authorized"); }
            catch (KeyNotFoundException) { }
        }
        using (var recovered = new DurableAcceptanceStore(workspace, readOnly: true))
        {
            if (recovered.Get(id)?.State != "dispatch_authorized" ||
                !recovered.GetSnapshot(id).SequenceEqual(data))
                throw new Exception("dispatch authorization survived restart");
        }
        // Two connections race to authorize a second accepted operation. Both must
        // converge on the SAME durable authorization, with no new operation ID.
        File.WriteAllBytes(manifest, data);
        string raceId;
        using (var store = new DurableAcceptanceStore(workspace))
            raceId = store.Accept("client-race", manifest, hash, 1, workspace).OperationId;
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            using var store = new DurableAcceptanceStore(workspace);
            return store.AuthorizeDispatch(raceId);
        })).ToArray();
        Task.WaitAll(tasks);
        var outcomes = tasks.Select(task => task.Result).ToArray();
        if (outcomes.Any(result => result.OperationId != raceId || result.State != "dispatch_authorized"))
            throw new Exception("concurrent authorization");
        // An authorized acceptance alone must NOT be treated as runner dispatch.
        // The runner write-ahead checkpoint must exist, match the snapshot and
        // contain an admitted running node before the host gate confirms it.
        var runDir = Path.Combine(workspace, "run-gated");
        Directory.CreateDirectory(runDir);
        using (var store = new DurableAcceptanceStore(workspace))
        {
            try { store.ConfirmRunnerCheckpoint(id, runDir); throw new Exception("missing checkpoint accepted"); }
            catch (FileNotFoundException) { }
            var report = new WorkflowReport
            {
                ManifestHash = hash,
                Workflow = manifest,
                Status = "running",
                Nodes = [new NodeResult { Id = "n1", Kind = "invoke", Status = "running" }]
            };
            var checkpoint = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport);
            File.WriteAllBytes(Path.Combine(runDir, "run.json"), checkpoint);
            var recorded = store.ConfirmRunnerCheckpoint(id, runDir);
            // macOS may canonicalize /var to /private/var when persisting the binding.
            var canonicalRunDir = BuildTreeOwnership.CanonicalPath(runDir);
            if (recorded != Convert.ToHexString(SHA256.HashData(checkpoint)) ||
                store.GetDispatchCheckpoint(id)?.RunDirectory != canonicalRunDir)
                throw new Exception("checkpoint binding integrity");
            report.ManifestHash = new string('0', 64);
            File.WriteAllBytes(Path.Combine(runDir, "run.json"),
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport));
            try { store.ConfirmRunnerCheckpoint(id, runDir); throw new Exception("foreign manifest checkpoint accepted"); }
            catch (InvalidOperationException e) when (e.Message == "dispatch_checkpoint_mismatch") { }
            if (store.GetDispatchCheckpoint(id)?.CheckpointSha256 != recorded)
                throw new Exception("failed confirmation overwrote durable binding");
        }
        using (var reopened = new DurableAcceptanceStore(workspace, readOnly: true))
            if (reopened.GetDispatchCheckpoint(id)?.RunDirectory != BuildTreeOwnership.CanonicalPath(runDir))
                throw new Exception("checkpoint binding not durable");
        // The executable bridge must reject an already-authorized operation BEFORE
        // attempting any runner dispatch, even if the source manifest changed.
        try
        {
            DurableWorkflowExecutor.ExecuteAcceptedAsync(workspace, id, "client-1", manifest,
                hash, 4, workspace, CancellationToken.None).GetAwaiter().GetResult();
            throw new Exception("authorized operation executed twice");
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("operation_already_authorized", StringComparison.Ordinal)) { }
        var cancelId = "cancel-client-" + Guid.NewGuid().ToString("N");
        // Cancellation on an accepted (but not dispatched) operation is durable,
        // revisioned, and prevents a later dispatch authorization.
        using (var cancelStore = new DurableAcceptanceStore(workspace))
        {
            // Restore original bytes for an independently accepted test operation.
            File.WriteAllBytes(manifest, data);
            var candidate = cancelStore.Accept(cancelId, manifest, hash, 1, workspace);
            var decision = cancelStore.RequestCancellation(candidate.OperationId, 0);
            if (!decision.Requested || decision.Revision != 1 || decision.State != "cancel_requested")
                throw new Exception("Cancellation intent was not committed.");
            if (cancelStore.Get(candidate.OperationId)?.State != "cancel_requested" ||
                cancelStore.Accept(cancelId, manifest, hash, 1, workspace).State != "cancel_requested")
                throw new Exception("Cancellation state missing from lookup/retry.");
            try { cancelStore.RequestCancellation(candidate.OperationId, 0); throw new Exception("Missing CAS refusal."); }
            catch (InvalidOperationException e) when (e.Message == "operation_revision_conflict") { }
            if (!cancelStore.RequestCancellation(candidate.OperationId, 1).Requested)
                throw new Exception("Cancellation intent retry rejected.");
            try { cancelStore.AuthorizeDispatch(candidate.OperationId); throw new Exception("Canceled admission was authorized."); }
            catch (InvalidOperationException e) when (e.Message == "operation_cancel_requested") { }
        }
        using (var reopenedCancel = new DurableAcceptanceStore(workspace))
        {
            if (reopenedCancel.Find(cancelId)?.State != "cancel_requested")
                throw new Exception("Cancellation intent did not survive restart.");
        }
        Console.WriteLine("Durable cancellation checks passed: CAS, retry, restart and no post-cancel authorization.");
        Console.WriteLine("Runner dispatch gate checks passed: write-ahead binding, hash, failure and restart; no execution.");
        Console.WriteLine("Dispatch authorization checks passed: migration, idempotence, concurrency and restart; no execution.");
        Console.WriteLine("Durable acceptance checks passed: atomic snapshot, retry, conflict, restart and stale manifest.");
    }
}
