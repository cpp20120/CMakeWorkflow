namespace CMakeWorkflow.Client.Workflows;

// One SDK entry for domain-qualified operations; existing Workflows stays intact.
/// <summary>Domain-qualified execution facade borrowing the client connection.</summary>
public sealed class ExecutionService(CMakeWorkflowClient client)
{
    /// <summary>Returns a verified terminal process-domain report.</summary>
    /// <remarks>
    /// Unavailable until durable finalization. Canceling this read does not affect execution.
    /// </remarks>
    public Task<System.Text.Json.JsonElement> GetReportAsync(string operationId,
        CancellationToken cancellationToken = default)
    {
        client.RequireCapability("execution/report");
        return client.Proxy.ExecutionReportAsync(new(operationId), cancellationToken);
    }

    /// <summary>Submits immutable input to a Host-supported durable execution domain.</summary>
    /// <remarks>
    /// Save ClientRequestId before calling. Identical admission identities reuse acceptance; conflicting reuse is rejected.
    /// Cancellation or transport failure can occur after acceptance; find the ID in the same workspace before retrying.
    /// No automatic retry/reconnect occurs. Request operation cancellation explicitly on the returned handle.
    /// </remarks>
    /// <param name="options">Domain and Host-side admission parameters.</param>
    /// <param name="cancellationToken">Cancels the RPC, not accepted execution.</param>
    /// <returns>A handle bound to this client; acceptance is not completion.</returns>
    public async Task<WorkflowOperation> StartAsync(ExecutionStartOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        client.RequireCapability("execution/start");
        if (string.IsNullOrWhiteSpace(options.ClientRequestId))
        {
            throw new ArgumentException("A durable ClientRequestId is required.");
        }

        OperationSnapshot accepted = await client.Proxy.ExecutionStartAsync(options, cancellationToken);
        return new WorkflowOperation(client, accepted.OperationId);
    }
}
