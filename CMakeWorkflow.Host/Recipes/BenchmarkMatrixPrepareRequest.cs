using CMakeWorkflow.Contracts.Recipes;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Recipes;
#pragma warning restore IDE0130

public sealed record BenchmarkMatrixPrepareRequest(string RequestId, string OutputPath, BenchmarkMatrixRecipeV1 Recipe);
