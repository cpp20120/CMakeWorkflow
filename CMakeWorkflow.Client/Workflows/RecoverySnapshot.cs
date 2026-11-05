namespace CMakeWorkflow.Client.Workflows;

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
