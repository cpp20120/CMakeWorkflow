// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Provisioning;
#pragma warning restore IDE0130

public sealed record ApplyFixRequest(string OperationId, string PlanId);
