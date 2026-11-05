#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public static class BenchmarkMatrixWorkflowBuilder
{
    public const int MaxNodes = 4096;

    public static WorkflowDocument Build(BenchmarkMatrixRecipeV1 recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        RecipeRules.Version(recipe.Kind, recipe.SchemaVersion, "benchmark-matrix");
        RecipeRules.Name(recipe.Target, "target");
        RecipeRules.Timeout(recipe.TimeoutSeconds);
        RecipeRules.Arguments(recipe.FixedArguments, "fixedArguments");
        if (recipe.Repeats is < 1 or > 1000)
        {
            throw new ArgumentException("Benchmark matrix repeats must be between 1 and 1000.");
        }

        if (recipe.ExecutionOrder is not ("sequential" or "round-robin"))
        {
            throw new ArgumentException("executionOrder must be sequential or round-robin.");
        }

        if (recipe.Variants is null || recipe.Variants.Length is < 1 or > 32)
        {
            throw new ArgumentException("Benchmark matrix requires 1..32 variants.");
        }

        HashSet<string> variantNames = new(StringComparer.Ordinal);
        foreach (BuildVariantV1? variant in recipe.Variants)
        {
            if (variant is null)
            {
                throw new ArgumentException("Benchmark variant cannot be null.");
            }

            RecipeRules.Id(variant.Name, "variant name");
            RecipeRules.Directory(variant.BuildDirectory, "buildDirectory");
            RecipeRules.Configuration(variant.Configuration);
            if (!variantNames.Add(variant.Name))
            {
                throw new ArgumentException($"Duplicate benchmark variant: {variant.Name}.");
            }
        }

        RecipeRules.DistinctDirectories(recipe.Variants.Select(v => v.BuildDirectory), "Benchmark variants");
        if (recipe.Axes is null || recipe.Axes.Length > 8)
        {
            throw new ArgumentException("Benchmark matrix accepts at most 8 argument axes.");
        }

        HashSet<string> axisNames = new(StringComparer.Ordinal);
        HashSet<string> flags = new(StringComparer.Ordinal);
        long combinations = 1;
        foreach (BenchmarkAxisV1? axis in recipe.Axes)
        {
            if (axis is null)
            {
                throw new ArgumentException("Benchmark axis cannot be null.");
            }

            RecipeRules.Id(axis.Name, "axis name");
            if (!axisNames.Add(axis.Name))
            {
                throw new ArgumentException($"Duplicate benchmark axis: {axis.Name}.");
            }

            if (string.IsNullOrWhiteSpace(axis.Flag) || !axis.Flag.StartsWith("--", StringComparison.Ordinal)
                                                     || axis.Flag.Length < 3 || axis.Flag.Any(char.IsWhiteSpace) ||
                                                     axis.Flag.Contains('\0', StringComparison.Ordinal)
                                                     || !flags.Add(axis.Flag))
            {
                throw new ArgumentException($"{axis.Name}: axis flag must be a unique literal --option.");
            }

            if (axis.Values is null || axis.Values.Length is < 1 or > 32
                                    || axis.Values.Any(v => v is null || v.Contains('\0', StringComparison.Ordinal))
                                    || axis.Values.Distinct(StringComparer.Ordinal).Count() != axis.Values.Length)
            {
                throw new ArgumentException($"{axis.Name}: values must be 1..32 distinct strings without NUL.");
            }

            combinations = checked(combinations * axis.Values.Length);
        }

        long timings = checked(combinations * recipe.Repeats * recipe.Variants.Length);
        long nodesCount = checked(timings + recipe.Variants.Length);
        if (nodesCount > MaxNodes)
        {
            throw new ArgumentException(
                $"Benchmark matrix expands to {nodesCount} nodes ({timings} timings); limit is {MaxNodes}.");
        }

        List<string[]> argumentSets = [[]];
        foreach (BenchmarkAxisV1 axis in recipe.Axes)
        {
            List<string[]> next = new(checked(argumentSets.Count * axis.Values.Length));
            foreach (string[] prefix in argumentSets)
            {
                foreach (string value in axis.Values)
                {
                    next.Add([.. prefix, axis.Flag, value]);
                }
            }

            argumentSets = next;
        }

        Dictionary<string, WorkflowContext> contexts = new(StringComparer.Ordinal);
        List<WorkflowNode> nodes = new((int)nodesCount);
        foreach (BuildVariantV1 variant in recipe.Variants)
        {
            contexts.Add(variant.Name, RecipeRules.Adopted(variant.BuildDirectory, variant.Configuration));
            nodes.Add(new WorkflowNode
            {
                Id = "build_" + variant.Name,
                Kind = "build",
                Context = variant.Name,
                Target = recipe.Target
            });
        }

        List<string> builds = [.. nodes.Select(n => n.Id)];
        string? previous = null;

        void AddTiming(BuildVariantV1 variant, int combo, int repeat)
        {
            string id = $"timing_{variant.Name}_{combo + 1:D4}_{repeat:D3}";
            nodes.Add(new WorkflowNode
            {
                Id = id,
                Kind = "run",
                From = "build_" + variant.Name,
                DependsOn = previous is null ? [.. builds] : [previous],
                Arguments = [.. recipe.FixedArguments, .. argumentSets[combo]],
                TimeoutSeconds = recipe.TimeoutSeconds,
                Resources = ["measurement-host"]
            });
            previous = id;
        }

        if (recipe.ExecutionOrder == "sequential")
        {
            foreach (BuildVariantV1 variant in recipe.Variants)
            {
                for (int combo = 0; combo < argumentSets.Count; combo++)
                {
                    for (int repeat = 1; repeat <= recipe.Repeats; repeat++)
                    {
                        AddTiming(variant, combo, repeat);
                    }
                }
            }
        }
        else
        {
            for (int repeat = 1; repeat <= recipe.Repeats; repeat++)
            {
                for (int combo = 0; combo < argumentSets.Count; combo++)
                {
                    foreach (BuildVariantV1? variant in repeat % 2 == 1 ? recipe.Variants : recipe.Variants.Reverse())
                    {
                        AddTiming(variant, combo, repeat);
                    }
                }
            }
        }

        return new WorkflowDocument { SchemaVersion = 2, Contexts = contexts, Nodes = nodes };
    }
}