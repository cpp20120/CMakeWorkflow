namespace CMakeWorkflow.Client.Recipes;

/// <summary>Prepares benchmark recipes on the connected Host; borrows the client lifetime.</summary>
public sealed class BenchmarkService(CMakeWorkflowClient client)
{
    /// <summary>Persists a benchmark manifest without building or running the target.</summary>
    /// <remarks>
    /// Host-side commit-once preparation can leave files after cancellation or loss of reply.
    /// Retry explicitly with the same request and output; conflicting committed contents fail. No implicit retry occurs.
    /// </remarks>
    /// <param name="options">Recipe, request identity and Host manifest destination.</param>
    /// <param name="ct">Cancels the preparation RPC, not a workflow operation.</param>
    public Task<PreparedBenchmark> PrepareAsync(PrepareBenchmarkOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("benchmark/prepare");
        return client.Proxy.BenchmarkPrepareAsync(options, ct);
    }
}
