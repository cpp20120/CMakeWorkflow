namespace CMakeWorkflow.Client.Workflows;

internal sealed record CancelRequest(string OperationId, long ExpectedRevision);
