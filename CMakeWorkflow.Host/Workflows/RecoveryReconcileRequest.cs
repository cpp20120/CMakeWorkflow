#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

public sealed record RecoveryReconcileRequest(
    string OperationId,
    long ExpectedRevision,
    string Decision,
    string Note,
    bool ConfirmedQuiescent);
