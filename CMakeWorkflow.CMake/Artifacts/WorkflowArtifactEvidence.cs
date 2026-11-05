#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed record WorkflowArtifactEvidence(
    string Name,
    string Path,
    string Sha256,
    string Role,
    string? From = null,
    string? SnapshotPath = null);
