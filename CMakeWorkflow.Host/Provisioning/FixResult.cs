using CMakeWorkflow.Tooling;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

public sealed record FixResult(
    int SchemaVersion,
    string Status,
    ToolingResult? Installation,
    ToolingPlan? Verification,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Diagnostics
#pragma warning restore CA1819
,
    bool ReadyToResume = false);
