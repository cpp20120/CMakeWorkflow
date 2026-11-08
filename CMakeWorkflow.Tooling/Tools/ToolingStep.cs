namespace CMakeWorkflow.Tooling.Tools;

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
