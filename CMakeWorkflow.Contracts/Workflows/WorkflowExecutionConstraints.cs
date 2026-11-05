#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed class WorkflowExecutionConstraints
{
    public string? CpuSet { get; set; }
    public WorkflowProfilerRequirement? Profiler { get; set; }
}
