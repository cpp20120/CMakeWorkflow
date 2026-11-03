using System.Text.Json.Serialization;

namespace CMakeWorkflow;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BuildVariantV1(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string BuildDirectory,
    string? Configuration = null);
