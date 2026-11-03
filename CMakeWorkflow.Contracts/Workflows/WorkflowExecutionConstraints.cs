using System.Text.Json.Serialization;

namespace CMakeWorkflow;

public sealed class WorkflowExecutionConstraints
{
    public string? CpuSet { get; set; }
    public WorkflowProfilerRequirement? Profiler { get; set; }
}
