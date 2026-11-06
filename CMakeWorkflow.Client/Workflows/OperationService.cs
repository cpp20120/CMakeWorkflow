namespace CMakeWorkflow.Client.Workflows;

/// <summary>Queries durable operations in the initialized Host workspace.</summary>
/// <remarks>
/// Handles borrow the client and do not own remote execution. Calls do not automatically retry or reconnect.
/// </remarks>
public sealed class OperationService(CMakeWorkflowClient client)
{
    /// <summary>Lists durable operations newest first without starting or resuming them.</summary>
    /// <remarks>
    /// A cursor excludes later admissions but does not freeze state transitions. Null query uses Host-supported defaults.
    /// </remarks>
    public Task<OperationPage> ListAsync(OperationQuery? query = null, CancellationToken ct = default)
    {
        client.RequireCapability("operations/list");
        return client.Proxy.ListOperationsAsync(query ?? new OperationQuery(), ct);
    }

    /// <summary>Creates a local handle without contacting the Host or validating operation existence.</summary>
    /// <param name="operationId">Existing operation ID in this client workspace.</param>
    public WorkflowOperation GetHandle(string operationId) => new(client, operationId);

    /// <summary>Returns one domain-neutral view. Node data requires verified durable finalization.</summary>
    public Task<OperationOverview> GetOverviewAsync(string operationId, CancellationToken ct = default)
    {
        client.RequireCapability("operations/overview");
        return client.Proxy.OverviewAsync(new(operationId), ct);
    }

    /// <summary>Reads the current durable acceptance/cancellation/finalization projection.</summary>
    /// <remarks>
    /// This is not live node progress. Cancellation affects only this read.
    /// </remarks>
    public Task<OperationSnapshot> GetAsync(string operationId, CancellationToken ct = default) =>
        client.Proxy.GetAsync(new(operationId), ct);

    /// <summary>Finds acceptance by caller request ID in the initialized workspace.</summary>
    /// <remarks>
    /// Use after an uncertain start reply. A missing ID is reported as an RPC refusal, not a null result.
    /// This lookup does not start or resume execution; cancellation affects only the lookup.
    /// </remarks>
    public Task<OperationSnapshot> FindAsync(string clientRequestId, CancellationToken ct = default) =>
        client.Proxy.FindAsync(new(clientRequestId), ct);
}
