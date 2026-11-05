using System.Text.Json.Serialization;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TestMatrixRecipeV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string BuildDirectory,
    [property: JsonRequired] string Target,
    string? Configuration,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
[property: JsonRequired] TestMatrixGroupV1[] Groups
#pragma warning restore CA1819
,
    [property: JsonRequired] string ExecutionOrder,
    [property: JsonRequired] double TimeoutSeconds);
