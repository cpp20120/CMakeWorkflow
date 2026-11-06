// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Contracts.Workflows;
#pragma warning restore IDE0130

/// <summary>Named output evidence captured after a node succeeds.</summary>
/// <remarks>
/// Names must be unique within the producer. Inspect nodes cannot declare outputs.
/// A declaration records and validates files; it does not make an external command transactional.
/// </remarks>
public sealed class WorkflowArtifactOutput
{
    /// <summary>Output identifier used by downstream Artifact references.</summary>
    public string Name { get; set; } = "";

    /// <summary>Output file path; run-rooted paths must be relative without traversal.</summary>
    public string Path { get; set; } = "";

    // manifest: relative to the manifest; run: relative to the current run directory.
    /// <summary>Path base: manifest (default) or run (the current results directory).</summary>
    public string Root { get; set; } = "manifest";
    /// <summary>Whether execution requires the declared output to be absent before the node starts.</summary>
    public bool Fresh { get; set; }
}
