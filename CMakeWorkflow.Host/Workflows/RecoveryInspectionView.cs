#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

public sealed record RecoveryInspectionView(
    string OperationId,
    string AcceptanceState,
    string? TerminalState,
    bool CancellationRequested,
    string? RunDirectory,
    string? CheckpointSha256,
    string? Decision,
    long Revision,
    string? Note);
