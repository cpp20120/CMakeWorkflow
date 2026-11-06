namespace CMakeWorkflow.Client.Workflows;

/// <summary>Durable recovery evidence and operator decision for an operation.</summary>
/// <remarks>
/// Null fields indicate evidence or decisions not recorded; they do not prove absence of external effects.
/// Recovery revision is separate from cancellation-intent revision.
/// </remarks>
/// <param name="OperationId">Durable operation ID.</param>
/// <param name="AcceptanceState">Underlying accepted_not_dispatched or dispatch_authorized state.</param>
/// <param name="TerminalState">succeeded, failed or canceled after finalization; otherwise null.</param>
/// <param name="CancellationRequested">Whether durable stop intent exists; not proof of worker quiescence.</param>
/// <param name="RunDirectory">Bound Host run directory, or null before checkpoint binding.</param>
/// <param name="CheckpointSha256">Bound checkpoint SHA-256, or null when no checkpoint has been confirmed.</param>
/// <param name="Decision">needs_attention or outcome_unknown, or null before reconciliation.</param>
/// <param name="Revision">Recovery revision: 0 before reconciliation, 1 after it, 2 after resume admission.</param>
/// <param name="Note">Operator reconciliation rationale, or null before reconciliation.</param>
public sealed record RecoverySnapshot(
    string OperationId,
    string AcceptanceState,
    string? TerminalState,
    bool CancellationRequested,
    string? RunDirectory,
    string? CheckpointSha256,
    string? Decision,
    long Revision,
    string? Note);
