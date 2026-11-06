using System.Diagnostics.CodeAnalysis;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Contracts.Builders;
#pragma warning restore IDE0130

/// <summary>A typed reference usable as the source of a run or profile node.</summary>
public readonly record struct BuildNodeRef(string Id)
{
    [SuppressMessage("Usage", "CA2225", Justification = "Contracts SDK conversion BuildNodeRef -> WorkflowNodeRef supplies WorkflowNodeBuilder.DependsOn(params WorkflowNodeRef[]); callers in other languages can construct WorkflowNodeRef(value.Id).")]
    public static implicit operator WorkflowNodeRef(BuildNodeRef value)
    {
        return new(value.Id);
    }
}
