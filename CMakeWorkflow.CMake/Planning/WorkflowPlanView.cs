using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.CMake.Planning;

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record WorkflowPlanView(
    int SchemaVersion,
    string Workflow,
    string ManifestHash,
    bool Valid,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<WorkflowPlanNodeView> Nodes);
