using System.Text.Json.Serialization;

namespace CMakeWorkflow.Contracts.Recipes;

/// <summary>Build every target in every variant without adding artificial ordering dependencies.</summary>
/// <remarks>
/// Kind is build-matrix. Requires 1..32 unique variants with distinct build trees, unique targets, and at most 512 expanded nodes.
/// The DTO itself performs no I/O or validation; recipe builders enforce these requirements.
/// </remarks>
/// <param name="Kind">Exact recipe kind discriminator.</param>
/// <param name="SchemaVersion">Recipe schema version; must be 1, independently of workflow schema version.</param>
/// <param name="Variants">Non-null variant array; entries must be non-null with unique names and distinct absolute build directories.</param>
/// <param name="Targets">Nonempty array of unique valid CMake target names.</param>
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
