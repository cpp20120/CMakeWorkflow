namespace CMakeWorkflow.CMake.Toolchains;

public sealed record WorkflowToolSelection(string Origin, string? Version, string? ArtifactSha256, string BinarySha256);
