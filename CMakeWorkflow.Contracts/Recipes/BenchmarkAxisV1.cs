using System.Text.Json.Serialization;

namespace CMakeWorkflow.Contracts.Recipes;

/// <summary>One argument axis in a Cartesian benchmark matrix.</summary>
/// <remarks>
/// Names and flags must be unique in the recipe. Each value contributes a literal flag/value pair.
/// The DTO itself performs no I/O or validation; recipe builders enforce these requirements.
/// </remarks>
/// <param name="Name">Lowercase identifier starting with a letter, followed by lowercase letters, digits or underscores.</param>
/// <param name="Flag">Unique literal --option without whitespace or NUL.</param>
/// <param name="Values">1..32 distinct literal values without NUL; entries must be non-null.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BenchmarkAxisV1(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Flag,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
[property: JsonRequired] string[] Values
#pragma warning restore CA1819
);
