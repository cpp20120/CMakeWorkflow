namespace CMakeWorkflow.Contracts.Builders;

/// <summary>A reference to a previously declared node. It is not a running operation.</summary>
public readonly record struct WorkflowNodeRef(string Id);
