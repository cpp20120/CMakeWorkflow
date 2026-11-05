namespace CMakeWorkflow.Client;

/// <summary>Read-only, domain-neutral view of a durable operation.</summary>
/// <remarks>Nodes are verified terminal evidence only; an empty list on a nonfinalized
/// operation does not indicate that the execution DAG is empty.</remarks>
public sealed record OperationOverview(
    int SchemaVersion, OperationSnapshot Operation, bool IsFinalized,
    string? RunDirectory, string? ReportSha256,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error,
    string? HostId, string? EnvironmentFingerprint, List<OperationNodeView> Nodes);

public sealed record OperationNodeView(
    string Id, string Kind, string Status, List<string> Dependencies,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int? ExitCode, string? Error, string? Stdout, string? Stderr);
