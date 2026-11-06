// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Tools;
#pragma warning restore IDE0130

public sealed record ToolingResult(
    int SchemaVersion,
    bool Success,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Ready
#pragma warning restore CA1819
,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Failed
#pragma warning restore CA1819
,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Diagnostics
#pragma warning restore CA1819
);
