// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Contracts.Workflows;
#pragma warning restore IDE0130

/// <summary>Input evidence from a filesystem path or a named producer output.</summary>
/// <remarks>
/// At least Path or From must be supplied. From requires Artifact and adds a DAG dependency.
/// Artifact without From is invalid. Evidence is checked before execution; an optional hash constrains expected bytes.
/// </remarks>
public sealed class WorkflowArtifactInput
{
    /// <summary>Input path relative to the manifest or absolute; null uses the producer output path.</summary>
    public string? Path { get; set; }
    /// <summary>Expected SHA-256 as 64 hexadecimal characters, or null for no caller-supplied hash expectation.</summary>
    public string? Sha256 { get; set; }
    /// <summary>Producer node ID, or null for a filesystem-only input.</summary>
    public string? From { get; set; }
    /// <summary>Producer output name; required with From and otherwise null.</summary>
    public string? Artifact { get; set; }
    /// <summary>Whether to retain a verified input copy in the run directory.</summary>
    public bool Snapshot { get; set; }
}
