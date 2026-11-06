// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Contracts.Workflows;
#pragma warning restore IDE0130

/// <summary>Optional execution requirements checked before external dispatch.</summary>
/// <remarks>
/// CPU affinity applies only to run/profile nodes; profiler requirements apply only to profile nodes.
/// Null means no corresponding requirement. Inspect nodes cannot request execution constraints.
/// </remarks>
public sealed class WorkflowExecutionConstraints
{
    /// <summary>Required CPU index/range expression, or null for no affinity requirement.</summary>
    public string? CpuSet { get; set; }
    /// <summary>Required profiler capabilities, or null for no explicit capability requirement.</summary>
    public WorkflowProfilerRequirement? Profiler { get; set; }
}
