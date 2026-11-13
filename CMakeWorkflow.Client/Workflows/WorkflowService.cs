using System.Text.Json;
using CMakeWorkflow.Client.Protocol;
using CMakeWorkflow.Client.Recipes;

namespace CMakeWorkflow.Client.Workflows;

/// <summary>Workflow planning and admission facade borrowing the client connection.</summary>
/// <remarks>
/// Paths are Host filesystem paths and are sent unchanged, including over SSH.
/// Calls do not automatically reconnect or retry. Preflight is advisory; start rechecks admission requirements.
/// </remarks>
public sealed class WorkflowService(CMakeWorkflowClient client)
{
    /// <summary>Submits a workflow through durable Host admission.</summary>
    /// <remarks>
    /// Save ClientRequestId before calling. Identical request identities reuse acceptance; changed parameters under the same ID conflict.
    /// An RPC cancellation or lost reply can race with durable acceptance and dispatch: neither proves rejection or stops accepted work.
    /// Find by the saved ID in the same workspace after reconnecting; never generate a new ID to retry an uncertain start.
    /// Use WorkflowOperation.RequestCancellationAsync to request stopping the operation.
    /// Acceptance does not guarantee successful planning, dispatch or completion.
    /// </remarks>
    /// <param name="options">Immutable admission identity, expected manifest hash, parallelism and results path.</param>
    /// <param name="ct">Cancels this RPC; independent of the accepted operation lifetime.</param>
    /// <returns>A non-owning handle to the accepted operation, bound to this client.</returns>
    public async Task<WorkflowOperation> StartAsync(StartWorkflowOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        client.RequireCapability("workflow/start");
        if (options.HistoryPolicy is not null)
        {
            client.RequireCapability("workflow/historyPolicy");
        }

        if (options.Only is { Count: > 0 })
        {
            client.RequireCapability("workflow/nodeSelection");
        }

        if (string.IsNullOrWhiteSpace(options.ClientRequestId))
        {
            throw new ArgumentException("ClientRequestId is required.");
        }

        OperationSnapshot result = await RpcRefusals.TranslateAsync(() => client.Proxy.StartAsync(options, ct));
        return new WorkflowOperation(client, result.OperationId);
    }

    /// <summary>Validates exact Host manifest bytes and the prospective DAG without dispatch.</summary>
    /// <remarks>
    /// The result describes current Host observations; it reserves no build trees or future execution capacity.
    /// Cancellation stops the request, with no durable operation cancellation. RPC failures propagate without automatic retry.
    /// </remarks>
    public Task<WorkflowPreflightResult> PreflightAsync(WorkflowPreflightOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        client.RequireCapability("workflow/preflight");
        if (options.HistoryPolicy is not null)
        {
            client.RequireCapability("workflow/historyPolicy");
        }

        if (options.Only is { Count: > 0 })
        {
            client.RequireCapability("workflow/nodeSelection");
        }

        return RpcRefusals.TranslateAsync(() => client.Proxy.PreflightAsync(options, ct));
    }

    /// <summary>Preflights a prepared typed recipe without dispatch.</summary>
    /// <remarks>
    /// The result describes current Host observations; it reserves no build trees or future execution capacity.
    /// Cancellation stops the request, with no durable operation cancellation. RPC failures propagate without automatic retry.
    /// </remarks>
    public Task<WorkflowPreflightResult> PreflightAsync(PreparedRecipe prepared, int jobs,
        string resultsDirectory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return PreflightAsync(new(prepared.Manifest, prepared.ManifestSha256, jobs, resultsDirectory), ct);
    }

    /// <summary>Preflights a prepared benchmark without dispatch.</summary>
    /// <remarks>
    /// The result describes current Host observations; it reserves no build trees or future execution capacity.
    /// Cancellation stops the request, with no durable operation cancellation. RPC failures propagate without automatic retry.
    /// </remarks>
    public Task<WorkflowPreflightResult> PreflightAsync(PreparedBenchmark prepared, int jobs,
        string resultsDirectory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return PreflightAsync(new(prepared.Manifest, prepared.ManifestSha256, jobs, resultsDirectory), ct);
    }

    /// <summary>Preflights a prepared workflow without dispatch.</summary>
    /// <remarks>
    /// The result describes current Host observations; it reserves no build trees or future execution capacity.
    /// Cancellation stops the request, with no durable operation cancellation. RPC failures propagate without automatic retry.
    /// </remarks>
    public Task<WorkflowPreflightResult> PreflightAsync(PreparedWorkflow prepared, int jobs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return PreflightAsync(new(prepared.Manifest, prepared.ManifestSha256, jobs,
            prepared.ResultsDirectory), ct);
    }

    /// <summary>Reads the Host manifest and returns its plan without starting workflow nodes.</summary>
    /// <remarks>
    /// The result describes current Host observations; it reserves no build trees or future execution capacity.
    /// Cancellation stops the request, with no durable operation cancellation. RPC failures propagate without automatic retry.
    /// </remarks>
    public Task<JsonElement> PlanAsync(string manifest, CancellationToken ct = default)
    {
        client.RequireCapability("workflow/plan");
        // InvokeWithCancellationAsync explicitly binds ct as the RPC invocation
        // cancellation token, never as a serialized member of the params array.
        return client.InvokePlanAsync(new PlanRequest(manifest), ct);
    }
}
