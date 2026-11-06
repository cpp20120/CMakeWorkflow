using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.CMake.Planning;

public sealed record WorkflowPlanNodeView(
    string Id,
    string Kind,
    string Target,
    string BuildDirectory,
    string? Configuration,

List<string> Dependencies,
    string ObservedState,

List<PlanDiagnostic> Diagnostics,

List<string> Resources,
    bool Exclusive,

List<WorkflowArtifactInput> Inputs,

List<WorkflowArtifactOutput> Outputs,
    WorkflowExecutionConstraints? Constraints);
