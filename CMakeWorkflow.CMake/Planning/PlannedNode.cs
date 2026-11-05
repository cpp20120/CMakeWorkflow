#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed record PlannedNode(
    WorkflowNode Definition,
    CMakeTargetRef Target,
    string WorkingDirectory,
    IReadOnlyList<string> Dependencies,
    RequestedBuildContext Requested,
    ObservedBuildContext Observed,
    IReadOnlyList<PlanDiagnostic> Diagnostics,
    WorkflowToolchainBinding? Toolchain = null);
