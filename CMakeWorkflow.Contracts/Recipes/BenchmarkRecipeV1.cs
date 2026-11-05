using System.Text.Json.Serialization;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

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

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
[property: JsonRequired] string[] Arguments
#pragma warning restore CA1819
);
