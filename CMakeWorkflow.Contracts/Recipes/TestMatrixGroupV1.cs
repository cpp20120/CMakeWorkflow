using System.Text.Json.Serialization;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Contracts.Recipes;
#pragma warning restore IDE0130

/// <summary>Named CTest selection with exactly one non-null filter.</summary>
/// <remarks>
/// The selected regex must be nonempty, contain no NUL, and pass regex validation.
/// The DTO itself performs no I/O or validation; recipe builders enforce these requirements.
/// </remarks>
/// <param name="Name">Lowercase identifier starting with a letter, followed by lowercase letters, digits or underscores.</param>
/// <param name="TestRegex">CTest name regex; null requires LabelRegex.</param>
/// <param name="LabelRegex">CTest label regex; null requires TestRegex.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TestMatrixGroupV1(
    [property: JsonRequired] string Name,
    string? TestRegex = null,
    string? LabelRegex = null);
