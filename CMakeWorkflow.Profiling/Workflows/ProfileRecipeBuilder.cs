using System.Text.RegularExpressions;
using CMakeWorkflow.Contracts.Recipes;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Profiling.Workflows;

/// <summary>Validates a versioned profile recipe and derives platform-specific capture dependencies.</summary>
/// <remarks>
/// Pure in-memory graph construction: no filesystem access, build, target execution or profiler launch.
/// The caller owns manifest persistence, Host path authorization, planning and durable admission.
/// At execution time, builds/configuration can mutate native trees and run/profile/test commands can have external effects.
/// Recovery must use verified checkpoints and must not replay uncertain running nodes blindly.
/// </remarks>
public static class ProfileRecipeBuilder
{
    /// <summary>Validates recipe inputs and returns a new mutable schema-v2 workflow definition.</summary>
    /// <remarks>
    /// Throws for invalid inputs before returning a graph. Returning a document does not establish target existence,
    /// profiler availability or permission to execute. The planner and runtime remain authoritative.
    /// </remarks>
    public static WorkflowDocument Build(ProfileRecipeV1 recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.Kind != "profile" || recipe.SchemaVersion != 1)
        {
            throw new ArgumentException("Profile recipe requires kind=profile and schemaVersion=1.");
        }

        if (recipe.Variants is null || recipe.Variants.Any(v => v is null))
        {
            throw new ArgumentException("Profile variants are required.");
        }

        if (recipe.Variants.Any(v => string.IsNullOrWhiteSpace(v.Name)
                                     || string.IsNullOrWhiteSpace(v.BuildDirectory)))
        {
            throw new ArgumentException("Profile variant names and build directories are required.");
        }

        if (string.IsNullOrWhiteSpace(recipe.Target) || !Regex.IsMatch(recipe.Target,
                @"^[A-Za-z0-9_.+\-]+$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Profile target is invalid.");
        }

        if (recipe.Configuration is not null && !Regex.IsMatch(recipe.Configuration,
                @"^[A-Za-z0-9_.+\-]+$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Profile configuration is invalid.");
        }

        if (recipe.SourceDirectory is not null && !Path.IsPathFullyQualified(recipe.SourceDirectory))
        {
            throw new ArgumentException("Profile sourceDirectory must be absolute.");
        }

        if (recipe.ConfigurePreset is not null && !Regex.IsMatch(recipe.ConfigurePreset,
                @"^[A-Za-z0-9_.+\-]+$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Profile configurePreset is invalid.");
        }

        if (recipe.Arguments is null || recipe.Arguments.Any(a => a is null || a.Contains('\0', StringComparison.Ordinal)))
        {
            throw new ArgumentException("Profile arguments must be strings without NUL.");
        }

        if (recipe.Platform is not ("linux" or "windows" or "macos"))
        {
            throw new ArgumentException("Profile platform must be linux, windows or macos.");
        }

        string backend = recipe.Backend == "auto"
            ? recipe.Platform switch
            {
                "linux" => "perf",
                "windows" => "etw",
                _ => "instruments"
            }
            : recipe.Backend;
        return ProfileWorkflowBuilder.Build(recipe.Variants, recipe.Target,
            recipe.SourceDirectory, recipe.Configuration, recipe.ConfigurePreset,
            recipe.Repeats, recipe.TimeoutSeconds, recipe.Arguments, backend,
            recipe.Mode, recipe.Platform);
    }
}