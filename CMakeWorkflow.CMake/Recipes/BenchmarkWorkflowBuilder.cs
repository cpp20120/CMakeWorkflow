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

        List<WorkflowNode> nodes = new(recipe.Repeats + 1)
        {
            new() { Id = "build", Kind = "build", Context = "benchmark", Target = recipe.Target }
        };
        for (int repeat = 1; repeat <= recipe.Repeats; repeat++)
        {
            nodes.Add(new WorkflowNode
            {
                Id = $"timing_{repeat:D3}",
                Kind = "run",
                From = "build",
                DependsOn = [repeat == 1 ? "build" : $"timing_{repeat - 1:D3}"],
                Arguments = [.. recipe.Arguments],
                TimeoutSeconds = recipe.TimeoutSeconds,
                Resources = ["measurement-host"]
            });
        }

        return new WorkflowDocument
        {
            SchemaVersion = 2,
            Contexts = new Dictionary<string, WorkflowContext>(StringComparer.Ordinal)
            {
                ["benchmark"] = new()
                {
                    BuildDirectory = recipe.BuildDirectory,
                    Configuration = recipe.Configuration,
                    Mode = "adopt",
                    ProvenancePolicy = "allowUnverified"
                }
            },
            Nodes = nodes
        };
    }
}