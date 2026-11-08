namespace CMakeWorkflow.Host.Workflows;

public sealed record OperationCancelResult(string OperationId, string State, long Revision, bool Requested);
