using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.Client.Workflows;

/// <summary>Read-only domain-neutral projection backed by verified terminal evidence.</summary>
/// <remarks>
/// Before finalization, Nodes is empty and report-derived nullable fields are null; this does not mean the DAG is empty.
/// Even finalized reports may omit domain-specific environment/timing details.
/// </remarks>
/// <param name="SchemaVersion">Response schema version.</param>
/// <param name="Operation">Durable operation state projection.</param>
/// <param name="IsFinalized">Whether verified terminal evidence has been committed.</param>
/// <param name="RunDirectory">Host run directory, or null when no finalized report is exposed.</param>
/// <param name="ReportSha256">Finalized report SHA-256, or null before finalization.</param>
/// <param name="StartedAt">Reported start instant with offset, or null if unavailable.</param>
/// <param name="FinishedAt">Reported finish instant with offset, or null if unavailable.</param>
/// <param name="Error">Reported operation error, or null if absent/unavailable.</param>
/// <param name="HostId">Recorded execution Host identity, or null if not supplied by this domain.</param>
/// <param name="EnvironmentFingerprint">Recorded environment identity, or null if unavailable.</param>
/// <param name="Nodes">Non-null list of verified terminal node evidence; empty before finalization.</param>
[method: SuppressMessage("Design", "CA1002", Justification = "Client SDK constructor CMakeWorkflow.Client.Workflows.OperationOverview(Nodes) participates in CMakeWorkflow.Client.Workflows.OperationService.GetOverviewAsync; retain these concrete collection parameter types.")]
public sealed record OperationOverview(
    int SchemaVersion, OperationSnapshot Operation, bool IsFinalized,
    string? RunDirectory, string? ReportSha256,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error,
    string? HostId, string? EnvironmentFingerprint, [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.Workflows.OperationOverview.Nodes (List<OperationNodeView>) is exposed through CMakeWorkflow.Client.Workflows.OperationService.GetOverviewAsync; retain this Client SDK property signature.")]
List<OperationNodeView> Nodes);

/// <summary>Verified terminal node evidence; fields may be unavailable for nodes that never executed.</summary>
/// <param name="Id">Node identifier.</param>
/// <param name="Kind">Execution kind in the operation domain.</param>
/// <param name="Status">Recorded node outcome, including succeeded, failed, blocked or canceled.</param>
/// <param name="Dependencies">Non-null prerequisite IDs.</param>
/// <param name="StartedAt">Node start instant, or null if unavailable/not started.</param>
/// <param name="FinishedAt">Node finish instant, or null if unavailable/not finished.</param>
/// <param name="ExitCode">Native process exit code, or null if no process result exists.</param>
/// <param name="Error">Reported node error, or null if absent.</param>
/// <param name="Stdout">Host stdout log path, or null when not recorded; not inline output text.</param>
/// <param name="Stderr">Host stderr log path, or null when not recorded; not inline output text.</param>
[method: SuppressMessage("Design", "CA1002", Justification = "Client SDK constructor CMakeWorkflow.Client.Workflows.OperationNodeView(Dependencies) participates in CMakeWorkflow.Client.Workflows.OperationService.GetOverviewAsync; retain these concrete collection parameter types.")]
public sealed record OperationNodeView(
    string Id, string Kind, string Status, [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.Workflows.OperationNodeView.Dependencies (List<string>) is exposed through CMakeWorkflow.Client.Workflows.OperationService.GetOverviewAsync; retain this Client SDK property signature.")]
List<string> Dependencies,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int? ExitCode, string? Error, string? Stdout, string? Stderr);
