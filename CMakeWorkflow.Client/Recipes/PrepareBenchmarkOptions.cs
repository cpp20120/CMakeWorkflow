namespace CMakeWorkflow.Client.Recipes;

public sealed record PrepareBenchmarkOptions(string RequestId, string OutputPath, BenchmarkRecipeV1 Recipe);
