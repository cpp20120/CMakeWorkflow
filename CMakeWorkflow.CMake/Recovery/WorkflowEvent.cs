using System.Diagnostics.CodeAnalysis;
using CMakeWorkflow.CMake.Artifacts;
namespace CMakeWorkflow.CMake.Recovery;

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
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
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<WorkflowArtifactView> Artifacts);
