using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.Client.Workflows;

/// <summary>Read-only, domain-neutral view of a durable operation.</summary>
/// <remarks>Nodes are verified terminal evidence only; an empty list on a nonfinalized
/// operation does not indicate that the execution DAG is empty.</remarks>
[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record OperationOverview(
    int SchemaVersion, OperationSnapshot Operation, bool IsFinalized,
    string? RunDirectory, string? ReportSha256,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error,
    string? HostId, string? EnvironmentFingerprint, [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<OperationNodeView> Nodes);

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record OperationNodeView(
    string Id, string Kind, string Status, [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<string> Dependencies,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int? ExitCode, string? Error, string? Stdout, string? Stderr);
