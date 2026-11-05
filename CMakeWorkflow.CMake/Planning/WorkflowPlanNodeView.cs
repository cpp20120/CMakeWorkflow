using System.Diagnostics.CodeAnalysis;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record WorkflowPlanNodeView(
    string Id,
    string Kind,
    string Target,
    string BuildDirectory,
    string? Configuration,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<string> Dependencies,
    string ObservedState,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<PlanDiagnostic> Diagnostics,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<string> Resources,
    bool Exclusive,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<WorkflowArtifactInput> Inputs,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<WorkflowArtifactOutput> Outputs,
    WorkflowExecutionConstraints? Constraints);
