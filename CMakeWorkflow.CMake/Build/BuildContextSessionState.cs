namespace CMakeWorkflow.CMake.Build;

public sealed record BuildContextSessionState(
    string Mode,
    string Policy,
    string Provenance,
    GenerationObservation Generation,
    string GenerationChange,
    bool ObservedPropertiesChanged,
    bool RequiresConfigure);
