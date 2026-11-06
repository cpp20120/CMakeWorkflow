namespace CMakeWorkflow.Client.Recipes;

/// <summary>Prepares typed recipes into immutable Host manifest files.</summary>
/// <remarks>
/// All paths belong to the initialized Host workspace. Preparation persists a request binding before the manifest.
/// An interrupted write may leave that binding or the manifest committed; retry explicitly with the same request and output.
/// Conflicting committed bytes are refused. Preparation neither executes nodes nor admits a durable workflow operation.
/// This facade borrows the client lifetime and does not automatically reconnect or retry.
/// </remarks>
public sealed class RecipeService(CMakeWorkflowClient client)
{
    /// <summary>Prepares build/timing/capture dependencies; managed configuration requires one variant and a source directory.</summary>
    /// <remarks>
    /// Validates the typed recipe and writes Host-side preparation evidence; no workflow nodes run.
    /// Cancellation can interrupt this RPC after files have committed. Explicitly repeat the same preparation to recover.
    /// </remarks>
    /// <param name="options">Request identity, Host output path and typed recipe.</param>
    /// <param name="ct">Cancels preparation; no durable operation is submitted or canceled.</param>
    /// <returns>Host manifest path and exact-byte SHA-256 for subsequent preflight and explicit start.</returns>
    public Task<PreparedRecipe> PrepareProfileAsync(PrepareProfileRecipeOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("profileRecipe/prepare");
        return client.Proxy.ProfileRecipePrepareAsync(options, ct);
    }

    /// <summary>Prepares one build followed by parallel or sequential CTest groups.</summary>
    /// <remarks>
    /// Validates the typed recipe and writes Host-side preparation evidence; no workflow nodes run.
    /// Cancellation can interrupt this RPC after files have committed. Explicitly repeat the same preparation to recover.
    /// </remarks>
    /// <param name="options">Request identity, Host output path and typed recipe.</param>
    /// <param name="ct">Cancels preparation; no durable operation is submitted or canceled.</param>
    /// <returns>Host manifest path and exact-byte SHA-256 for subsequent preflight and explicit start.</returns>
    public Task<PreparedRecipe> PrepareTestMatrixAsync(PrepareTestMatrixOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("testMatrix/prepare");
        return client.Proxy.TestMatrixPrepareAsync(options, ct);
    }

    /// <summary>Prepares the variant/target build product with no artificial ordering edges.</summary>
    /// <remarks>
    /// Validates the typed recipe and writes Host-side preparation evidence; no workflow nodes run.
    /// Cancellation can interrupt this RPC after files have committed. Explicitly repeat the same preparation to recover.
    /// </remarks>
    /// <param name="options">Request identity, Host output path and typed recipe.</param>
    /// <param name="ct">Cancels preparation; no durable operation is submitted or canceled.</param>
    /// <returns>Host manifest path and exact-byte SHA-256 for subsequent preflight and explicit start.</returns>
    public Task<PreparedRecipe> PrepareBuildMatrixAsync(PrepareBuildMatrixOptions options,
        CancellationToken ct = default)
    {
        client.RequireCapability("buildMatrix/prepare");
        return client.Proxy.BuildMatrixPrepareAsync(options, ct);
    }

    /// <summary>Prepares all builds followed by a serial chain of Cartesian argument timings.</summary>
    /// <remarks>
    /// Validates the typed recipe and writes Host-side preparation evidence; no workflow nodes run.
    /// Cancellation can interrupt this RPC after files have committed. Explicitly repeat the same preparation to recover.
    /// </remarks>
    /// <param name="options">Request identity, Host output path and typed recipe.</param>
    /// <param name="ct">Cancels preparation; no durable operation is submitted or canceled.</param>
    /// <returns>Host manifest path and exact-byte SHA-256 for subsequent preflight and explicit start.</returns>
    public Task<PreparedRecipe> PrepareBenchmarkMatrixAsync(PrepareBenchmarkMatrixOptions options,
        CancellationToken ct = default)
    {
        client.RequireCapability("benchmarkMatrix/prepare");
        return client.Proxy.BenchmarkMatrixPrepareAsync(options, ct);
    }
}
