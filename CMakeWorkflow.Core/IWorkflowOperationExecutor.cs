namespace CMakeWorkflow.Core;

/// <summary>Extension boundary for node kinds handled outside the built-in CMake executor.</summary>
/// <remarks>
/// The runner verifies the built executable before invocation and owns DAG admission/checkpoint persistence.
/// The extension owns capture/process resources and must finish cleanup before its returned task completes.
/// Do not retain the request or borrowed execution resources for work after completion.
/// </remarks>
public interface IWorkflowOperationExecutor
{
    /// <summary>Reports whether this extension implements the requested node kind; has no execution side effects.</summary>
    bool Supports(string kind);

    /// <summary>Executes one admitted extension node and returns only after worker cleanup.</summary>
    /// <remarks>
    /// The token is the node execution lifetime, possibly including its timeout; it is not a start RPC token.
    /// Cancellation may follow external effects. Returning/throwing does not finalize durable operation evidence by itself.
    /// </remarks>
    Task<WorkflowOperationResult> ExecuteAsync(
        WorkflowOperationRequest request, CancellationToken cancellationToken);
}