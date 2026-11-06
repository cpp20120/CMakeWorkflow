using System.Text.Json.Serialization;
using CMakeWorkflow.Contracts.Recipes;

namespace CMakeWorkflow.Commands;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(BenchmarkRecipeV1))]
[JsonSerializable(typeof(ProfileRecipeV1))]
[JsonSerializable(typeof(TestMatrixRecipeV1))]
[JsonSerializable(typeof(BuildMatrixRecipeV1))]
[JsonSerializable(typeof(BenchmarkMatrixRecipeV1))]
internal sealed partial class RecipeJson : JsonSerializerContext;