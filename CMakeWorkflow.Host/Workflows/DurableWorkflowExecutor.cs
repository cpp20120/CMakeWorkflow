using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Execution;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Recovery;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.CMake.Toolchains;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Host.Toolchains;
using CMakeWorkflow.Profiling.Workflows;
using CMakeWorkflow.Storage.Documents;

namespace CMakeWorkflow.Host.Workflows;

/// <summary>Host-owned CMake execution bridge across durable acceptance and runner evidence.</summary>
/// <remarks>
/// The operation file lease covers validation, dispatch authorization, runner lifetime and finalization.
/// The runner also owns its run-directory lease. Cancellation is the coordinator execution token, not an RPC token.
/// Exceptions preserve unresolved state; only verified normal runner returns can finalize an operation.
/// </remarks>
public static class DurableWorkflowExecutor
{
    // Uses the original immutable snapshot and runner checkpoint recovery; a
    // committed attempt is never automatically re-claimed after an exception.
    /// <summary>Validates admitted recovery and permanently claims its attempt before runner mutation.</summary>
    /// <remarks>
    /// Uses original accepted bytes and bound run evidence, checks toolchain identity, and resumes with parallelism 1.
    /// The caller must have admitted recovery after quiescence confirmation. A committed attempt is never reclaimed on exception.
    /// Only verified terminal returns finalize; lease disposal does not erase uncertain results.
    /// </remarks>
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
        string? sourceId = store.GetSourceSnapshotId(operationId);
        (string runDirectory, _) = store.GetDispatchCheckpoint(operationId)
                    ?? throw new InvalidOperationException("resume_checkpoint_missing");
        string output = paths.Require(runDirectory);
        BinaryWorkflowInput? binary = store.ReadBinaryInput(operationId);
        WorkflowCheckpointPersistence? persistence = CheckpointPersistence(store, operationId, binary);
        WorkflowReport saved;
        string checkpointHash;
        if (persistence is not null)
        {
            BoundNativeCheckpoint checkpoint = store.ReadNativeCheckpoint(operationId);
            saved = checkpoint.State.Report;
            checkpointHash = checkpoint.Digest;
        }
        else
        {
            string checkpointPath = paths.Require(Path.Combine(output, "run.json"));
            byte[] checkpoint = await File.ReadAllBytesAsync(checkpointPath, cancellationToken);
            saved = JsonSerializer.Deserialize(checkpoint, WorkflowJson.Default.WorkflowReport)
                ?? throw new InvalidDataException("Invalid checkpoint.");
            checkpointHash = Convert.ToHexString(SHA256.HashData(checkpoint));
        }
        if (!string.Equals(saved.SourceSnapshotId, sourceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("source_snapshot_resume_binding_mismatch");
        }
        string manifest = paths.Require(saved.Workflow);
        WorkflowPlan plan = await LoadPlanAsync(paths, store, accepted, binary, manifest, output, sourceId,
            cancellationToken, resume: true);
        if (!string.Equals(plan.ManifestHash, accepted.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Resume snapshot mismatch.");
        }

        IReadOnlyDictionary<string, WorkflowToolchainRuntime> toolchains = await WorkflowToolchainAdmission.ResolveAsync(plan,
            new WorkflowToolchainProvider(sourceId, sourceId is null ? null :
                WorkflowSourceSnapshotExecution.SourcePath(paths.WorkspaceDirectory, operationId)), cancellationToken);
        foreach ((string? tree, WorkflowToolchainRuntime? selected) in toolchains)
        {
            WorkflowToolchainAdmission.ValidateBuildTree(tree, selected);
        }

        WorkflowToolchainAdmission.RequireSameIdentity(saved.Toolchains, toolchains);
        Dictionary<string, CompilerCacheProfile> cache = await CompilerCacheProfile.ResolveAsync(plan, cancellationToken);
        CompilerCacheProfile.RequireSameIdentity(plan, saved, cache);
        (StoredPlanAuthorization? planAuthorization, DocumentSession? storedPlan) = binary is null
            ? (null, null)
            : await store.VerifyExecutionPlanAsync(operationId,
                BinaryDispatchBinding.Encode(accepted, binary, plan, toolchains, cache, sourceId, store.UsesDocumentPlans),
                cancellationToken);
        using DocumentSession? planLease = storedPlan;
        // Permanent claim occurs before any runner mutation or external dispatch.
        _ = store.ClaimResumeAttempt(operationId, output, checkpointHash);
        DurableRunnerDispatchGate gate = new(paths.WorkspaceDirectory, operationId) { PlanAuthorization = planAuthorization };
        bool nativeEvents = binary is not null && store.UsesNativeWorkflowEvents;
        (int code, WorkflowReport? terminal) = await WorkflowRunner.RunWithEventJournalAsync(plan, 1, output, cancellationToken,
            extension: new ProfileWorkflowExecutor(recordNativeEvidence: binary is not null && store.UsesNativeWorkflowReports), resume: true,
            beforeExternalDispatch: gate.ConfirmAsync, toolchains: toolchains,
            sourceSnapshotId: sourceId, appendEvent: nativeEvents ? item => store.AppendWorkflowEvent(operationId, item) : null,
            previousEventSequence: nativeEvents ? store.LastWorkflowEventSequence(operationId) : 0,
            separateTerminalReport: binary is not null && store.UsesNativeWorkflowReports, checkpoint: persistence);
        if (code is 0 or 1 or 130)
        {
            await FinalizeAsync(store, accepted, binary, output, code, terminal);
        }

        return code;
    }

    /// <summary>Executes a still-unauthorized accepted snapshot once under an exclusive operation lease.</summary>
    /// <remarks>
    /// Validates immutable request identity, DAG diagnostics and toolchain identity before reserving results and authorizing dispatch.
    /// The runner must persist running intent and bind its checkpoint before external launch. After authorization, interruption
    /// cannot authorize implicit retry. Finalizes only runner return codes 0, 1 or 130; exceptions retain recovery evidence.
    /// </remarks>
    public static Task<int> ExecuteAcceptedAsync(string workspace, string operationId,
        string clientRequestId, string originalManifestPath, string expectedManifestHash,
        int jobs, string resultsDirectory, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? faultBarrier = null) =>
        ExecuteAcceptedAsync(workspace, operationId,
            new WorkflowStartRequest(clientRequestId, originalManifestPath, expectedManifestHash, jobs, resultsDirectory),
            null, faultBarrier, cancellationToken);

    internal static async Task<int> ExecuteAcceptedAsync(string workspace, string operationId,
        WorkflowStartRequest request, AcceptedWorkflow? admitted, Func<string, CancellationToken, Task>? faultBarrier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        (_, string originalManifestPath, _, int jobs, string resultsDirectory) = request;
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
        AcceptedWorkflow verified = admitted ?? store.Accept(request with { Manifest = manifest, ResultsDirectory = output });
        if (verified.OperationId != operationId || verified.RequestDigest != existing.RequestDigest ||
            verified.ManifestBaseDirectory != Path.GetDirectoryName(manifest))
        {
            throw new InvalidOperationException("operation_request_mismatch");
        }

        BinaryWorkflowInput? binary = store.ReadBinaryInput(operationId);
        if (binary is not null && binary.Jobs != jobs)
        {
            throw new InvalidDataException("Execution parallelism differs from binary admission.");
        }

        string? sourceId = store.GetSourceSnapshotId(operationId);
        WorkflowPlan plan = await LoadPlanAsync(paths, store, existing, binary, manifest, output, sourceId, cancellationToken);
        if (!string.Equals(plan.ManifestHash, existing.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Immutable snapshot hash changed.");
        }

        DurableAcceptanceStore.RequireDispatchablePlan(plan);

        if (Directory.Exists(output) && File.Exists(Path.Combine(output, "run.json")))
        {
            throw new InvalidOperationException("run_directory_already_initialized");
        }

        IReadOnlyDictionary<string, WorkflowToolchainRuntime> toolchains = await WorkflowToolchainAdmission.ResolveAsync(plan,
            new WorkflowToolchainProvider(sourceId, sourceId is null ? null :
                WorkflowSourceSnapshotExecution.SourcePath(paths.WorkspaceDirectory, operationId)), cancellationToken);
        foreach ((string? tree, WorkflowToolchainRuntime? selected) in toolchains)
        {
            WorkflowToolchainAdmission.ValidateBuildTree(tree, selected);
        }
        // Reserve before authorization so two distinct operations cannot use one
        // results tree, even if the first host dies before its runner starts.
        store.ReserveResultsDirectory(operationId, output);
        // From here onward, a crash must NOT permit a second implicit invocation.
        // The runner's write-ahead checkpoint is the next durable boundary.
        byte[]? binding = binary is null ? null : BinaryDispatchBinding.Encode(existing, binary, plan, toolchains,
            await CompilerCacheProfile.ResolveAsync(plan, cancellationToken), sourceId, store.UsesDocumentPlans);
        StoredPlanAuthorization? planAuthorization = binding is null ? null
            : await store.PublishExecutionPlanAsync(operationId, binding, cancellationToken);
        _ = store.AuthorizeDispatch(operationId, binary, binding, planAuthorization);
        if (faultBarrier is not null)
        {
            await faultBarrier("authorized", cancellationToken);
        }

        DurableRunnerDispatchGate gate = new(paths.WorkspaceDirectory, operationId) { PlanAuthorization = planAuthorization };
        (int exitCode, WorkflowReport? terminal) = await WorkflowRunner.RunWithEventJournalAsync(plan, jobs, output, cancellationToken,
            extension: new ProfileWorkflowExecutor(recordNativeEvidence: binary is not null && store.UsesNativeWorkflowReports), toolchains: toolchains,
            sourceSnapshotId: sourceId,
            appendEvent: binary is not null && store.UsesNativeWorkflowEvents
                ? item => store.AppendWorkflowEvent(operationId, item) : null,
            beforeExternalDispatch: async (checkpoint, ct) =>
            {
                await gate.ConfirmAsync(checkpoint, ct);
                if (faultBarrier is not null)
                {
                    await faultBarrier("checkpoint", ct);
                }
            }, separateTerminalReport: binary is not null && store.UsesNativeWorkflowReports,
            checkpoint: CheckpointPersistence(store, operationId, binary));
        // No finalization from catch/finally: a crash leaves the outcome unresolved.
        if (exitCode is 0 or 1 or 130)
        {
            await FinalizeAsync(store, existing, binary, output, exitCode, terminal);
        }

        return exitCode;
    }

    private static WorkflowCheckpointPersistence? CheckpointPersistence(DurableAcceptanceStore store, string operation,
        BinaryWorkflowInput? binary) =>
        binary is not null && store.UsesNativeWorkflowCheckpoints
            ? new(() => store.ReadNativeCheckpoint(operation).State.Report, report => store.SaveNativeCheckpoint(operation, report))
            : null;

    private static async Task FinalizeAsync(DurableAcceptanceStore store, AcceptedWorkflow accepted,
        BinaryWorkflowInput? binary, string output, int exitCode, WorkflowReport? terminal)
    {
        if (binary is not null && store.UsesNativeWorkflowReports)
        {
            await store.FinalizeNativeExecutionAsync(accepted, binary, output, exitCode,
                terminal ?? throw new InvalidDataException("Runner did not return terminal evidence."), CancellationToken.None);
            return;
        }

        _ = store.FinalizeExecution(accepted.OperationId, output, exitCode);
    }

    private static async Task<WorkflowPlan> LoadPlanAsync(WorkspaceReadPaths paths, DurableAcceptanceStore store,
        AcceptedWorkflow accepted, BinaryWorkflowInput? binary, string manifest, string output, string? sourceId,
        CancellationToken ct, bool resume = false)
    {
        IReadOnlyList<string> selection = store.GetWorkflowSelection(accepted.OperationId);
        if (binary is null)
        {
            byte[] snapshot = store.GetSnapshot(accepted.OperationId);
            return sourceId is null
                ? await WorkflowPlanner.LoadSnapshotAsync(manifest, snapshot, selection, null, ct, paths.Read)
                : await WorkflowSourceSnapshotExecution.PlanAsync(paths, accepted.OperationId, manifest, output,
                    snapshot, sourceId, selection, ct, resume);
        }

        if (!WorkflowPlanner.PathComparer.Equals(manifest, binary.ManifestPath) ||
            !WorkflowPlanner.PathComparer.Equals(output, binary.ResultsDirectory) ||
            !string.Equals(accepted.ManifestSha256, binary.Document.Blob.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Binary execution context differs from its admitted input.");
        }

        using DocumentSession session = await BinaryWorkflowSubmission.OpenVerifiedAsync(store, binary.Document, ct);
        WorkflowDocument document = session.Materialize();
        return sourceId is null
            ? await WorkflowPlanner.LoadDocumentAsync(manifest, document, accepted.ManifestSha256, selection, null, ct, paths.Read)
            : await WorkflowSourceSnapshotExecution.PlanAsync(paths, accepted.OperationId, manifest, output,
                document, accepted.ManifestSha256, sourceId, selection, ct, resume, store.AllowsSourceManifestArtifacts);
    }

}
