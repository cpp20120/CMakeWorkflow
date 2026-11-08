using CMakeWorkflow.Contracts.Recipes;

namespace CMakeWorkflow.Host.Recipes;

public sealed record TestMatrixPrepareRequest(string RequestId, string OutputPath, TestMatrixRecipeV1 Recipe);
