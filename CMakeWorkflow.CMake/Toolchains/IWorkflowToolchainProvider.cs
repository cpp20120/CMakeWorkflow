namespace CMakeWorkflow;

public interface IWorkflowToolchainProvider
{
    Task<WorkflowToolchainRuntime> ResolveAsync(WorkflowToolchainBinding binding, CancellationToken ct);
}
