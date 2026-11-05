#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

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
