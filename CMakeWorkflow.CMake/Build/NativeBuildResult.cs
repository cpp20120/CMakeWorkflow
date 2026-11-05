#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed record NativeBuildResult(
    CMakeTargetRef Target,
    string Verification,
    int? BackendExitCode,
    string? ObservationIndex,
    GenerationObservation? Generation = null);
