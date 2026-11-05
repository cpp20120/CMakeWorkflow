namespace CMakeWorkflow.Client.Workflows;

internal sealed record EventsRequest(string OperationId, long AfterSequence, int Limit);
