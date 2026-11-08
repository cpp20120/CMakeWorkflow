using CMakeWorkflow.Contracts.Recipes;

namespace CMakeWorkflow.Host.Recipes;

public sealed record ProfileRecipePrepareRequest(string RequestId, string OutputPath, ProfileRecipeV1 Recipe);
