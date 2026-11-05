using System.Diagnostics.CodeAnalysis;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

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
