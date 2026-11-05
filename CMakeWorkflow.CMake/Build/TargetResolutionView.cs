namespace CMakeWorkflow.CMake.Build;

public sealed record TargetResolutionView(
    int SchemaVersion,
    string BuildDirectory,
    string Target,
    string? Configuration,
    string Executable);
