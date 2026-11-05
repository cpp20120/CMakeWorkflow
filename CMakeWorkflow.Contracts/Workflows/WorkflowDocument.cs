using System.Diagnostics.CodeAnalysis;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed class WorkflowDocument
{
    public int SchemaVersion { get; set; }
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public Dictionary<string, WorkflowContext> Contexts { get; set; } = [];
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<WorkflowNode> Nodes { get; set; } = [];
}
