namespace CMakeWorkflow.CMake.Planning;

public sealed record WorkflowPlan(
    string File,
    string ManifestHash,
    IReadOnlyList<PlannedNode> Nodes);
