#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

internal sealed record ReconcileRequest(
    string OperationId,
    long ExpectedRevision,
    string Decision,
    string Note,
    bool ConfirmedQuiescent);
