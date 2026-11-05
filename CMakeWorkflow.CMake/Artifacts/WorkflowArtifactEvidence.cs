namespace CMakeWorkflow.CMake.Artifacts;

public sealed record WorkflowArtifactEvidence(
    string Name,
    string Path,
    string Sha256,
    string Role,
    string? From = null,
    string? SnapshotPath = null);
