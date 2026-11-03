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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BuildVariantV1(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string BuildDirectory,
    string? Configuration = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BuildMatrixRecipeV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] BuildVariantV1[] Variants,
    [property: JsonRequired] string[] Targets);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TestMatrixGroupV1(
    [property: JsonRequired] string Name,
    string? TestRegex = null,
    string? LabelRegex = null);

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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BenchmarkAxisV1(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Flag,
    [property: JsonRequired] string[] Values);

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