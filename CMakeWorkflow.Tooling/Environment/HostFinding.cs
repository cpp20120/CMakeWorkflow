// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Environment;
#pragma warning restore IDE0130

public sealed record HostFinding(
    string Id,
    string Status,
    string? Observed,
    string Expected,
    string? Diagnostic);
