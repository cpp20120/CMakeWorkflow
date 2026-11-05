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

    /// <summary>Read-only validation of exact Host manifest bytes and the prospective DAG.</summary>
    public Task<WorkflowPreflightResult> PreflightAsync(WorkflowPreflightOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        client.RequireCapability("workflow/preflight");
        return RpcRefusals.TranslateAsync(() => client.Proxy.PreflightAsync(options, ct));
    }

    /// <summary>Preflight a typed recipe already prepared on this Host; does not execute it.</summary>
    public Task<WorkflowPreflightResult> PreflightAsync(PreparedRecipe prepared, int jobs,
        string resultsDirectory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return PreflightAsync(new(prepared.Manifest, prepared.ManifestSha256, jobs, resultsDirectory), ct);
    }

    public Task<WorkflowPreflightResult> PreflightAsync(PreparedBenchmark prepared, int jobs,
        string resultsDirectory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return PreflightAsync(new(prepared.Manifest, prepared.ManifestSha256, jobs, resultsDirectory), ct);
    }

    public Task<WorkflowPreflightResult> PreflightAsync(PreparedWorkflow prepared, int jobs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return PreflightAsync(new(prepared.Manifest, prepared.ManifestSha256, jobs,
            prepared.ResultsDirectory), ct);
    }

    public Task<JsonElement> PlanAsync(string manifest, CancellationToken ct = default)
    {
        client.RequireCapability("workflow/plan");
        // InvokeWithCancellationAsync explicitly binds ct as the RPC invocation
        // cancellation token, never as a serialized member of the params array.
        return client.InvokePlanAsync(new PlanRequest(manifest), ct);
    }
}
