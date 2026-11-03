using System.Text.Json.Serialization;

namespace CMakeWorkflow;

public sealed class WorkflowNode
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Context { get; set; }
    public string? Target { get; set; }
    public string? From { get; set; }
    public List<string> DependsOn { get; set; } = [];
    public List<string> Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> Environment { get; set; } = [];

    public Dictionary<string, string> Options { get; set; } = [];

    // Explicit output directory relative to the native build tree (invoke only).
    public string? HarnessDirectory { get; set; }
    public string? TestRegex { get; set; }
    public string? LabelRegex { get; set; }
    public List<string> Resources { get; set; } = [];
    public bool Exclusive { get; set; }

    public double? TimeoutSeconds { get; set; }

    // Optional schema-v2 evidence. A reference adds an ordinary DAG dependency.
    public List<WorkflowArtifactInput> Inputs { get; set; } = [];
    public List<WorkflowArtifactOutput> Outputs { get; set; } = [];
    public WorkflowExecutionConstraints? Constraints { get; set; }
}
