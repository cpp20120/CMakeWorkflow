using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Tooling.Environment;

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
