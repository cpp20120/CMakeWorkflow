namespace CMakeWorkflow.CMake.Planning;

public sealed record WorkflowPlanView(
    int SchemaVersion,
    string Workflow,
    string ManifestHash,
    bool Valid,

List<WorkflowPlanNodeView> Nodes);
