using CMakeWorkflow.Contracts.Recipes;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Recipes;
#pragma warning restore IDE0130

public sealed record BuildMatrixPrepareRequest(string RequestId, string OutputPath, BuildMatrixRecipeV1 Recipe);
