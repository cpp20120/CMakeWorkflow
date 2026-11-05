#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed class WorkflowArtifactInput
{
    public string? Path { get; set; }
    public string? Sha256 { get; set; }
    public string? From { get; set; }
    public string? Artifact { get; set; }
    public bool Snapshot { get; set; }
}
