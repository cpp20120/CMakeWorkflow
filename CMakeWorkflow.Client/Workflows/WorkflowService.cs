using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;

namespace CMakeWorkflow.Client;

public sealed class WorkflowService(CMakeWorkflowClient client)
{
    /// <summary>Submits once through durable Host admission. Paths are sent unchanged, including over SSH.</summary>
    /// <remarks>
    /// The Host owns accepted work independently of this connection. Cancellation of this RPC or a lost
    /// response is not proof of rejection or operation cancellation. Reconnect to the same workspace and
    /// use Operations.FindAsync with the saved ClientRequestId; never automatically resubmit an uncertain request.
    /// Stop accepted work explicitly through WorkflowOperation.RequestCancellationAsync.
    /// </remarks>
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
