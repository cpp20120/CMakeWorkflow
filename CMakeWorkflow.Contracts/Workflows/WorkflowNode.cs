using System.Diagnostics.CodeAnalysis;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed class WorkflowNode
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Context { get; set; }
    public string? Target { get; set; }
    public string? From { get; set; }
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<string> DependsOn { get; set; } = [];
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<string> Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public Dictionary<string, string> Environment { get; set; } = [];

    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public Dictionary<string, string> Options { get; set; } = [];

    // Explicit output directory relative to the native build tree (invoke only).
    public string? HarnessDirectory { get; set; }
    public string? TestRegex { get; set; }
    public string? LabelRegex { get; set; }
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<string> Resources { get; set; } = [];
    public bool Exclusive { get; set; }

    public double? TimeoutSeconds { get; set; }

    // Optional schema-v2 evidence. A reference adds an ordinary DAG dependency.
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<WorkflowArtifactInput> Inputs { get; set; } = [];
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<WorkflowArtifactOutput> Outputs { get; set; } = [];
    public WorkflowExecutionConstraints? Constraints { get; set; }
}
