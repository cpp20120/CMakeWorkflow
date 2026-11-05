using CMakeWorkflow;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Host;

internal static class DurableAcceptanceTests
{
    public static void Run(string root)
    {
        string workspace = Path.Combine(root, "durable-workspace");
        _ = Directory.CreateDirectory(workspace);
        string manifest = Path.Combine(workspace, "workflow.json");
        byte[] data = Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"schemaVersion\":1}");
        File.WriteAllBytes(manifest, data);
        string hash = Convert.ToHexString(SHA256.HashData(data));
        string id, workspaceId;
        using (DurableAcceptanceStore store = new(workspace))
        {
            AcceptedWorkflow first = store.Accept("client-1", manifest, hash, 4, workspace);
            id = first.OperationId;
            workspaceId = first.WorkspaceId;
            if (!store.GetSnapshot(id).SequenceEqual(data) || first.State != "accepted_not_dispatched")
            {
                throw new System.Diagnostics.UnreachableException("snapshot persistence");
            }
        }

        File.WriteAllText(manifest, "changed after acceptance");
        // The planner must read supplied snapshot bytes, never the current file.
        // The accepted example is schema v1 without nodes: it must fail with the
        // snapshot's semantic validation error, NOT a JSON parser error from disk.
        try
        {
            _ = WorkflowPlanner.LoadSnapshotAsync(manifest, data, [], null, CancellationToken.None)
                .GetAwaiter().GetResult();
            throw new System.Diagnostics.UnreachableException("incomplete snapshot was treated as runnable");
        }
        catch (ArgumentException e) when (e.Message.Contains("contexts and a nonempty nodes", StringComparison.Ordinal))
        {
        }

        using (DurableAcceptanceStore reopened = new(workspace))
        {
            AcceptedWorkflow repeat = reopened.Accept("client-1", manifest, hash, 4, workspace);
            if (repeat.OperationId != id || repeat.WorkspaceId != workspaceId ||
                reopened.Find("client-1")?.OperationId != id || !reopened.GetSnapshot(id).SequenceEqual(data))
            {
                throw new System.Diagnostics.UnreachableException("idempotent retry after manifest changed / reopen");
            }

            try
            {
                _ = reopened.Accept("client-1", manifest, hash, 8, workspace);
                throw new System.Diagnostics.UnreachableException("missing request ID conflict");
            }
            catch (InvalidOperationException e) when (e.Message == "request_id_conflict")
            {
            }

            try
            {
                _ = reopened.Accept("client-2", manifest, hash, 4, workspace);
                throw new System.Diagnostics.UnreachableException("missing stale manifest");
            }
            catch (InvalidOperationException e) when (e.Message == "stale_manifest")
            {
            }
        }

        // Authorization is durable and idempotent, but DOES NOT launch a process.
        using (DurableAcceptanceStore firstWriter = new(workspace))
        using (DurableAcceptanceStore secondWriter = new(workspace))
        {
            AcceptedWorkflow authorized = firstWriter.AuthorizeDispatch(id);
            if (authorized.State != "dispatch_authorized" ||
                secondWriter.AuthorizeDispatch(id).OperationId != id ||
                firstWriter.Find("client-1")?.State != "dispatch_authorized")
            {
                throw new System.Diagnostics.UnreachableException("monotonic idempotent authorization");
            }

            try
            {
                _ = firstWriter.AuthorizeDispatch("missing");
                throw new System.Diagnostics.UnreachableException("unknown operation was authorized");
            }
            catch (KeyNotFoundException)
            {
            }
        }

        using (DurableAcceptanceStore recovered = new(workspace, readOnly: true))
        {
            if (recovered.Get(id)?.State != "dispatch_authorized" ||
                !recovered.GetSnapshot(id).SequenceEqual(data))
            {
                throw new System.Diagnostics.UnreachableException("dispatch authorization survived restart");
            }
        }

        // Two connections race to authorize a second accepted operation. Both must
        // converge on the SAME durable authorization, with no new operation ID.
        File.WriteAllBytes(manifest, data);
        string raceId;
        using (DurableAcceptanceStore store = new(workspace))
        {
            raceId = store.Accept("client-race", manifest, hash, 1, workspace).OperationId;
        }

