using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;

namespace CMakeWorkflow.Client;

public sealed class OperationService(CMakeWorkflowClient client)
{
    public WorkflowOperation GetHandle(string operationId) => new(client, operationId);

    public Task<OperationSnapshot> GetAsync(string operationId, CancellationToken ct = default) =>
        client.Proxy.GetAsync(new(operationId), ct);

    public Task<OperationSnapshot> FindAsync(string clientRequestId, CancellationToken ct = default) =>
        client.Proxy.FindAsync(new(clientRequestId), ct);
}
