using CMakeWorkflow.Contracts.Recipes;

namespace CMakeWorkflow.Host.Recipes;

public sealed record BuildMatrixPrepareRequest(string RequestId, string OutputPath, BuildMatrixRecipeV1 Recipe);
