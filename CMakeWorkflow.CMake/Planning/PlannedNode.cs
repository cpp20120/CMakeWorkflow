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
    WorkflowToolchainBinding? Toolchain = null);
