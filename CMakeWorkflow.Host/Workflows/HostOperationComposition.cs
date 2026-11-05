namespace CMakeWorkflow.Host;

// Composition boundary: each registered domain owns request semantics, executable
// behavior and checkpoint evidence. The coordinator still owns operation lifetimes.
internal static class HostOperationComposition
{
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
