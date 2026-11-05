namespace CMakeWorkflow;

// Compatibility entry point for workflow callers. CMake preparation and process
// execution are implemented in separate internal layers.
public static class WorkflowProcessDispatcher
{
    public static Task<int> RunAsync(NodeResult result,
        IReadOnlyDictionary<string, string> environment, string output, CancellationToken ct,
        Action<TimeSpan>? heartbeat = null) =>
        CMakeProcessExecution.RunAsync(result, environment, output, ct, heartbeat);
}
