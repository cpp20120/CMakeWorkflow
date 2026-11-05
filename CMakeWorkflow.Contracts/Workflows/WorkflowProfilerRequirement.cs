#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed class WorkflowProfilerRequirement
{
    public string Backend { get; set; } = "";
    public string Mode { get; set; } = "";
    public string? EventSource { get; set; }
}
