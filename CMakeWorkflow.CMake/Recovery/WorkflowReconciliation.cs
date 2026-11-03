using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

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
