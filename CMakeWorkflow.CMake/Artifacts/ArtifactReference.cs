#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed record ArtifactReference(
    string BuildNode,
    CMakeTargetRef Target,
    string Executable,
    string Sha256,
    string Materialization);
