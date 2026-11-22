using System.Text.RegularExpressions;
using CMakeWorkflow.Contracts.Recipes;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.CMake.Recipes;

/// <summary>Validates a benchmark recipe and creates a build followed by serial timing dependencies.</summary>
/// <remarks>
/// Pure in-memory graph construction: no filesystem access, build, target execution or profiler launch.
/// The caller owns manifest persistence, Host path authorization, planning and durable admission.
/// At execution time, builds/configuration can mutate native trees and run/profile/test commands can have external effects.
/// Recovery must use verified checkpoints and must not replay uncertain running nodes blindly.
/// </remarks>
public static class BenchmarkWorkflowBuilder
{
    /// <summary>Validates recipe inputs and returns a new mutable schema-v2 workflow definition.</summary>
    /// <remarks>
    /// Throws for invalid inputs before returning a graph. Returning a document does not establish target existence,
    /// profiler availability or permission to execute. The planner and runtime remain authoritative.
    /// </remarks>
    public static WorkflowDocument Build(BenchmarkRecipeV1 recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.Kind != "benchmark" || recipe.SchemaVersion != 1)
        {
            throw new ArgumentException("Benchmark recipe requires kind=benchmark and schemaVersion=1.");
        }

        if (string.IsNullOrWhiteSpace(recipe.BuildDirectory)
            || !Path.IsPathFullyQualified(recipe.BuildDirectory)
            || recipe.BuildDirectory.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Benchmark buildDirectory must be an absolute path.");
        }

        if (string.IsNullOrWhiteSpace(recipe.Target)
            || !Regex.IsMatch(recipe.Target, @"^[A-Za-z0-9_.+\-]+$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Benchmark target must be a valid CMake target name.");
        }

        if (recipe.Configuration is not null && !Regex.IsMatch(recipe.Configuration,
                @"^[A-Za-z0-9_.+\-]+$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Benchmark configuration is invalid.");
        }

        if (recipe.Repeats is < 1 or > 1000)
        {
            throw new ArgumentException("Benchmark repeats must be between 1 and 1000.");
        }

        if (!double.IsFinite(recipe.TimeoutSeconds) || recipe.TimeoutSeconds is <= 0 or > 86400)
        {
            throw new ArgumentException("Benchmark timeoutSeconds must be positive and at most one day.");
        }

        if (recipe.Arguments is null || recipe.Arguments.Any(a => a is null || a.Contains('\0', StringComparison.Ordinal)))
        {
            throw new ArgumentException("Benchmark arguments must be an array of strings without NUL characters.");
        }

        RecipeRules.ValidateCompilerCacheProfile(recipe.CompilerCacheProfile);
        BenchmarkMetricExtractor.Validate(recipe.Metrics);
        bool managed = recipe.SourceDirectory is not null || recipe.ConfigurePreset is not null;
        if (managed && (string.IsNullOrWhiteSpace(recipe.SourceDirectory)
                        || !Path.IsPathFullyQualified(recipe.SourceDirectory)
                        || recipe.SourceDirectory.Contains('\0', StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(recipe.ConfigurePreset)
                        || recipe.ConfigurePreset.StartsWith('-')
                        || recipe.ConfigurePreset.Contains('\0', StringComparison.Ordinal)))
        {
            throw new ArgumentException("Managed benchmark requires an absolute sourceDirectory and a literal configurePreset.");
        }

        List<WorkflowNode> nodes = new(recipe.Repeats + (managed ? 2 : 1));
        if (managed)
        {
            nodes.Add(new() { Id = "configure", Kind = "configure", Context = "benchmark" });
        }
        BenchmarkWorkflowNodes.Add(nodes, recipe.Target, recipe.Repeats, recipe.Arguments, recipe.TimeoutSeconds,
            recipe.Metrics, managed);

        return new WorkflowDocument
        {
            SchemaVersion = 2,
            Contexts = new Dictionary<string, WorkflowContext>(StringComparer.Ordinal)
            {
                ["benchmark"] = new()
                {
                    BuildDirectory = recipe.BuildDirectory,
                    Configuration = recipe.Configuration,
                    SourceDirectory = recipe.SourceDirectory,
                    ConfigurePreset = recipe.ConfigurePreset,
                    CompilerCacheProfile = recipe.CompilerCacheProfile,
                    Mode = managed ? "managed" : "adopt",
                    ProvenancePolicy = managed ? "strict" : "allowUnverified"
                }
            },
            Nodes = nodes
        };
    }
}
