namespace CMakeWorkflow.Core;

public sealed record CommandResult(
    int ExitCode,
    string Output,
    bool TimedOut,
    int? ProcessId = null,
    string? Stdout = null,
    string? Stderr = null);
