using System.Text.Json;

namespace CMakeWorkflow.Host;

/// <summary>Read-only domain projection. Never interprets an unfinalized run.json as evidence.</summary>
internal static class OperationOverviewReader
{
    internal static async Task<OperationOverview> ReadAsync(
        WorkspaceReadPaths paths, string operationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        ct.ThrowIfCancellationRequested();
        var database = Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "operations.sqlite");
        if (!File.Exists(database)) throw new KeyNotFoundException("Unknown operation.");
        paths.Database(database);
        OperationReference operation;
        bool finalized;
        using (var store = new DurableAcceptanceStore(paths.WorkspaceDirectory, readOnly: true))
        {
            var accepted = store.Get(operationId) ?? throw new KeyNotFoundException("Unknown operation.");
            operation = new OperationReference(accepted.WorkspaceId, accepted.OperationId,
                accepted.ClientRequestId, store.GetDomainKind(operationId), accepted.State,
                accepted.ManifestSha256, accepted.ManifestBaseDirectory);
            finalized = store.GetFinalization(operationId) is not null;
        }

        // A dispatch-authorized operation can have an incomplete or ambiguous report.
        // The view deliberately does not expose mutable checkpoint/node state.
        if (!finalized)
            return new(1, operation, false, null, null, null, null, null, null, null, []);

        if (operation.Kind == "workflow")
        {
            var verified = await OperationArtifactReader.ReportAsync(paths, operationId, ct);
            return new(1, operation, true, verified.RunDirectory, verified.Revision,
                verified.Report.StartedAt, verified.Report.FinishedAt, verified.Report.Error,
                verified.Report.Environment?.HostId, verified.Report.Environment?.Fingerprint,
                verified.Report.Nodes.Select(n => new OperationNodeView(n.Id, n.Kind, n.Status,
                    n.Dependencies.ToList(), n.StartedAt, n.FinishedAt,
                    n.ExitCode, n.Error, n.Stdout, n.Stderr)).ToList());
        }
        if (operation.Kind == "process")
        {
            var verified = await ProcessOperationReportReader.ReadVerifiedAsync(paths, operationId, ct);
            // The immutable admission snapshot is the authority for DAG edges;
            // the terminal process report intentionally records outcomes only.
            using var store = new DurableAcceptanceStore(paths.WorkspaceDirectory, readOnly: true);
            var snapshot = store.GetSnapshot(operationId);
            var plan = JsonSerializer.Deserialize(snapshot, ProcessWorkflowJson.Default.ProcessWorkflowManifest)
                ?? throw new ApplicationRefusal("report_changed", "Missing process operation plan.");
            if (plan.Domain != "process" || plan.SchemaVersion != 1 || plan.Nodes is null ||
                plan.Nodes.Count != verified.Report.Nodes.Count ||
                plan.Nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != plan.Nodes.Count)
                throw new ApplicationRefusal("report_changed", "Process report does not match the accepted graph.");
            var declared = plan.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            if (verified.Report.Nodes.Any(n => !declared.ContainsKey(n.Id)) ||
                verified.Report.Nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != declared.Count)
                throw new ApplicationRefusal("report_changed", "Process report node identities differ from admission.");
            return new(1, operation, true, verified.Directory, verified.Revision,
                null, verified.Report.FinishedAt, verified.Report.Error, null, null,
                verified.Report.Nodes.Select(n => new OperationNodeView(n.Id, "process", n.Status,
                    (declared[n.Id].Dependencies ?? []).ToList(), n.StartedAt, n.FinishedAt,
                    n.ExitCode, n.Error, n.Stdout, n.Stderr)).ToList());
        }
        throw new ApplicationRefusal("unknown_execution_domain", "Unsupported durable operation domain.");
    }
}
