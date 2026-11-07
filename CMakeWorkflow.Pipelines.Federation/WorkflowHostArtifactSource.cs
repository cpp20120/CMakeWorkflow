using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Pipelines.Artifacts;

namespace CMakeWorkflow.Pipelines.Federation;

/// <summary>Host operation already opened in the correct workspace by the trusted caller.</summary>
/// <remarks>
/// HostIdentity is a stable caller-configured host + workspace scope, not an assertion authenticated by this class.
/// The operation ID is bound again against the pipeline ledger by the coordinator before any Host RPC.
/// </remarks>
public interface IWorkflowHostArtifactSource
{
    ArtifactOrigin Origin { get; }
    string HostIdentity { get; }
    string OperationId { get; }
    Task<OperationArtifactList> ListAsync(CancellationToken cancellationToken);
    Task<string> DownloadAsync(OperationArtifactInfo artifact, string destinationPath,
        CancellationToken cancellationToken);
}

/// <summary>Reuses the existing resumable, SHA-256-checked Host SDK for local and SSH connections.</summary>
public sealed class WorkflowHostArtifactSource(
    WorkflowOperation operation, ArtifactOrigin origin, string hostIdentity) : IWorkflowHostArtifactSource
{
    private readonly WorkflowOperation operation = operation ?? throw new ArgumentNullException(nameof(operation));

    public ArtifactOrigin Origin { get; } = origin is ArtifactOrigin.HostLocal or ArtifactOrigin.HostSsh
        ? origin : throw new ArgumentException("A Host source must be local or SSH.", nameof(origin));

    public string HostIdentity { get; } = !string.IsNullOrWhiteSpace(hostIdentity) &&
        hostIdentity.Length <= 1024 ? hostIdentity :
        throw new ArgumentException("A bounded host/workspace identity is required.", nameof(hostIdentity));

    public string OperationId => operation.OperationId;

    public Task<OperationArtifactList> ListAsync(CancellationToken cancellationToken) =>
        operation.ListArtifactsAsync(cancellationToken);

    public Task<string> DownloadAsync(OperationArtifactInfo artifact, string destinationPath,
        CancellationToken cancellationToken) =>
        operation.DownloadArtifactAsync(artifact, destinationPath, null, cancellationToken);
}
