using CMakeWorkflow;

namespace CMakeWorkflow.Host;

/// <summary>
/// One-shot, opt-in host-side execution bridge. No RPC method exposes this until
/// ownership, shutdown and crash-injection contracts have been verified.
/// </summary>
public static class DurableWorkflowExecutor
{
    // Uses the original immutable snapshot and runner checkpoint recovery; a
    // committed attempt is never automatically re-claimed after an exception.
    public static async Task<int> ExecuteResumedAsync(string workspace, string operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = new WorkspaceReadPaths(workspace);
        if (operationId.Length != 32 || !operationId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        var lockDir = Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "execution-locks");
        Directory.CreateDirectory(lockDir);
        await using var lease = new FileStream(Path.Combine(lockDir, operationId + ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var store = new DurableAcceptanceStore(paths.WorkspaceDirectory);
        var accepted = store.Get(operationId) ?? throw new KeyNotFoundException("Unknown operation.");
        var bound = store.GetDispatchCheckpoint(operationId)
            ?? throw new InvalidOperationException("resume_checkpoint_missing");
        var output = paths.Require(bound.RunDirectory);
        var checkpointPath = paths.Require(Path.Combine(output, "run.json"));
        var saved = System.Text.Json.JsonSerializer.Deserialize(await File.ReadAllBytesAsync(checkpointPath, cancellationToken),
            WorkflowJson.Default.WorkflowReport) ?? throw new InvalidDataException("Invalid checkpoint.");
        var manifest = paths.Require(saved.Workflow);
        var plan = await WorkflowPlanner.LoadSnapshotAsync(manifest, store.GetSnapshot(operationId), [], null,
            cancellationToken, paths.Read);
        if (!string.Equals(plan.ManifestHash, accepted.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resume snapshot mismatch.");
        // Permanent claim occurs before any runner mutation or external dispatch.
        store.ClaimResumeAttempt(operationId, output);
        var gate = new DurableRunnerDispatchGate(paths.WorkspaceDirectory, operationId);
        var code = await WorkflowRunner.RunAsync(plan, 1, output, cancellationToken,
            resume: true, beforeExternalDispatch: gate.ConfirmAsync);
        if (code is 0 or 1 or 130) store.FinalizeExecution(operationId, output, code);
        return code;
    }

    public static async Task<int> ExecuteAcceptedAsync(string workspace, string operationId,
        string clientRequestId, string originalManifestPath, string expectedManifestHash,
        int jobs, string resultsDirectory, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? faultBarrier = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (jobs < 1) throw new ArgumentOutOfRangeException(nameof(jobs));
        var paths = new WorkspaceReadPaths(workspace);
        var manifest = paths.Require(originalManifestPath);
        var output = paths.Require(resultsDirectory);
        // The outer lease covers admission lookup, authorization, runner lifetime
        // and final report. It is not a substitute for the runner's own run lease.
        var lockDirectory = Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "execution-locks");
        Directory.CreateDirectory(lockDirectory);
        if (operationId.Length != 32 || !operationId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        await using var lease = new FileStream(Path.Combine(lockDirectory, operationId + ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var store = new DurableAcceptanceStore(paths.WorkspaceDirectory);
        var existing = store.Get(operationId) ?? throw new KeyNotFoundException("Unknown operation.");
        if (existing.State != "accepted_not_dispatched")
            throw new InvalidOperationException("operation_already_authorized: inspect checkpoint/reconcile; never start again implicitly");
        // A repeat cannot replace accepted parameters. Accept compares the original
        // request digest BEFORE reading the current manifest, including after edits.
        var verified = store.Accept(clientRequestId, manifest, expectedManifestHash, jobs, output);
        if (verified.OperationId != operationId || verified.ManifestBaseDirectory != Path.GetDirectoryName(manifest))
            throw new InvalidOperationException("operation_request_mismatch");
        var snapshot = store.GetSnapshot(operationId);
        var plan = await WorkflowPlanner.LoadSnapshotAsync(manifest, snapshot, [], null,
            cancellationToken, paths.Read);
        if (!string.Equals(plan.ManifestHash, existing.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Immutable snapshot hash changed.");
        var errors = plan.Nodes.SelectMany(n => n.Diagnostics
            .Where(d => d.Severity == "Error")
            .Select(d => $"{n.Definition.Id}: {d.Message}")).ToArray();
        if (errors.Length != 0)
            throw new InvalidOperationException("workflow_preflight_rejected: " + string.Join(" | ", errors));
        if (Directory.Exists(output) && File.Exists(Path.Combine(output, "run.json")))
            throw new InvalidOperationException("run_directory_already_initialized");
        // Reserve before authorization so two distinct operations cannot use one
        // results tree, even if the first host dies before its runner starts.
        store.ReserveResultsDirectory(operationId, output);
        // From here onward, a crash must NOT permit a second implicit invocation.
        // The runner's write-ahead checkpoint is the next durable boundary.
        store.AuthorizeDispatch(operationId);
        if (faultBarrier is not null) await faultBarrier("authorized", cancellationToken);
        var gate = new DurableRunnerDispatchGate(paths.WorkspaceDirectory, operationId);
        var exitCode = await WorkflowRunner.RunAsync(plan, jobs, output, cancellationToken,
            beforeExternalDispatch: async (checkpoint, ct) =>
            {
                await gate.ConfirmAsync(checkpoint, ct);
                if (faultBarrier is not null) await faultBarrier("checkpoint", ct);
            });
        // No finalization from catch/finally: a crash leaves the outcome unresolved.
        if (exitCode is 0 or 1 or 130)
            store.FinalizeExecution(operationId, output, exitCode);
        return exitCode;
    }
}
