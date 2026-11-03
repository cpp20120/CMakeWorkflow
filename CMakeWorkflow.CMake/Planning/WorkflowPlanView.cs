using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow;

public sealed record WorkflowPlanView(
    int SchemaVersion,
    string Workflow,
    string ManifestHash,
    bool Valid,
    List<WorkflowPlanNodeView> Nodes);
