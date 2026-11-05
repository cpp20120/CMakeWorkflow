#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

/// <summary>A reference to a previously declared node. It is not a running operation.</summary>
public readonly record struct WorkflowNodeRef(string Id);
