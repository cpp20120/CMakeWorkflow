using CMakeWorkflow.Contracts.Workflows;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.CMake.Toolchains;
#pragma warning restore IDE0130

public interface IWorkflowToolchainProvider
{
    Task<WorkflowToolchainRuntime> ResolveAsync(WorkflowToolchainBinding binding, CancellationToken ct);
}
