using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Host.Provisioning;

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
