namespace CMakeWorkflow.Core;

/// <summary>Extension outcome awaiting runner integration and durable persistence.</summary>
/// <param name="ExitCode">Native/backend status code; zero conventionally indicates success.</param>
/// <param name="Status">Domain outcome string interpreted by the runner.</param>
/// <param name="Outputs">Non-null named artifact/output evidence supplied by the extension.</param>
/// <param name="Message">Optional diagnostic explanation; null when none is supplied.</param>
public sealed record WorkflowOperationResult(
    int ExitCode,
    string Status,
    IReadOnlyDictionary<string, string> Outputs,
    string? Message = null);
