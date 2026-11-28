using CMakeWorkflow.CMake.Recovery;
using System.Security.Cryptography;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Host.Workflows;

namespace CMakeWorkflow.Host.Execution;

// The Host holds the same permanent authorization and execution lease as the
// CMake runner. Resume is explicit and guarded by the existing one-shot claim.
/// <summary>Host-owned process-domain execution across immutable acceptance and checkpoint evidence.</summary>
/// <remarks>
/// Holds a cross-process operation lease across validation, runner execution and finalization.
/// The runner separately owns its run lease and child cleanup. Only verified exit codes 0, 1 or 130 finalize.
/// Exceptions preserve uncertainty; lease disposal neither rolls back external effects nor releases durable results ownership.
/// </remarks>
internal static class DurableProcessWorkflowExecutor
{
    /// <summary>Runs an explicitly admitted process recovery after a permanent one-shot claim.</summary>
    /// <remarks>
    /// Validates checkpoint hash, original graph, recovery note and source binding before runner mutation.
    /// The runner rechecks exact bytes and claims under the run lease; interrupted attempts cannot be automatically reclaimed.
    /// </remarks>
    internal static async Task<int> ExecuteResumedAsync(string workspace, string operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceReadPaths paths = new(workspace);
        if (operationId.Length != 32 || !operationId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        }

        string lockDirectory = paths.Require(Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "execution-locks"));
        _ = Directory.CreateDirectory(lockDirectory);
        await using FileStream lease = new(paths.Require(Path.Combine(lockDirectory, operationId + ".lock")),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using DurableAcceptanceStore store = new(paths.WorkspaceDirectory, false,
            ProcessCheckpointEvidenceCodec.Instance);
        if (store.GetDomainKind(operationId) != "process")
        {
            throw new InvalidOperationException("wrong_domain");
        }

        AcceptedWorkflow accepted = store.Get(operationId) ?? throw new KeyNotFoundException("Unknown operation.");
        DurableAcceptanceStore.RecoveryInspection inspection = store.InspectRecovery(operationId);
        if (inspection.Revision != 2 || inspection.Decision != "needs_attention" ||
            inspection.TerminalState is not null || inspection.CancellationRequested)
        {
            throw new InvalidOperationException("resume_not_admitted_or_bound");
        }

        (string? RunDirectory, string? CheckpointSha256) = store.GetDispatchCheckpoint(operationId)
                      ?? throw new InvalidOperationException("resume_checkpoint_missing");
        string output = paths.Require(RunDirectory);
        _ = paths.Require(accepted.ManifestBaseDirectory);
        byte[] snapshot = store.GetSnapshot(operationId); // verifies the stored SHA
        ProcessWorkflowManifest plan = ProcessWorkflowDomain.ReadPlan(snapshot, paths);
        byte[] bytes = await store.ReadProcessEvidenceAsync(paths, operationId, output, cancellationToken);
        OperationCheckpointEvidence evidence = store.ProcessEvidenceCodec.ForResume(bytes);
        ProcessOperationReport report = store.ProcessEvidenceCodec.Read(bytes);
        if (report.Recovery!.Note != inspection.Note ||
            !string.Equals(evidence.ResumeSourceCheckpointSha256, CheckpointSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Process recovery checkpoint binding changed.");
        }

        ProcessWorkflowRecovery.ValidatePlan(report, plan, accepted.ManifestSha256, paths, output);
        store.VerifyProcessRetryCandidates(operationId, report);
        string hash = ProcessWorkflowRecovery.Hash(bytes);
        // Runner holds the run lease while rechecking these exact bytes, claiming
        // the attempt, and only then changing state or starting any process.
        int code = await ProcessWorkflowRunner.RunAsync(plan, accepted.ManifestSha256, paths, output, 1,
            () => { _ = store.ConfirmRunnerCheckpoint(operationId, output); return Task.CompletedTask; },
            resume: true, expectedCheckpointSha256: hash,
            claimResume: () => { _ = store.ClaimResumeAttempt(operationId, output, hash); return Task.CompletedTask; }, cancellationToken: cancellationToken,
            persistence: store.UsesNativeProcessCheckpoints ? new(store, operationId) : null);
        if (code is 0 or 1 or 130)
        {
            await RecoveryFaults.HitAsync("process-before-finalization", null, CancellationToken.None);
            _ = store.FinalizeExecution(operationId, output, code);
            await RecoveryFaults.HitAsync("process-after-finalization", null, CancellationToken.None);
        }

        return code;
    }

    /// <summary>Validates accepted process input, reserves results and authorizes one initial dispatch.</summary>
    /// <remarks>
    /// Uses the original immutable snapshot. The runner binds its durable running checkpoint before launching workers.
    /// The supplied token belongs to Host execution, not the RPC; authorization prevents implicit replay after a crash.
    /// </remarks>
    internal static async Task<int> ExecuteAcceptedAsync(string workspace, string operationId,
        DurableImmutableRequest request, int jobs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceReadPaths paths = new(workspace);
        string lockDirectory = paths.Require(Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "execution-locks"));
        _ = Directory.CreateDirectory(lockDirectory);
        if (operationId.Length != 32 || !operationId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        }

        await using FileStream lease = new(paths.Require(Path.Combine(lockDirectory, operationId + ".lock")),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using DurableAcceptanceStore store = new(paths.WorkspaceDirectory, false,
            ProcessCheckpointEvidenceCodec.Instance);
        AcceptedWorkflow accepted = store.Get(operationId) ?? throw new KeyNotFoundException("Unknown operation.");
        if (store.GetDomainKind(operationId) != "process" ||
            (!store.UsesNativeProcessCheckpoints && accepted.State != "accepted_not_dispatched"))
        {
            throw new InvalidOperationException("operation_already_authorized_or_wrong_domain");
        }

        AcceptedWorkflow verified = store.AcceptImmutable(request);
        if (verified.OperationId != operationId)
        {
            throw new InvalidOperationException("operation_request_mismatch");
        }

        byte[] snapshot = store.GetSnapshot(operationId);
        ProcessWorkflowManifest plan = ProcessWorkflowDomain.ReadPlan(snapshot, paths);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(snapshot)), accepted.ManifestSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Immutable process manifest changed.");
        }

