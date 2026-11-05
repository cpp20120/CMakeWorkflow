namespace CMakeWorkflow.Client.Workflows;

internal sealed record ResumeRequest(string OperationId, long ExpectedRevision, bool ConfirmedQuiescent);
