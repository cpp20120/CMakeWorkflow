using System.Text.RegularExpressions;

namespace CMakeWorkflow;

public static class BenchmarkWorkflowBuilder
{
    public static WorkflowDocument Build(BenchmarkRecipeV1 recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.Kind != "benchmark" || recipe.SchemaVersion != 1)
            throw new ArgumentException("Benchmark recipe requires kind=benchmark and schemaVersion=1.");
        if (string.IsNullOrWhiteSpace(recipe.BuildDirectory)
            || !Path.IsPathFullyQualified(recipe.BuildDirectory)
            || recipe.BuildDirectory.Contains('\0'))
            throw new ArgumentException("Benchmark buildDirectory must be an absolute path.");
        if (string.IsNullOrWhiteSpace(recipe.Target)
            || !Regex.IsMatch(recipe.Target, @"^[A-Za-z0-9_.+\-]+$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Benchmark target must be a valid CMake target name.");
        if (recipe.Configuration is not null && !Regex.IsMatch(recipe.Configuration,
            @"^[A-Za-z0-9_.+\-]+$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Benchmark configuration is invalid.");
        if (recipe.Repeats is < 1 or > 1000)
            throw new ArgumentException("Benchmark repeats must be between 1 and 1000.");
        if (!double.IsFinite(recipe.TimeoutSeconds) || recipe.TimeoutSeconds is <= 0 or > 86400)
            throw new ArgumentException("Benchmark timeoutSeconds must be positive and at most one day.");
        if (recipe.Arguments is null || recipe.Arguments.Any(a => a is null || a.Contains('\0')))
            throw new ArgumentException("Benchmark arguments must be an array of strings without NUL characters.");

        var nodes = new List<WorkflowNode>(recipe.Repeats + 1)
        {
            new() { Id = "build", Kind = "build", Context = "benchmark", Target = recipe.Target }
        };
        for (var repeat = 1; repeat <= recipe.Repeats; repeat++)
        {
            nodes.Add(new WorkflowNode
            {
                Id = $"timing_{repeat:D3}", Kind = "run", From = "build",
                DependsOn = [repeat == 1 ? "build" : $"timing_{repeat - 1:D3}"],
                Arguments = recipe.Arguments.ToList(), TimeoutSeconds = recipe.TimeoutSeconds,
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
                    Mode = "adopt", ProvenancePolicy = "allowUnverified"
                }
            },
            Nodes = nodes
        };
    }
}
