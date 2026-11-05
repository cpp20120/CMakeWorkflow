namespace CMakeWorkflow.CMake.Build;

public sealed record CMakeTargetRef(string BuildDirectory, string Target, string? Configuration);
