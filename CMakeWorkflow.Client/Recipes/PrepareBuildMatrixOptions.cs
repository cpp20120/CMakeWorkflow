namespace CMakeWorkflow.Client.Recipes;

public sealed record PrepareBuildMatrixOptions(string RequestId, string OutputPath, BuildMatrixRecipeV1 Recipe);
