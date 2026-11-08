namespace CMakeWorkflow.Host.Workflows;

public sealed record RecoveryReconcileRequest(
    string OperationId,
    long ExpectedRevision,
    string Decision,
    string Note,
    bool ConfirmedQuiescent);
