using System.Text.Json.Serialization;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Contracts.Recipes;
#pragma warning restore IDE0130

/// <summary>Cartesian argument matrix with serialized timing runs after all variant builds.</summary>
/// <remarks>
/// Kind is benchmark-matrix. Requires 1..32 distinct variants, 0..8 axes, repeats 1..1000, and at most 4096 nodes including builds.
/// ExecutionOrder is sequential or round-robin; round-robin reverses variant order on even repetitions.
/// The DTO itself performs no I/O or validation; recipe builders enforce these requirements.
/// </remarks>
/// <param name="Kind">Exact recipe kind discriminator.</param>
/// <param name="SchemaVersion">Recipe schema version; must be 1, independently of workflow schema version.</param>
/// <param name="Variants">Non-null variant array; entries must be non-null with unique names and distinct absolute build directories.</param>
/// <param name="Target">CMake target name.</param>
/// <param name="Repeats">Number of timing repetitions.</param>
/// <param name="TimeoutSeconds">Positive finite per-execution timeout in seconds, at most 86400.</param>
/// <param name="FixedArguments">Literal arguments prepended to every axis combination; no null entries or NUL characters.</param>
/// <param name="Axes">Non-null axes array; empty means one combination with fixed arguments only.</param>
/// <param name="ExecutionOrder">Explicit ordering policy; see the recipe remarks for allowed values.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BenchmarkMatrixRecipeV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int SchemaVersion,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
[property: JsonRequired] BuildVariantV1[] Variants
#pragma warning restore CA1819
,
    [property: JsonRequired] string Target,
    [property: JsonRequired] int Repeats,
    [property: JsonRequired] double TimeoutSeconds,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
[property: JsonRequired] string[] FixedArguments
#pragma warning restore CA1819
,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
[property: JsonRequired] BenchmarkAxisV1[] Axes
#pragma warning restore CA1819
,
    [property: JsonRequired] string ExecutionOrder);