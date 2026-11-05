#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

public sealed record HostPreparationPlan(
    int SchemaVersion,
    string RequirementsSha256,
    string Source,
    ToolingPlan Tools,
    IReadOnlyList<HostFinding> Findings,
    bool Ready,
    string ApprovalSha256,
    IReadOnlyList<HostSettingChange>? SystemChanges = null,
    IReadOnlyList<string>? CpuPolicyPaths = null);
