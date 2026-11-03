namespace CMakeWorkflow;

/// <summary>A typed reference usable as the source of a run or profile node.</summary>
public readonly record struct BuildNodeRef(string Id)
{
    public static implicit operator WorkflowNodeRef(BuildNodeRef value) => new(value.Id);
}
