namespace CMakeWorkflow.CMake.Build;

public sealed record BuiltArtifact(CMakeTargetRef TargetRef, string ExecutablePath);
