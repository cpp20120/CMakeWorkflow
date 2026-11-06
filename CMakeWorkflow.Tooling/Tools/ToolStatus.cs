// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Tools;
#pragma warning restore IDE0130

public sealed record ToolStatus(
    string Id,
    string DisplayName,
    bool Installed,
    string? Command,
    string? Version,
    string? MinimumVersion,
    string? Diagnostic);
