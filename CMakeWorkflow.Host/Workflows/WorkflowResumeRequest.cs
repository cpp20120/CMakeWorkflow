// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

public sealed record WorkflowResumeRequest(string OperationId, long ExpectedRevision, bool ConfirmedQuiescent);
