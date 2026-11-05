namespace CMakeWorkflow.Client.Workflows;

public sealed record CancellationReceipt(string OperationId, string State, long Revision, bool Requested);
