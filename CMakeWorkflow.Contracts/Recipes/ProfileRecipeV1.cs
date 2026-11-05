using System.Text.Json.Serialization;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling;
#pragma warning restore IDE0130

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileRecipeV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int SchemaVersion,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
[property: JsonRequired] ProfileVariant[] Variants
#pragma warning restore CA1819
,
    [property: JsonRequired] string Target,
    string? Configuration,
    [property: JsonRequired] int Repeats,
    [property: JsonRequired] double TimeoutSeconds,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
[property: JsonRequired] string[] Arguments
#pragma warning restore CA1819
,
    [property: JsonRequired] string Backend,
    [property: JsonRequired] string Mode,
    [property: JsonRequired] string Platform,
    string? SourceDirectory = null,
    string? ConfigurePreset = null);