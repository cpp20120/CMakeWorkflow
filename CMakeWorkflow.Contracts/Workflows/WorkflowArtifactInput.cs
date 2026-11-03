using System.Text.Json.Serialization;

namespace CMakeWorkflow;

public sealed class WorkflowArtifactInput
{
    public string? Path { get; set; }
    public string? Sha256 { get; set; }
    public string? From { get; set; }
    public string? Artifact { get; set; }
    public bool Snapshot { get; set; }
}
