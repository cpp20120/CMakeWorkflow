#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

// Compatibility entry point for workflow callers. CMake preparation and process
// execution are implemented in separate internal layers.
public static class WorkflowProcessDispatcher
{
    public static Task<int> RunAsync(NodeResult result,
        IReadOnlyDictionary<string, string> environment, string output, CancellationToken ct,
        Action<TimeSpan>? heartbeat = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(result);
        return CMakeProcessExecution.RunAsync(result, environment, output, heartbeat, ct: ct);
    }
}
