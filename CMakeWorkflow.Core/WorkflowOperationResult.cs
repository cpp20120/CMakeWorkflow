namespace CMakeWorkflow;

public sealed record WorkflowOperationResult(
    int ExitCode,
    string Status,
    IReadOnlyDictionary<string, string> Outputs,
    string? Message = null);
