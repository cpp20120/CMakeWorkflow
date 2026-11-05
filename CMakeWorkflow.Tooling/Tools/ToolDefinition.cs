#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

public sealed record ToolDefinition(
    string Id,
    string DisplayName,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Commands
#pragma warning restore CA1819
,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] ProbeArguments
#pragma warning restore CA1819
,
    Version? MinimumVersion,
    IReadOnlyDictionary<PackageManagerKind, string[]> Packages,
    bool StandaloneCMake = false,
    string? ManualHint = null);
