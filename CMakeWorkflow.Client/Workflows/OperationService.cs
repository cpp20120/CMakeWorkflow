using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;

namespace CMakeWorkflow.Client;

public sealed class OperationService(CMakeWorkflowClient client)
{
    /// <summary>Binds an existing operation ID to this connection without submitting or resuming work.</summary>
    /// <summary>Browse durable operations, newest first. Does not start or resume execution.</summary>
    public Task<OperationPage> ListAsync(OperationQuery? query = null, CancellationToken ct = default)
    {
        client.RequireCapability("operations/list");
        return client.Proxy.ListOperationsAsync(query ?? new OperationQuery(), ct);
    }

    public WorkflowOperation GetHandle(string operationId) => new(client, operationId);

    /// <summary>Returns one domain-neutral view. Node data requires verified durable finalization.</summary>
    public Task<OperationOverview> GetOverviewAsync(string operationId, CancellationToken ct = default)
    {
        client.RequireCapability("operations/overview");
        return client.Proxy.OverviewAsync(new(operationId), ct);
    }

    public Task<OperationSnapshot> GetAsync(string operationId, CancellationToken ct = default) =>
        client.Proxy.GetAsync(new(operationId), ct);

    /// <summary>Looks up durable acceptance in the initialized Host workspace, including after a lost start response.</summary>
    public Task<OperationSnapshot> FindAsync(string clientRequestId, CancellationToken ct = default) =>
        client.Proxy.FindAsync(new(clientRequestId), ct);
}
