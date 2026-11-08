using System.Text.Json.Serialization;

namespace CMakeWorkflow.Contracts.Recipes;

/// <summary>Named build-tree variant used by matrix recipes.</summary>
/// <remarks>
/// Variant names match [a-z][a-z0-9_]* and must be unique within the recipe.
/// The DTO itself performs no I/O or validation; recipe builders enforce these requirements.
/// </remarks>
/// <param name="Name">Lowercase identifier starting with a letter, followed by lowercase letters, digits or underscores.</param>
/// <param name="BuildDirectory">Absolute build-tree path on the execution host.</param>
/// <param name="Configuration">CMake configuration, or null for the build-tree default.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BuildVariantV1(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string BuildDirectory,
    string? Configuration = null);
