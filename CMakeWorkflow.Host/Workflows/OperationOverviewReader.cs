using System.Text.Json;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.Host.Execution;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Sessions;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

/// <summary>Read-only domain projection. Never interprets an unfinalized run.json as evidence.</summary>
internal static class OperationOverviewReader
{
    /// <summary>Projects operation state without presenting unfinalized node reports as verified results.</summary>
    /// <remarks>
    /// Before finalization, returns no report-derived node/environment/timing evidence even if run.json exists.
    /// After finalization, delegates exact-byte verification to the domain reader; process dependencies come from
    /// the immutable accepted graph, including legacy reports without saved edges. No live execution lock is acquired.
    /// </remarks>
    internal static async Task<OperationOverview> ReadAsync(
        WorkspaceReadPaths paths, string operationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
        {
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        }

        ct.ThrowIfCancellationRequested();
        string database = Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "operations.sqlite");
        if (!File.Exists(database))
        {
            throw new KeyNotFoundException("Unknown operation.");
        }

        _ = paths.Database(database);
        OperationReference operation;
        bool finalized;
        using (DurableAcceptanceStore store = new(paths.WorkspaceDirectory, readOnly: true))
        {
            AcceptedWorkflow accepted = store.Get(operationId) ?? throw new KeyNotFoundException("Unknown operation.");
            operation = new OperationReference(accepted.WorkspaceId, accepted.OperationId,
                accepted.ClientRequestId, store.GetDomainKind(operationId), accepted.State,
                accepted.ManifestSha256, accepted.ManifestBaseDirectory);
            finalized = store.GetFinalization(operationId) is not null;
        }

        // A dispatch-authorized operation can have an incomplete or ambiguous report.
        // The view deliberately does not expose mutable checkpoint/node state.
        if (!finalized)
        {
            return new(1, operation, false, null, null, null, null, null, null, null, []);
        }

        if (operation.Kind == "workflow")
        {
            WorkflowReportSnapshot verified = await OperationArtifactReader.ReportAsync(paths, operationId, ct);
            return new(1, operation, true, verified.RunDirectory, verified.Revision,
                verified.Report.StartedAt, verified.Report.FinishedAt, verified.Report.Error,
                verified.Report.Environment?.HostId, verified.Report.Environment?.Fingerprint,
                [.. verified.Report.Nodes.Select(n => new OperationNodeView(n.Id, n.Kind, n.Status,
                    [.. n.Dependencies], n.StartedAt, n.FinishedAt,
                    n.ExitCode, n.Error, n.Stdout, n.Stderr))]);
        }
        if (operation.Kind == "process")
        {
            (ProcessOperationReport Report, string Directory, string Revision) verified = await ProcessOperationReportReader.ReadVerifiedAsync(paths, operationId, ct);
            // The immutable admission snapshot is the authority for DAG edges;
            // the terminal process report intentionally records outcomes only.
            using DurableAcceptanceStore store = new(paths.WorkspaceDirectory, readOnly: true);
            byte[] snapshot = store.GetSnapshot(operationId);
            ProcessWorkflowManifest plan = JsonSerializer.Deserialize(snapshot, ProcessWorkflowJson.Default.ProcessWorkflowManifest)
                ?? throw new ApplicationRefusal("report_changed", "Missing process operation plan.");
            if (plan.Domain != "process" || plan.SchemaVersion != 1 || plan.Nodes is null ||
                plan.Nodes.Count != verified.Report.Nodes.Count ||
                plan.Nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != plan.Nodes.Count)
            {
                throw new ApplicationRefusal("report_changed", "Process report does not match the accepted graph.");
            }

            Dictionary<string, ProcessWorkflowNode> declared = plan.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            return verified.Report.Nodes.Any(n => !declared.ContainsKey(n.Id)) ||
                verified.Report.Nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != declared.Count
                ? throw new ApplicationRefusal("report_changed", "Process report node identities differ from admission.")
                : new(1, operation, true, verified.Directory, verified.Revision,
                null, verified.Report.FinishedAt, verified.Report.Error, null, null,
                [.. verified.Report.Nodes.Select(n => new OperationNodeView(n.Id, "process", n.Status,
                    [.. declared[n.Id].Dependencies ?? []], n.StartedAt, n.FinishedAt,
                    n.ExitCode, n.Error, n.Stdout, n.Stderr))]);
        }
        throw new ApplicationRefusal("unknown_execution_domain", "Unsupported durable operation domain.");
    }
}
