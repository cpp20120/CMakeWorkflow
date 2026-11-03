using System.Text.Json.Serialization;

namespace CMakeWorkflow;

// Canonical wire and manifest recipe definitions. No runtime or I/O dependency.
// Recipe versioning is independent of WorkflowDocument.schemaVersion.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BenchmarkRecipeV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string BuildDirectory,
    [property: JsonRequired] string Target,
    string? Configuration,
    [property: JsonRequired] int Repeats,
    [property: JsonRequired] double TimeoutSeconds,
    [property: JsonRequired] string[] Arguments);
