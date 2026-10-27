namespace CMakeWorkflow;

// Optional operation executed by the existing workflow DAG. The runner verifies
// the built executable before calling this interface; backends own capture.
public sealed record WorkflowOperationRequest(
    string Kind,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> Options,
    string OutputDirectory,
    string NodeId);

public sealed record WorkflowOperationResult(
    int ExitCode,
    string Status,
    IReadOnlyDictionary<string, string> Outputs,
    string? Message = null);

public interface IWorkflowOperationExecutor
{
    bool Supports(string kind);
    Task<WorkflowOperationResult> ExecuteAsync(
        WorkflowOperationRequest request, CancellationToken cancellationToken);
}
