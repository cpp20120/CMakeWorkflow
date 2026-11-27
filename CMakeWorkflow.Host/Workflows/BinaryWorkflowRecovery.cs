using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Recovery;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Host.Sessions;

namespace CMakeWorkflow.Host.Workflows;

internal static class BinaryWorkflowRecovery
{
    // Caller holds the operation lease. The run lease serializes evidence
    // changes with the runner and local operator tooling.
    internal static async Task<DurableAcceptanceStore.RecoveryInspection> ReconcileRetryAsync(
        WorkspaceReadPaths paths, DurableAcceptanceStore store, RecoveryReconcileRequest request, CancellationToken ct)
    {
        if (request.Decision != "needs_attention" || string.IsNullOrWhiteSpace(request.RetryBinaryNode) ||
            string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 4096 ||
            request.ExpectedCheckpointSha256 is not { Length: 64 } expected || !expected.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Binary retry requires a node, the checkpoint SHA-256 and an operator reason.", nameof(request));
        }

        BinaryWorkflowInput input = store.ReadBinaryInput(request.OperationId)
            ?? throw new InvalidOperationException("Operation has no native binary input.");
        DurableAcceptanceStore.RecoveryInspection state = store.InspectRecovery(request.OperationId);
        if (state.TerminalState is not null || state.CancellationRequested || state.AcceptanceState != "dispatch_authorized" ||
            state.RunDirectory is null || !WorkflowPlanner.PathComparer.Equals(input.ResultsDirectory, state.RunDirectory) ||
            !string.Equals(state.CheckpointSha256, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("binary_retry_checkpoint_not_bound");
        }

        string run = paths.Require(state.RunDirectory);
        await using FileStream lease = new(paths.Require(Path.Combine(run, ".workflow-run.lock")), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        string file = paths.Require(Path.Combine(run, "run.json"));
        paths.Read(file);
        byte[] bytes = await File.ReadAllBytesAsync(file, ct);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        WorkflowReport report = JsonSerializer.Deserialize(bytes, WorkflowJson.Default.WorkflowReport)
            ?? throw new InvalidDataException("Invalid workflow checkpoint.");
        if (report.SchemaVersion != 1 || !WorkflowPlanner.PathComparer.Equals(report.Workflow, input.ManifestPath) ||
            !string.Equals(report.ManifestHash, input.Document.Blob.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Binary recovery checkpoint does not match admitted input.");
        }

        if (report.Status == "needs_attention" && report.Reconciliations.LastOrDefault() is { } prior &&
            prior.CheckpointSha256.Equals(expected, StringComparison.OrdinalIgnoreCase) &&
            prior.NodeId == request.RetryBinaryNode && prior.Reason == request.Note && prior.Decision == "retry")
        {
            return state is { Revision: 1, Decision: "needs_attention" } && state.Note == request.Note
                ? state
                : store.Reconcile(request.OperationId, request.ExpectedRevision, request.Decision, request.Note);
        }

        if (state.Revision != 0 || request.ExpectedRevision != 0 ||
            !hash.Equals(expected, StringComparison.OrdinalIgnoreCase) || report.Status != "running")
        {
            throw new InvalidOperationException("binary_retry_checkpoint_changed");
        }

        NodeResult[] pending = [.. report.Nodes.Where(node => node.Status == "running")];
        if (pending.Length != 1 || pending[0].Id != request.RetryBinaryNode)
        {
            throw new InvalidOperationException("Explicit binary retry requires exactly one matching running node.");
        }

        NodeResult node = pending[0];
        NodeResult? previous = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(node, WorkflowJson.Default.NodeResult),
            WorkflowJson.Default.NodeResult);
        report.Reconciliations.Add(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            Environment.UserName, Environment.MachineName, node.Id, "retry", request.Note, hash,
            report.Status, report.RecoveryPhase, previous, true));
        node.Status = "pending";
        node.Error = null;
        report.Status = "needs_attention";
        report.RecoveryPhase = "reconciled";
        report.Error = "Manually reconciled; explicit resume is required.";
        await WorkflowCheckpoint.SaveAsync(report, run, ct);
        return store.Reconcile(request.OperationId, request.ExpectedRevision, request.Decision, request.Note);
    }
}
