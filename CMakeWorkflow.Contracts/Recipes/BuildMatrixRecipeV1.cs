using System.Text.Json.Serialization;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BuildMatrixRecipeV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int SchemaVersion,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
[property: JsonRequired] BuildVariantV1[] Variants
#pragma warning restore CA1819
,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
[property: JsonRequired] string[] Targets
#pragma warning restore CA1819
);
