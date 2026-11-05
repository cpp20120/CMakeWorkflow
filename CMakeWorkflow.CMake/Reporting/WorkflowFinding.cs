namespace CMakeWorkflow.CMake.Reporting;

public sealed record WorkflowFinding(
    string Code,
    string Severity,
    string NodeId,
    string Message,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] SuggestedProfiles
#pragma warning restore CA1819
);
