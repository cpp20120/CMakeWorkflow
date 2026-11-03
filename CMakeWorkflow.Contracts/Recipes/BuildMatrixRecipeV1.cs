using System.Text.Json.Serialization;

namespace CMakeWorkflow;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BuildMatrixRecipeV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] BuildVariantV1[] Variants,
    [property: JsonRequired] string[] Targets);
