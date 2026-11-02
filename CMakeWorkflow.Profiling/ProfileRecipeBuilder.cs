using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CMakeWorkflow.Profiling;

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

public static class ProfileRecipeBuilder
{
    public static WorkflowDocument Build(ProfileRecipeV1 recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.Kind != "profile" || recipe.SchemaVersion != 1)
            throw new ArgumentException("Profile recipe requires kind=profile and schemaVersion=1.");
        if (recipe.Variants is null || recipe.Variants.Any(v => v is null))
            throw new ArgumentException("Profile variants are required.");
        if (recipe.Variants.Any(v => string.IsNullOrWhiteSpace(v.Name)
            || string.IsNullOrWhiteSpace(v.BuildDirectory)))
            throw new ArgumentException("Profile variant names and build directories are required.");
        if (string.IsNullOrWhiteSpace(recipe.Target) || !Regex.IsMatch(recipe.Target,
            @"^[A-Za-z0-9_.+\-]+$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Profile target is invalid.");
        if (recipe.Configuration is not null && !Regex.IsMatch(recipe.Configuration,
            @"^[A-Za-z0-9_.+\-]+$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Profile configuration is invalid.");
        if (recipe.SourceDirectory is not null && !Path.IsPathFullyQualified(recipe.SourceDirectory))
            throw new ArgumentException("Profile sourceDirectory must be absolute.");
        if (recipe.ConfigurePreset is not null && !Regex.IsMatch(recipe.ConfigurePreset,
            @"^[A-Za-z0-9_.+\-]+$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Profile configurePreset is invalid.");
        if (recipe.Arguments is null || recipe.Arguments.Any(a => a is null || a.Contains('\0')))
            throw new ArgumentException("Profile arguments must be strings without NUL.");
        if (recipe.Platform is not ("linux" or "windows" or "macos"))
            throw new ArgumentException("Profile platform must be linux, windows or macos.");
        var backend = recipe.Backend == "auto" ? recipe.Platform switch
        {
            "linux" => "perf", "windows" => "etw", _ => "instruments"
        } : recipe.Backend;
        return ProfileWorkflowBuilder.Build(recipe.Variants, recipe.Target,
            recipe.SourceDirectory, recipe.Configuration, recipe.ConfigurePreset,
            recipe.Repeats, recipe.TimeoutSeconds, recipe.Arguments, backend,
            recipe.Mode, recipe.Platform);
    }
}
