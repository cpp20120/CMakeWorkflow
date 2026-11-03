using System.Text.Json.Serialization;

namespace CMakeWorkflow;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BenchmarkAxisV1(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Flag,
    [property: JsonRequired] string[] Values);
