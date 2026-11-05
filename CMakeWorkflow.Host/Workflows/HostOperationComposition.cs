#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

// Composition boundary: each registered domain owns request semantics, executable
// behavior and checkpoint evidence. The coordinator still owns operation lifetimes.
internal static class HostOperationComposition
{
    internal static Task<int> ResumeAsync(string domain, string workspace, string operationId,
        CancellationToken ct) => domain switch
        {
            "workflow" => DurableWorkflowExecutor.ExecuteResumedAsync(workspace, operationId, ct),
            "process" => DurableProcessWorkflowExecutor.ExecuteResumedAsync(workspace, operationId, ct),
            _ => throw new InvalidOperationException("resume_unsupported_for_execution_domain")
        };

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
