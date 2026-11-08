using CMakeWorkflow.Contracts.Recipes;

namespace CMakeWorkflow.Host.Recipes;

public sealed record BenchmarkPrepareRequest(
    string RequestId,
    string OutputPath,
    BenchmarkRecipeV1 Recipe);
