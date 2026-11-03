using System.Text.Json.Serialization;

namespace CMakeWorkflow;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BenchmarkMatrixRecipeV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] BuildVariantV1[] Variants,
    [property: JsonRequired] string Target,
    [property: JsonRequired] int Repeats,
    [property: JsonRequired] double TimeoutSeconds,
    [property: JsonRequired] string[] FixedArguments,
    [property: JsonRequired] BenchmarkAxisV1[] Axes,
    [property: JsonRequired] string ExecutionOrder);