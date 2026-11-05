using System.Security.Cryptography;

namespace CMakeWorkflow.Host;

// The Host holds the same permanent authorization and execution lease as the
// CMake runner. This executor deliberately has no auto-resume implementation.
internal static class DurableProcessWorkflowExecutor
{
    internal static async Task<int> ExecuteAcceptedAsync(string workspace, string operationId,
        DurableImmutableRequest request, int jobs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = new WorkspaceReadPaths(workspace);
        var lockDirectory = Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "execution-locks");
        Directory.CreateDirectory(lockDirectory);
        if (operationId.Length != 32 || !operationId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        await using var lease = new FileStream(Path.Combine(lockDirectory, operationId + ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var store = new DurableAcceptanceStore(paths.WorkspaceDirectory, false,
            ProcessCheckpointEvidenceCodec.Instance);
        var accepted = store.Get(operationId) ?? throw new KeyNotFoundException("Unknown operation.");
        if (store.GetDomainKind(operationId) != "process" ||
            accepted.State != "accepted_not_dispatched")
            throw new InvalidOperationException("operation_already_authorized_or_wrong_domain");
        var verified = store.AcceptImmutable(request);
        if (verified.OperationId != operationId)
            throw new InvalidOperationException("operation_request_mismatch");
        var snapshot = store.GetSnapshot(operationId);
        var plan = ProcessWorkflowDomain.ReadPlan(snapshot, paths);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(snapshot)), accepted.ManifestSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Immutable process manifest changed.");
        var output = paths.Require(request.ResultsDirectory);
        if (File.Exists(Path.Combine(output, "run.json")))
            throw new InvalidOperationException("run_directory_already_initialized");
        store.ReserveResultsDirectory(operationId, output);
        store.AuthorizeDispatch(operationId);

        // The runner persists the admitted batch before this callback binds its
        // exact bytes. No external process is started before confirmation.
        var exitCode = await ProcessWorkflowRunner.RunAsync(plan, accepted.ManifestSha256,
            paths, output, jobs, cancellationToken,
            () => { store.ConfirmRunnerCheckpoint(operationId, output); return Task.CompletedTask; });
        if ((exitCode is 0 or 1 or 130) && store.GetDispatchCheckpoint(operationId) is not null)
            store.FinalizeExecution(operationId, output, exitCode);
        return exitCode;
    }
}
