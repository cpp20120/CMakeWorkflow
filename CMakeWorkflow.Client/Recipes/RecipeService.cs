#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

public sealed class RecipeService(CMakeWorkflowClient client)
{
    public Task<PreparedRecipe> PrepareProfileAsync(PrepareProfileRecipeOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("profileRecipe/prepare");
        return client.Proxy.ProfileRecipePrepareAsync(options, ct);
    }

    public Task<PreparedRecipe> PrepareTestMatrixAsync(PrepareTestMatrixOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("testMatrix/prepare");
        return client.Proxy.TestMatrixPrepareAsync(options, ct);
    }

    public Task<PreparedRecipe> PrepareBuildMatrixAsync(PrepareBuildMatrixOptions options,
        CancellationToken ct = default)
    {
        client.RequireCapability("buildMatrix/prepare");
        return client.Proxy.BuildMatrixPrepareAsync(options, ct);
    }

    public Task<PreparedRecipe> PrepareBenchmarkMatrixAsync(PrepareBenchmarkMatrixOptions options,
        CancellationToken ct = default)
    {
        client.RequireCapability("benchmarkMatrix/prepare");
        return client.Proxy.BenchmarkMatrixPrepareAsync(options, ct);
    }
}
