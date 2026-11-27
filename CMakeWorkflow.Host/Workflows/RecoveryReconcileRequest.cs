namespace CMakeWorkflow.Host.Workflows;

public sealed record RecoveryReconcileRequest(
    string OperationId,
    long ExpectedRevision,
    string Decision,
    string Note,
    bool ConfirmedQuiescent)
{
    public string? RetryBinaryNode { get; init; }
    public string? ExpectedCheckpointSha256 { get; init; }
}
