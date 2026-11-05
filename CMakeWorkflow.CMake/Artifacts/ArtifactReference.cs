using CMakeWorkflow.CMake.Build;

namespace CMakeWorkflow.CMake.Artifacts;

public sealed record ArtifactReference(
    string BuildNode,
    CMakeTargetRef Target,
    string Executable,
    string Sha256,
    string Materialization);
