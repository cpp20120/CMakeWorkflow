// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Contracts.Workflows;
#pragma warning restore IDE0130

/// <summary>Explicit profiler capability requirement for a profile node.</summary>
/// <remarks>
/// Backend and Mode must match the node options; backend auto is not a concrete requirement.
/// The profiling extension must verify the requested capability before capture.
/// </remarks>
public sealed class WorkflowProfilerRequirement
{
    /// <summary>Concrete backend identifier; must match the profile node backend option.</summary>
    public string Backend { get; set; } = "";
    /// <summary>Capture mode identifier; must match the profile node mode option.</summary>
    public string Mode { get; set; } = "";
    /// <summary>Required event source, or null for no explicit source requirement; empty is invalid.</summary>
    public string? EventSource { get; set; }
}
