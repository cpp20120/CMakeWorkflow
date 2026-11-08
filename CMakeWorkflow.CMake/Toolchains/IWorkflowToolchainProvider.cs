using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.CMake.Toolchains;

public interface IWorkflowToolchainProvider
{
    Task<WorkflowToolchainRuntime> ResolveAsync(WorkflowToolchainBinding binding, CancellationToken ct);
}
