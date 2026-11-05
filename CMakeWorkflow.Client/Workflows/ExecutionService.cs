namespace CMakeWorkflow.Client;

// One SDK entry for domain-qualified operations; existing Workflows stays intact.
public sealed class ExecutionService(CMakeWorkflowClient client)
{
    /// <summary>Returns the verified process-domain report after durable finalization.</summary>
    public Task<System.Text.Json.JsonElement> GetReportAsync(string operationId,
        CancellationToken cancellationToken = default)
    {
        client.RequireCapability("execution/report");
        return client.Proxy.ExecutionReportAsync(new(operationId), cancellationToken);
    }

    public async Task<WorkflowOperation> StartAsync(ExecutionStartOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        client.RequireCapability("execution/start");
        if (string.IsNullOrWhiteSpace(options.ClientRequestId))
            throw new ArgumentException("A durable ClientRequestId is required.");
        var accepted = await client.Proxy.ExecutionStartAsync(options, cancellationToken);
        return new WorkflowOperation(client, accepted.OperationId);
    }
}
