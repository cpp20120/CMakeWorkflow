using CMakeWorkflow.CMake.Artifacts;
namespace CMakeWorkflow.CMake.Recovery;

public sealed record WorkflowEvent(
    int SchemaVersion,
    long Sequence,
    DateTimeOffset Timestamp,
    string Type,
    string RunDirectory,
    string? NodeId,
    string? NodeKind,
    string? Status,
    int? ExitCode,
    string? Error,

List<WorkflowArtifactView> Artifacts);
