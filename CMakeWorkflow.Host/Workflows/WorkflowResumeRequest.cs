namespace CMakeWorkflow.Host.Workflows;

public sealed record WorkflowResumeRequest(string OperationId, long ExpectedRevision, bool ConfirmedQuiescent);
