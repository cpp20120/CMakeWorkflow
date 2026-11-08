using CMakeWorkflow.Contracts.Recipes;

namespace CMakeWorkflow.Host.Recipes;

public sealed record BenchmarkMatrixPrepareRequest(string RequestId, string OutputPath, BenchmarkMatrixRecipeV1 Recipe);