        if (accepted.State != "accepted_not_dispatched")
        {
            return RecoverFinalization(store, operationId, paths, plan, accepted.ManifestSha256);
        }

        string output = paths.Require(request.ResultsDirectory);
        if (!store.UsesNativeProcessCheckpoints && File.Exists(Path.Combine(output, "run.json")))
        {
            throw new InvalidOperationException("run_directory_already_initialized");
        }

        store.ReserveResultsDirectory(operationId, output);
        _ = store.AuthorizeDispatch(operationId);

        // The runner persists the admitted batch before this callback binds its
        // exact bytes. No external process is started before confirmation.
        int exitCode = await ProcessWorkflowRunner.RunAsync(plan, accepted.ManifestSha256,
            paths, output, jobs, () => { _ = store.ConfirmRunnerCheckpoint(operationId, output); return Task.CompletedTask; }, cancellationToken: cancellationToken,
            persistence: store.UsesNativeProcessCheckpoints ? new(store, operationId) : null);
        if ((exitCode is 0 or 1 or 130) && store.GetDispatchCheckpoint(operationId) is not null)
        {
            await RecoveryFaults.HitAsync("process-before-finalization", null, CancellationToken.None);
            _ = store.FinalizeExecution(operationId, output, exitCode);
            await RecoveryFaults.HitAsync("process-after-finalization", null, CancellationToken.None);
        }

        return exitCode;
    }

    private static int RecoverFinalization(DurableAcceptanceStore store, string operationId, WorkspaceReadPaths paths,
        ProcessWorkflowManifest plan, string snapshotSha256)
    {
        if (store.GetFinalization(operationId) is { } terminal)
        {
            return terminal.ExitCode;
        }

        if (store.GetDispatchCheckpoint(operationId) is not { } dispatch)
        {
            return 4;
        }

        string directory = paths.Require(dispatch.RunDirectory);
        byte[] frame = store.ReadProcessCheckpoint(operationId, directory);
        ProcessOperationReport report = store.ProcessEvidenceCodec.Read(frame);
        int? exit = report.Status switch
        {
            "succeeded" => 0,
            "failed" => 1,
            "canceled" => 130,
            _ => null
        };
        if (exit is not { } exitCode)
        {
            return 4;
        }

        ProcessWorkflowRecovery.ValidatePlan(report, plan, snapshotSha256, paths, directory);
        store.VerifyProcessRetryCandidates(operationId, report);
        return store.FinalizeExecution(operationId, directory, exitCode).ExitCode;
    }
}
