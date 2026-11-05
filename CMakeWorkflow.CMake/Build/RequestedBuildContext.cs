namespace CMakeWorkflow.CMake.Build;

public sealed record RequestedBuildContext(
    string BuildDirectory,
    string? Configuration = null,
    string? ConfigurePreset = null,
    string? SourceDirectory = null,
    string ProvenancePolicy = "strict",
    string Mode = "legacy",
    string? Generator = null);
