#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed record CommandResult(
    int ExitCode,
    string Output,
    bool TimedOut,
    int? ProcessId = null,
    string? Stdout = null,
    string? Stderr = null);
