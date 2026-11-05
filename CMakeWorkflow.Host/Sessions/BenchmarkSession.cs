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
            {
                throw new ArgumentException("Benchmark recipe is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Recipe.BuildDirectory))
            {
                throw new ArgumentException("Benchmark buildDirectory is required.");
            }

            string manifest = paths.Require(request.OutputPath);
            string build = paths.Require(request.Recipe.BuildDirectory);
            BenchmarkRecipeV1 recipe = request.Recipe with { BuildDirectory = build };
            WorkflowDocument document = BenchmarkWorkflowBuilder.Build(recipe);
            BenchmarkPreparation prepared = new(1, manifest, Hash(document));
            await CommitRecipeAsync(paths, "benchmark", request.RequestId, manifest,
                document, JsonSerializer.SerializeToUtf8Bytes(prepared,
                    HostJson.Default.BenchmarkPreparation), ct);
            return prepared;
        }, cancellationToken);
}