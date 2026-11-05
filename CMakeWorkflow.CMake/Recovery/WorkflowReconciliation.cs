#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed record WorkflowReconciliation(
    string Id,
    DateTimeOffset Timestamp,
    string Actor,
    string Host,
    string? NodeId,
    string Decision,
    string Reason,
    string CheckpointSha256,
    string PreviousRunStatus,
    string? PreviousRecoveryPhase,
    NodeResult? PreviousNode,
    bool ProcessesConfirmedStopped);
