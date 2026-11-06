// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

public sealed record OperationCancelResult(string OperationId, string State, long Revision, bool Requested);
