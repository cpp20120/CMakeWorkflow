namespace CMakeWorkflow.Host.Workflows;

public sealed record OperationEventsRequest(string OperationId, long AfterSequence = 0, int Limit = 100);
