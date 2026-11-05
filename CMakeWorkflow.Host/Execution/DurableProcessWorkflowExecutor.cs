using System.Security.Cryptography;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

// The Host holds the same permanent authorization and execution lease as the
// CMake runner. Resume is explicit and guarded by the existing one-shot claim.
internal static class DurableProcessWorkflowExecutor
{
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
        string checkpoint = paths.Require(Path.Combine(output, "run.json"));
        paths.Read(checkpoint);
        byte[] bytes = await File.ReadAllBytesAsync(checkpoint, cancellationToken);
        OperationCheckpointEvidence evidence = ProcessCheckpointEvidenceCodec.Instance.ForResume(bytes);
        ProcessOperationReport report = ProcessCheckpointEvidenceCodec.Parse(bytes);
        if (report.Recovery!.Note != inspection.Note ||
            !string.Equals(evidence.ResumeSourceCheckpointSha256, CheckpointSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Process recovery checkpoint binding changed.");
        }

        ProcessWorkflowRecovery.ValidatePlan(report, plan, accepted.ManifestSha256, paths, output);
        string hash = ProcessWorkflowRecovery.Hash(bytes);
        // Runner holds the run lease while rechecking these exact bytes, claiming
        // the attempt, and only then changing state or starting any process.
        int code = await ProcessWorkflowRunner.RunAsync(plan, accepted.ManifestSha256, paths, output, 1,
() => { _ = store.ConfirmRunnerCheckpoint(operationId, output); return Task.CompletedTask; },
            resume: true, expectedCheckpointSha256: hash,
            claimResume: () => { _ = store.ClaimResumeAttempt(operationId, output, hash); return Task.CompletedTask; }, cancellationToken: cancellationToken);
        if (code is 0 or 1 or 130)
        {
            _ = store.FinalizeExecution(operationId, output, code);
        }

        return code;
    }

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
            accepted.State != "accepted_not_dispatched")
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

        string output = paths.Require(request.ResultsDirectory);
        if (File.Exists(Path.Combine(output, "run.json")))
        {
            throw new InvalidOperationException("run_directory_already_initialized");
        }

        store.ReserveResultsDirectory(operationId, output);
        _ = store.AuthorizeDispatch(operationId);

        // The runner persists the admitted batch before this callback binds its
        // exact bytes. No external process is started before confirmation.
        int exitCode = await ProcessWorkflowRunner.RunAsync(plan, accepted.ManifestSha256,
            paths, output, jobs, () => { _ = store.ConfirmRunnerCheckpoint(operationId, output); return Task.CompletedTask; }, cancellationToken: cancellationToken);
        if ((exitCode is 0 or 1 or 130) && store.GetDispatchCheckpoint(operationId) is not null)
        {
            _ = store.FinalizeExecution(operationId, output, exitCode);
        }

        return exitCode;
    }
}
