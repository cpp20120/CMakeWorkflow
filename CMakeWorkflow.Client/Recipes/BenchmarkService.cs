namespace CMakeWorkflow.Client.Recipes;

public sealed class BenchmarkService(CMakeWorkflowClient client)
{
    public Task<PreparedBenchmark> PrepareAsync(PrepareBenchmarkOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("benchmark/prepare");
        return client.Proxy.BenchmarkPrepareAsync(options, ct);
    }
}
