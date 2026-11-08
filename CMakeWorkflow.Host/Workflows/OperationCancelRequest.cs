namespace CMakeWorkflow.Host.Workflows;

public sealed record OperationCancelRequest(string OperationId, long ExpectedRevision);
