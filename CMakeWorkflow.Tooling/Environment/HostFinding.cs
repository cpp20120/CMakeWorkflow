namespace CMakeWorkflow.Tooling.Environment;

public sealed record HostFinding(
    string Id,
    string Status,
    string? Observed,
    string Expected,
    string? Diagnostic);
