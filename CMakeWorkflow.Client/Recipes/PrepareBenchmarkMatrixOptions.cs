namespace CMakeWorkflow.Client.Recipes;

public sealed record PrepareBenchmarkMatrixOptions(string RequestId, string OutputPath, BenchmarkMatrixRecipeV1 Recipe);
