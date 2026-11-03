using System.Text.Json.Serialization;

namespace CMakeWorkflow;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TestMatrixRecipeV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string BuildDirectory,
    [property: JsonRequired] string Target,
    string? Configuration,
    [property: JsonRequired] TestMatrixGroupV1[] Groups,
    [property: JsonRequired] string ExecutionOrder,
    [property: JsonRequired] double TimeoutSeconds);
