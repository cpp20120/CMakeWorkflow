namespace CMakeWorkflow.CMake.Build;

public sealed record NativeBuildResult(
    CMakeTargetRef Target,
    string Verification,
    int? BackendExitCode,
    string? ObservationIndex,
    GenerationObservation? Generation = null);
