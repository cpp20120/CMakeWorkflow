namespace CMakeWorkflow.Tooling.Tools;

public sealed record ToolStatus(
    string Id,
    string DisplayName,
    bool Installed,
    string? Command,
    string? Version,
    string? MinimumVersion,
    string? Diagnostic);
