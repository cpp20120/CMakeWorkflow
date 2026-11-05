#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public interface IWorkflowToolchainProvider
{
    Task<WorkflowToolchainRuntime> ResolveAsync(WorkflowToolchainBinding binding, CancellationToken ct);
}
