namespace CMakeWorkflow.CMake.Artifacts;

public sealed record WorkflowArtifactView(string Type, string Path, string? Sha256);
