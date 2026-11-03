using System.Text.Json.Serialization;

namespace CMakeWorkflow.Profiling;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileVariant(string Name, string BuildDirectory);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileRecipeV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] ProfileVariant[] Variants,
    [property: JsonRequired] string Target,
    string? Configuration,
    [property: JsonRequired] int Repeats,
    [property: JsonRequired] double TimeoutSeconds,
    [property: JsonRequired] string[] Arguments,
    [property: JsonRequired] string Backend,
    [property: JsonRequired] string Mode,
    [property: JsonRequired] string Platform,
    string? SourceDirectory = null,
    string? ConfigurePreset = null);
