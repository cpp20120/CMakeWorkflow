using System.Diagnostics.CodeAnalysis;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Contracts.Workflows;
#pragma warning restore IDE0130

/// <summary>Mutable serialized workflow definition; construction alone does not validate or execute it.</summary>
/// <remarks>
/// Schema versions 1 and 2 are supported. Context keys and node IDs are case-sensitive.
/// The planner validates the whole DAG, including unselected branches; dependencies must exist and be acyclic.
/// Collections and their elements must remain non-null. Paths are interpreted on the execution host.
/// </remarks>
public sealed class WorkflowDocument
{
    /// <summary>Manifest schema version: 1 or 2; the default zero is invalid.</summary>
    public int SchemaVersion { get; set; }
    /// <summary>Non-null map of context identifiers to build-tree specifications.</summary>
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public Dictionary<string, WorkflowContext> Contexts { get; set; } = [];
    /// <summary>Non-null node list; declaration order is not a substitute for DAG dependencies.</summary>
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<WorkflowNode> Nodes { get; set; } = [];
}
