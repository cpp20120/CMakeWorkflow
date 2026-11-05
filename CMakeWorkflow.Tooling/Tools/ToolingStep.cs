#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

public sealed record ToolingStep(
    string Tool,
    string Method,
    string? Executable,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Arguments
#pragma warning restore CA1819
,
    string? Source,
    string? Diagnostic);
