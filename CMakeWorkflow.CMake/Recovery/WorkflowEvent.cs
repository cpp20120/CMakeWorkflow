using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow;

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
