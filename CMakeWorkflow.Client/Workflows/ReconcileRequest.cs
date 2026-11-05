namespace CMakeWorkflow.Client.Workflows;

internal sealed record ReconcileRequest(
    string OperationId,
    long ExpectedRevision,
    string Decision,
    string Note,
    bool ConfirmedQuiescent);
