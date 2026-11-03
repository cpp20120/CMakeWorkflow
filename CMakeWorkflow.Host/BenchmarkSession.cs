using System.Text.Json;

namespace CMakeWorkflow.Host;

public sealed partial class DoctorSession
{
    public Task<BenchmarkPreparation> BenchmarkPrepareAsync(BenchmarkPrepareRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            ValidateRecipeRequest(request.RequestId);
            if (request.Recipe is null)
                throw new ArgumentException("Benchmark recipe is required.");
            if (string.IsNullOrWhiteSpace(request.Recipe.BuildDirectory))
                throw new ArgumentException("Benchmark buildDirectory is required.");
            var manifest = paths.Require(request.OutputPath);
            var build = paths.Require(request.Recipe.BuildDirectory);
            var recipe = request.Recipe with { BuildDirectory = build };
            var document = BenchmarkWorkflowBuilder.Build(recipe);
            var prepared = new BenchmarkPreparation(1, manifest, Hash(document));
            await CommitRecipeAsync(paths, "benchmark", request.RequestId, manifest,
                document, JsonSerializer.SerializeToUtf8Bytes(prepared,
                    HostJson.Default.BenchmarkPreparation), ct);
            return prepared;
        }, cancellationToken);
}