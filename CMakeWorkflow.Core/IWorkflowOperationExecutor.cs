namespace CMakeWorkflow;

public interface IWorkflowOperationExecutor
{
    bool Supports(string kind);

    Task<WorkflowOperationResult> ExecuteAsync(
        WorkflowOperationRequest request, CancellationToken cancellationToken);
}