namespace CMakeWorkflow.CMake.Execution;

public sealed record WorkflowDispatchEvent(string Type, string Label, TimeSpan Elapsed);