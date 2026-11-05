#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

public sealed class BenchmarkService(CMakeWorkflowClient client)
{
    public Task<PreparedBenchmark> PrepareAsync(PrepareBenchmarkOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("benchmark/prepare");
        return client.Proxy.BenchmarkPrepareAsync(options, ct);
    }
}
