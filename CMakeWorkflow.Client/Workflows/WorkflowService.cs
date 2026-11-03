using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;

namespace CMakeWorkflow.Client;

public sealed class WorkflowService(CMakeWorkflowClient client)
{
    public async Task<WorkflowOperation> StartAsync(StartWorkflowOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("workflow/start");
        if (string.IsNullOrWhiteSpace(options.ClientRequestId))
            throw new ArgumentException("ClientRequestId is required.");
        var result = await client.Proxy.StartAsync(options, ct);
        return new WorkflowOperation(client, result.OperationId);
    }

    public Task<JsonElement> PlanAsync(string manifest, CancellationToken ct = default)
    {
        client.RequireCapability("workflow/plan");
        // InvokeWithCancellationAsync explicitly binds ct as the RPC invocation
        // cancellation token, never as a serialized member of the params array.
        return client.InvokePlanAsync(new PlanRequest(manifest), ct);
    }
}
