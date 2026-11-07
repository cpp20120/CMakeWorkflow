using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Pipelines.Persistence;

namespace CMakeWorkflow.Pipelines.Coordinator;

/// <summary>Adapter from the pipeline coordinator to an already initialized, persistent CMakeWorkflow Host.</summary>
/// <remarks>The client connection is borrowed. Caller owns reconnection and shutdown.</remarks>
public sealed class ClientWorkflowChildHost(CMakeWorkflowClient client) : IWorkflowChildHost
{
    private readonly CMakeWorkflowClient client = client ?? throw new ArgumentNullException(nameof(client));

    public string WorkspaceDirectory => client.Host.WorkspaceDirectory;

    public async Task ValidatePreflightAsync(PipelineChildLaunch launch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        WorkflowPreflightResult result = await client.Workflows.PreflightAsync(
            new WorkflowPreflightOptions(launch.Manifest, launch.ManifestSha256, launch.Jobs,
                launch.ResultsDirectory), cancellationToken);
        if (result.SchemaVersion != 1 || !result.Valid ||
            result.Manifest != launch.Manifest || result.Jobs != launch.Jobs ||
            result.ResultsDirectory != launch.ResultsDirectory ||
            !string.Equals(result.ManifestSha256, launch.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("host_workflow_preflight_not_equivalent");
        }
    }

    public async Task<WorkflowChildIdentity> StartAsync(PipelineChildLaunch launch, string childRequestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentException.ThrowIfNullOrWhiteSpace(childRequestId);
        WorkflowOperation accepted = await client.Workflows.StartAsync(
            new StartWorkflowOptions(childRequestId, launch.Manifest, launch.ManifestSha256,
                launch.Jobs, launch.ResultsDirectory), cancellationToken);
        OperationSnapshot snapshot = await accepted.GetSnapshotAsync(cancellationToken);
        return ToIdentity(snapshot);
    }

    public async Task<WorkflowChildIdentity> FindAsync(string childRequestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childRequestId);
        OperationSnapshot snapshot = await client.Operations.FindAsync(childRequestId, cancellationToken);
        return ToIdentity(snapshot);
    }

    public async Task<WorkflowChildProgress> ObserveAsync(string childOperationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childOperationId);
        OperationOverview overview = await client.Operations.GetOverviewAsync(childOperationId, cancellationToken);
        return new(ToIdentity(overview.Operation), overview.Operation.State,
            overview.IsFinalized, overview.ReportSha256);
    }

    private static WorkflowChildIdentity ToIdentity(OperationSnapshot snapshot) =>
        new(snapshot.OperationId, snapshot.ClientRequestId, snapshot.Kind, snapshot.ManifestSha256);
}
