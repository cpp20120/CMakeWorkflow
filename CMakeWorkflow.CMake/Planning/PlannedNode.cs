using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.CMake.Planning;

public sealed record PlannedNode(
    WorkflowNode Definition,
    CMakeTargetRef Target,
    string WorkingDirectory,
    IReadOnlyList<string> Dependencies,
    RequestedBuildContext Requested,
    ObservedBuildContext Observed,
    IReadOnlyList<PlanDiagnostic> Diagnostics,
    WorkflowToolchainBinding? Toolchain = null)
{
    public string? CompilerCacheProfile { get; init; }
    internal WorkflowVariantBinding? VariantBinding { get; init; }
    internal Action<string>? VariantReadGuard { get; init; }
}
