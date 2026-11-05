#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed record WorkflowFinding(
    string Code,
    string Severity,
    string NodeId,
    string Message,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] SuggestedProfiles
#pragma warning restore CA1819
);
