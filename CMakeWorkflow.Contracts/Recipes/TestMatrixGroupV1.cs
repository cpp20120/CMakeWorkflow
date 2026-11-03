using System.Text.Json.Serialization;

namespace CMakeWorkflow;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TestMatrixGroupV1(
    [property: JsonRequired] string Name,
    string? TestRegex = null,
    string? LabelRegex = null);
