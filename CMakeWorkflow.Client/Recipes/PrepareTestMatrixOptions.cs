namespace CMakeWorkflow.Client.Recipes;

public sealed record PrepareTestMatrixOptions(string RequestId, string OutputPath, TestMatrixRecipeV1 Recipe);
