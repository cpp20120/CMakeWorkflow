using System.Text.RegularExpressions;

namespace CMakeWorkflow.CMake.Recipes;

public static class TestMatrixWorkflowBuilder
{
    public const int MaxGroups = 64;

    public static WorkflowDocument Build(TestMatrixRecipeV1 recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        RecipeRules.Version(recipe.Kind, recipe.SchemaVersion, "test-matrix");
        RecipeRules.Directory(recipe.BuildDirectory, "buildDirectory");
        RecipeRules.Name(recipe.Target, "target");
        RecipeRules.Configuration(recipe.Configuration);
        RecipeRules.Timeout(recipe.TimeoutSeconds);
        if (recipe.ExecutionOrder is not ("parallel" or "sequential"))
        {
            throw new ArgumentException("executionOrder must be parallel or sequential.");
        }

        if (recipe.Groups is null || recipe.Groups.Length is < 1 or > MaxGroups)
        {
            throw new ArgumentException($"Test matrix requires 1..{MaxGroups} groups.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (TestMatrixGroupV1? group in recipe.Groups)
        {
            if (group is null)
            {
                throw new ArgumentException("Test group cannot be null.");
            }

            RecipeRules.Id(group.Name, "group name");
            if (!names.Add(group.Name))
            {
                throw new ArgumentException($"Duplicate test group: {group.Name}.");
            }

            if ((group.TestRegex is null) == (group.LabelRegex is null))
            {
                throw new ArgumentException($"{group.Name}: select exactly one of testRegex or labelRegex.");
            }

            string pattern = group.TestRegex ?? group.LabelRegex!;
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException($"{group.Name}: selection regex is empty or contains NUL.");
            }

            try
            {
                _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException error)
            {
                throw new ArgumentException($"{group.Name}: invalid selection regex: {error.Message}");
            }
        }

        List<WorkflowNode> nodes = new(recipe.Groups.Length + 1)
        {
            new() { Id = "build_tests", Kind = "build", Context = "tests", Target = recipe.Target }
        };
        string previous = "build_tests";
        foreach (TestMatrixGroupV1 group in recipe.Groups)
        {
            string id = "ctest_" + group.Name;
            nodes.Add(new WorkflowNode
            {
                Id = id,
                Kind = "ctest",
                Context = "tests",
                DependsOn = [recipe.ExecutionOrder == "parallel" ? "build_tests" : previous],
                TestRegex = group.TestRegex,
                LabelRegex = group.LabelRegex,
                TimeoutSeconds = recipe.TimeoutSeconds
            });
            previous = id;
        }

        return new WorkflowDocument
        {
            SchemaVersion = 2,
            Contexts = new(StringComparer.Ordinal)
            { ["tests"] = RecipeRules.Adopted(recipe.BuildDirectory, recipe.Configuration) },
            Nodes = nodes
        };
    }
}