using System.Text.Json.Serialization;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Contracts.Recipes;
#pragma warning restore IDE0130

/// <summary>Named build-tree variant used for timing and profile capture.</summary>
/// <remarks>
/// Names match [a-z][a-z0-9_]*; names and normalized build directories must be distinct in a profile recipe.
/// The DTO itself performs no I/O or validation; recipe builders enforce these requirements.
/// </remarks>
/// <param name="Name">Lowercase identifier starting with a letter, followed by lowercase letters, digits or underscores.</param>
/// <param name="BuildDirectory">Absolute build-tree path on the execution host.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileVariant(string Name, string BuildDirectory);
