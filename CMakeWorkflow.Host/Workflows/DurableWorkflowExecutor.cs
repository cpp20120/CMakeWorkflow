using CMakeWorkflow.CMake.Execution;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.Profiling;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

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
        ArgumentNullException.ThrowIfNull(operationId);
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceReadPaths paths = new(workspace);
        if (operationId.Length != 32 || !operationId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        }

        string lockDir = Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "execution-locks");
        _ = Directory.CreateDirectory(lockDir);
        await using FileStream lease = new(Path.Combine(lockDir, operationId + ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using DurableAcceptanceStore store = new(paths.WorkspaceDirectory);
        AcceptedWorkflow accepted = store.Get(operationId) ?? throw new KeyNotFoundException("Unknown operation.");
        (string? RunDirectory, string? CheckpointSha256) = store.GetDispatchCheckpoint(operationId)
                    ?? throw new InvalidOperationException("resume_checkpoint_missing");
        string output = paths.Require(RunDirectory);
        string checkpointPath = paths.Require(Path.Combine(output, "run.json"));
        WorkflowReport saved = System.Text.Json.JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(checkpointPath, cancellationToken),
            WorkflowJson.Default.WorkflowReport) ?? throw new InvalidDataException("Invalid checkpoint.");
        string manifest = paths.Require(saved.Workflow);
        WorkflowPlan plan = await WorkflowPlanner.LoadSnapshotAsync(manifest, store.GetSnapshot(operationId), [], null,
            cancellationToken, paths.Read);
        if (!string.Equals(plan.ManifestHash, accepted.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Resume snapshot mismatch.");
        }

        IReadOnlyDictionary<string, WorkflowToolchainRuntime> toolchains = await WorkflowToolchainAdmission.ResolveAsync(plan,
            new WindowsWorkflowToolchainProvider(), cancellationToken);
        foreach ((string? tree, WorkflowToolchainRuntime? selected) in toolchains)
        {
            WorkflowToolchainAdmission.ValidateBuildTree(tree, selected);
        }

        WorkflowToolchainAdmission.RequireSameIdentity(saved.Toolchains, toolchains);
        // Permanent claim occurs before any runner mutation or external dispatch.
        _ = store.ClaimResumeAttempt(operationId, output);
        DurableRunnerDispatchGate gate = new(paths.WorkspaceDirectory, operationId);
        int code = await WorkflowRunner.RunAsync(plan, 1, output, cancellationToken,
            extension: new ProfileWorkflowExecutor(), resume: true,
            beforeExternalDispatch: gate.ConfirmAsync, toolchains: toolchains);
        if (code is 0 or 1 or 130)
        {
            _ = store.FinalizeExecution(operationId, output, code);
        }

        return code;
    }

    public static async Task<int> ExecuteAcceptedAsync(string workspace, string operationId,
        string clientRequestId, string originalManifestPath, string expectedManifestHash,
        int jobs, string resultsDirectory, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? faultBarrier = null)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(jobs, 1);

        WorkspaceReadPaths paths = new(workspace);
        string manifest = paths.Require(originalManifestPath);
        string output = paths.Require(resultsDirectory);
        // The outer lease covers admission lookup, authorization, runner lifetime
        // and final report. It is not a substitute for the runner's own run lease.
        string lockDirectory = Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "execution-locks");
        _ = Directory.CreateDirectory(lockDirectory);
        if (operationId.Length != 32 || !operationId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        }

        await using FileStream lease = new(Path.Combine(lockDirectory, operationId + ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using DurableAcceptanceStore store = new(paths.WorkspaceDirectory);
        AcceptedWorkflow existing = store.Get(operationId) ?? throw new KeyNotFoundException("Unknown operation.");
        if (existing.State != "accepted_not_dispatched")
        {
            throw new InvalidOperationException(
                "operation_already_authorized: inspect checkpoint/reconcile; never start again implicitly");
        }
        // A repeat cannot replace accepted parameters. Accept compares the original
        // request digest BEFORE reading the current manifest, including after edits.
        AcceptedWorkflow verified = store.Accept(clientRequestId, manifest, expectedManifestHash, jobs, output);
        if (verified.OperationId != operationId || verified.ManifestBaseDirectory != Path.GetDirectoryName(manifest))
        {
            throw new InvalidOperationException("operation_request_mismatch");
        }

        byte[] snapshot = store.GetSnapshot(operationId);
        WorkflowPlan plan = await WorkflowPlanner.LoadSnapshotAsync(manifest, snapshot, [], null,
            cancellationToken, paths.Read);
        if (!string.Equals(plan.ManifestHash, existing.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Immutable snapshot hash changed.");
        }

        string[] errors = [.. plan.Nodes.SelectMany(n => n.Diagnostics
            .Where(d => d.Severity == "Error")
            .Select(d => $"{n.Definition.Id}: {d.Message}"))];
        if (errors.Length != 0)
        {
            throw new InvalidOperationException("workflow_preflight_rejected: " + string.Join(" | ", errors));
        }

        if (Directory.Exists(output) && File.Exists(Path.Combine(output, "run.json")))
        {
            throw new InvalidOperationException("run_directory_already_initialized");
        }

        IReadOnlyDictionary<string, WorkflowToolchainRuntime> toolchains = await WorkflowToolchainAdmission.ResolveAsync(plan,
            new WindowsWorkflowToolchainProvider(), cancellationToken);
        foreach ((string? tree, WorkflowToolchainRuntime? selected) in toolchains)
        {
            WorkflowToolchainAdmission.ValidateBuildTree(tree, selected);
        }
        // Reserve before authorization so two distinct operations cannot use one
        // results tree, even if the first host dies before its runner starts.
        store.ReserveResultsDirectory(operationId, output);
        // From here onward, a crash must NOT permit a second implicit invocation.
        // The runner's write-ahead checkpoint is the next durable boundary.
        _ = store.AuthorizeDispatch(operationId);
        if (faultBarrier is not null)
        {
            await faultBarrier("authorized", cancellationToken);
        }

        DurableRunnerDispatchGate gate = new(paths.WorkspaceDirectory, operationId);
        int exitCode = await WorkflowRunner.RunAsync(plan, jobs, output, cancellationToken,
            extension: new ProfileWorkflowExecutor(), toolchains: toolchains,
            beforeExternalDispatch: async (checkpoint, ct) =>
            {
                await gate.ConfirmAsync(checkpoint, ct);
                if (faultBarrier is not null)
                {
                    await faultBarrier("checkpoint", ct);
                }
            });
        // No finalization from catch/finally: a crash leaves the outcome unresolved.
        if (exitCode is 0 or 1 or 130)
        {
            _ = store.FinalizeExecution(operationId, output, exitCode);
        }

        return exitCode;
    }
}