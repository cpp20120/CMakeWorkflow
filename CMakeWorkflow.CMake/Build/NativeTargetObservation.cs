namespace CMakeWorkflow.CMake.Build;

public sealed record NativeTargetObservation(
    string Name,
    string Configuration,
    string Type,
    string? Executable,
    string Evidence);