        Task<AcceptedWorkflow>[] tasks = [.. Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            using DurableAcceptanceStore store = new(workspace);
            return store.AuthorizeDispatch(raceId);
        }))];
        Task.WaitAll(tasks);
        AcceptedWorkflow[] outcomes = [.. tasks.Select(task => task.Result)];
        if (outcomes.Any(result => result.OperationId != raceId || result.State != "dispatch_authorized"))
        {
            throw new System.Diagnostics.UnreachableException("concurrent authorization");
        }
        // An authorized acceptance alone must NOT be treated as runner dispatch.
        // The runner write-ahead checkpoint must exist, match the snapshot and
        // contain an admitted running node before the host gate confirms it.
        string runDir = Path.Combine(workspace, "run-gated");
        _ = Directory.CreateDirectory(runDir);
        using (DurableAcceptanceStore store = new(workspace))
        {
            try
            {
                _ = store.ConfirmRunnerCheckpoint(id, runDir);
                throw new System.Diagnostics.UnreachableException("missing checkpoint accepted");
            }
            catch (FileNotFoundException)
            {
            }

            WorkflowReport report = new()
            {
                ManifestHash = hash,
                Workflow = manifest,
                Status = "running",
                Nodes = [new NodeResult { Id = "n1", Kind = "invoke", Status = "running" }]
            };
            byte[] checkpoint =
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport);
            File.WriteAllBytes(Path.Combine(runDir, "run.json"), checkpoint);
            string recorded = store.ConfirmRunnerCheckpoint(id, runDir);
            // macOS may canonicalize /var to /private/var when persisting the binding.
            string canonicalRunDir = BuildTreeOwnership.CanonicalPath(runDir);
            if (recorded != Convert.ToHexString(SHA256.HashData(checkpoint)) ||
                store.GetDispatchCheckpoint(id)?.RunDirectory != canonicalRunDir)
            {
                throw new System.Diagnostics.UnreachableException("checkpoint binding integrity");
            }

            report.ManifestHash = new string('0', 64);
            File.WriteAllBytes(Path.Combine(runDir, "run.json"),
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport));
            try
            {
                _ = store.ConfirmRunnerCheckpoint(id, runDir);
                throw new System.Diagnostics.UnreachableException("foreign manifest checkpoint accepted");
            }
            catch (InvalidOperationException e) when (e.Message == "dispatch_checkpoint_mismatch")
            {
            }

            if (store.GetDispatchCheckpoint(id)?.CheckpointSha256 != recorded)
            {
                throw new System.Diagnostics.UnreachableException("failed confirmation overwrote durable binding");
            }
        }

        using (DurableAcceptanceStore reopened = new(workspace, readOnly: true))
        {
            if (reopened.GetDispatchCheckpoint(id)?.RunDirectory != BuildTreeOwnership.CanonicalPath(runDir))
            {
                throw new System.Diagnostics.UnreachableException("checkpoint binding not durable");
            }
        }
        // The executable bridge must reject an already-authorized operation BEFORE
        // attempting any runner dispatch, even if the source manifest changed.
        try
        {
            _ = DurableWorkflowExecutor.ExecuteAcceptedAsync(workspace, id, "client-1", manifest,
                hash, 4, workspace, CancellationToken.None).GetAwaiter().GetResult();
            throw new System.Diagnostics.UnreachableException("authorized operation executed twice");
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("operation_already_authorized",
                                                      StringComparison.Ordinal))
        {
        }

        string cancelId = "cancel-client-" + Guid.NewGuid().ToString("N");
        // Cancellation on an accepted (but not dispatched) operation is durable,
        // revisioned, and prevents a later dispatch authorization.
        using (DurableAcceptanceStore cancelStore = new(workspace))
        {
            // Restore original bytes for an independently accepted test operation.
            File.WriteAllBytes(manifest, data);
            AcceptedWorkflow candidate = cancelStore.Accept(cancelId, manifest, hash, 1, workspace);
            DurableAcceptanceStore.CancellationDecision decision = cancelStore.RequestCancellation(candidate.OperationId, 0);
            if (!decision.Requested || decision.Revision != 1 || decision.State != "cancel_requested")
            {
                throw new System.Diagnostics.UnreachableException("Cancellation intent was not committed.");
            }

            if (cancelStore.Get(candidate.OperationId)?.State != "cancel_requested" ||
                cancelStore.Accept(cancelId, manifest, hash, 1, workspace).State != "cancel_requested")
            {
                throw new System.Diagnostics.UnreachableException("Cancellation state missing from lookup/retry.");
            }

            try
            {
                _ = cancelStore.RequestCancellation(candidate.OperationId, 0);
                throw new System.Diagnostics.UnreachableException("Missing CAS refusal.");
            }
            catch (InvalidOperationException e) when (e.Message == "operation_revision_conflict")
            {
            }

            if (!cancelStore.RequestCancellation(candidate.OperationId, 1).Requested)
            {
                throw new System.Diagnostics.UnreachableException("Cancellation intent retry rejected.");
            }

            try
            {
                _ = cancelStore.AuthorizeDispatch(candidate.OperationId);
                throw new System.Diagnostics.UnreachableException("Canceled admission was authorized.");
            }
            catch (InvalidOperationException e) when (e.Message == "operation_cancel_requested")
            {
            }
        }

        using (DurableAcceptanceStore reopenedCancel = new(workspace))
        {
            if (reopenedCancel.Find(cancelId)?.State != "cancel_requested")
            {
                throw new System.Diagnostics.UnreachableException("Cancellation intent did not survive restart.");
            }
        }

        Console.WriteLine("Durable cancellation checks passed: CAS, retry, restart and no post-cancel authorization.");
        Console.WriteLine(
            "Runner dispatch gate checks passed: write-ahead binding, hash, failure and restart; no execution.");
        Console.WriteLine(
            "Dispatch authorization checks passed: migration, idempotence, concurrency and restart; no execution.");
        Console.WriteLine(
            "Durable acceptance checks passed: atomic snapshot, retry, conflict, restart and stale manifest.");
    }
}