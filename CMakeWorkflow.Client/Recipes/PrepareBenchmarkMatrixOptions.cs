using CMakeWorkflow.Contracts.Recipes;

namespace CMakeWorkflow.Client.Recipes;

/// <summary>Inputs for commit-once Host recipe preparation.</summary>
/// <param name="RequestId">32 hexadecimal characters; retain the same ID and payload when recovering an interrupted preparation.</param>
/// <param name="OutputPath">Host manifest destination in the initialized workspace; existing conflicting content is refused.</param>
/// <param name="Recipe">Non-null typed recipe validated before persistence.</param>
public sealed record PrepareBenchmarkMatrixOptions(string RequestId, string OutputPath, BenchmarkMatrixRecipeV1 Recipe);
