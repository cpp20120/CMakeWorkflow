// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

public sealed record OperationEventsRequest(string OperationId, long AfterSequence = 0, int Limit = 100);
