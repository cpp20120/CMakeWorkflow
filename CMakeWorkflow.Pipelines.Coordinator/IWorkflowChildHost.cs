using CMakeWorkflow.Pipelines.Persistence;

namespace CMakeWorkflow.Pipelines.Coordinator;

/// <summary>Read-only or explicitly requested Host operations; never retries a failed start.</summary>
/// <remarks>
/// Implementations must use a persistent Host scoped to WorkspaceDirectory. A missing Find result must
/// be reported as failure, not permission to dispatch. Each invocation is one RPC; no automatic reconnect.
/// </remarks>
public interface IWorkflowChildHost
{
    string WorkspaceDirectory { get; }

    /// <summary>Validate the exact Host manifest and proposed launch; this call must not dispatch.</summary>
    Task ValidatePreflightAsync(PipelineChildLaunch launch, CancellationToken cancellationToken = default);

    /// <summary>One durable Host admission invocation after ledger reservation.</summary>
    Task<WorkflowChildIdentity> StartAsync(PipelineChildLaunch launch, string childRequestId,
        CancellationToken cancellationToken = default);

    /// <summary>Read-only durable lookup by the original childRequestId, never a submission.</summary>
    Task<WorkflowChildIdentity> FindAsync(string childRequestId, CancellationToken cancellationToken = default);

    /// <summary>Read-only verified terminal overview; null report hash means not finalized.</summary>
    Task<WorkflowChildProgress> ObserveAsync(string childOperationId, CancellationToken cancellationToken = default);
}

/// <summary>Identity returned by the Host's durable acceptance or lookup, not proof of execution.</summary>
public sealed record WorkflowChildIdentity(
    string OperationId, string ClientRequestId, string Kind, string ManifestSha256);

/// <summary>Host read projection; final report SHA-256 is authoritative only when IsFinalized is true.</summary>
public sealed record WorkflowChildProgress(
    WorkflowChildIdentity Identity, string State, bool IsFinalized, string? ReportSha256);
