using System.Diagnostics.CodeAnalysis;

namespace CMakeWorkflow.Contracts.Builders;

/// <summary>A typed reference usable as the source of a run or profile node.</summary>
public readonly record struct BuildNodeRef(string Id)
{
    [SuppressMessage("Usage", "CA2225", Justification = "Contracts SDK conversion BuildNodeRef -> WorkflowNodeRef supplies WorkflowNodeBuilder.DependsOn(params WorkflowNodeRef[]); callers in other languages can construct WorkflowNodeRef(value.Id).")]
    public static implicit operator WorkflowNodeRef(BuildNodeRef value)
    {
        return new(value.Id);
    }
}
