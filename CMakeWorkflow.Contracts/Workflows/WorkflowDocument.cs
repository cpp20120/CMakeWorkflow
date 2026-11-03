using System.Text.Json.Serialization;

namespace CMakeWorkflow;

public sealed class WorkflowDocument
{
    public int SchemaVersion { get; set; }
    public Dictionary<string, WorkflowContext> Contexts { get; set; } = [];
    public List<WorkflowNode> Nodes { get; set; } = [];
}
