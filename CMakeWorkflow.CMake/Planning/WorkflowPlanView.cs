using System.Diagnostics.CodeAnalysis;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record WorkflowPlanView(
    int SchemaVersion,
    string Workflow,
    string ManifestHash,
    bool Valid,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<WorkflowPlanNodeView> Nodes);
