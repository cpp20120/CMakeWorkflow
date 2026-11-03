using System.Text.Json.Serialization;

namespace CMakeWorkflow;

public sealed class WorkflowProfilerRequirement
{
    public string Backend { get; set; } = "";
    public string Mode { get; set; } = "";
    public string? EventSource { get; set; }
}
