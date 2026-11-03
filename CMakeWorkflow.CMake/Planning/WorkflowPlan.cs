using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow;

public sealed record WorkflowPlan(
    string File,
    string ManifestHash,
    IReadOnlyList<PlannedNode> Nodes);
