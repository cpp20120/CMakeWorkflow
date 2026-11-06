using CMakeWorkflow.Contracts.Recipes;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Recipes;
#pragma warning restore IDE0130

public sealed record ProfileRecipePrepareRequest(string RequestId, string OutputPath, ProfileRecipeV1 Recipe);
