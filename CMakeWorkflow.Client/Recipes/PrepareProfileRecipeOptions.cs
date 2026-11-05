using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client.Recipes;

public sealed record PrepareProfileRecipeOptions(string RequestId, string OutputPath, ProfileRecipeV1 Recipe);
