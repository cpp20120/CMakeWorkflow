using CMakeWorkflow.Host.Execution;

namespace CMakeWorkflow.Host.Workflows;

// Composition boundary: each registered domain owns request semantics, executable
// behavior and checkpoint evidence. The coordinator still owns operation lifetimes.
/// <summary>Selects trusted execution-domain adapters without taking ownership of their lifetimes.</summary>
/// <remarks>
/// The coordinator owns scheduling and cancellation; each adapter owns request identity, executable behavior
/// and checkpoint interpretation. Unsupported domains fail before adapter dispatch.
/// </remarks>
internal static class HostOperationComposition
{
    /// <summary>Routes an already admitted recovery attempt to its domain executor.</summary>
    /// <remarks>
    /// Caller must have validated recovery admission; executor leases and permanent attempt claims still guard dispatch.
    /// </remarks>
    /// <param name="domain">Persisted domain: workflow or process.</param>
    /// <param name="workspace">Authorized Host workspace.</param>
    /// <param name="operationId">Accepted operation identity.</param>
    /// <param name="ct">Coordinator execution lifetime, independent of resume RPC cancellation.</param>
    internal static Task<int> ResumeAsync(string domain, string workspace, string operationId,
        CancellationToken ct) => domain switch
        {
            "workflow" => DurableWorkflowExecutor.ExecuteResumedAsync(workspace, operationId, ct),
            "process" => DurableProcessWorkflowExecutor.ExecuteResumedAsync(workspace, operationId, ct),
            _ => throw new InvalidOperationException("resume_unsupported_for_execution_domain")
        };

    /// <summary>Routes a workspace-authorized request through domain-specific durable admission.</summary>
    /// <remarks>
    /// cmake maps to the legacy workflow domain; process uses its own snapshot validator. A reference acknowledges
    /// acceptance only. No caller RPC token is passed into coordinator execution.
    /// </remarks>
    internal static OperationReference Start(DurableExecutionCoordinator coordinator,
        string workspace, ExecutionStartRequest request) => request.Domain switch
        {
            "cmake" => coordinator.Start(workspace, new WorkflowStartRequest(
                request.ClientRequestId, request.Manifest, request.ManifestSha256,
                request.Jobs, request.ResultsDirectory)),
            "process" => coordinator.StartProcess(workspace, request),
            _ => throw new InvalidOperationException("unknown_execution_domain")
        };
}
