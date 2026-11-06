using CMakeWorkflow.Contracts.Recipes;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.CMake.Recipes;

/// <summary>Validates matrix limits and creates every variant/target build node.</summary>
/// <remarks>
/// Pure in-memory graph construction: no filesystem access, build, target execution or profiler launch.
/// The caller owns manifest persistence, Host path authorization, planning and durable admission.
/// At execution time, builds/configuration can mutate native trees and run/profile/test commands can have external effects.
/// Recovery must use verified checkpoints and must not replay uncertain running nodes blindly.
/// </remarks>
public static class BuildMatrixWorkflowBuilder
{
    public const int MaxNodes = 512;

    /// <summary>Validates recipe inputs and returns a new mutable schema-v2 workflow definition.</summary>
    /// <remarks>
    /// Throws for invalid inputs before returning a graph. Returning a document does not establish target existence,
    /// profiler availability or permission to execute. The planner and runtime remain authoritative.
    /// </remarks>
    public static WorkflowDocument Build(BuildMatrixRecipeV1 recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        RecipeRules.Version(recipe.Kind, recipe.SchemaVersion, "build-matrix");
        if (recipe.Variants is null || recipe.Variants.Length is < 1 or > 32)
        {
            throw new ArgumentException("Build matrix requires 1..32 variants.");
        }

        if (recipe.Targets is null || recipe.Targets.Length is < 1 or > MaxNodes)
        {
            throw new ArgumentException($"Build matrix requires 1..{MaxNodes} targets.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (BuildVariantV1? variant in recipe.Variants)
        {
            if (variant is null)
            {
                throw new ArgumentException("Build variant cannot be null.");
            }

            RecipeRules.Id(variant.Name, "variant name");
            RecipeRules.Directory(variant.BuildDirectory, "buildDirectory");
            RecipeRules.Configuration(variant.Configuration);
            if (!names.Add(variant.Name))
            {
                throw new ArgumentException($"Duplicate build variant: {variant.Name}.");
            }
        }

        RecipeRules.DistinctDirectories(recipe.Variants.Select(v => v.BuildDirectory), "Build variants");
        HashSet<string> targets = new(StringComparer.Ordinal);
        foreach (string target in recipe.Targets)
        {
            RecipeRules.Name(target, "target");
            if (!targets.Add(target))
            {
                throw new ArgumentException($"Duplicate build target: {target}.");
            }
        }

        long count = checked((long)recipe.Variants.Length * recipe.Targets.Length);
        if (count > MaxNodes)
        {
            throw new ArgumentException($"Build matrix expands to {count} nodes; limit is {MaxNodes}.");
        }

        Dictionary<string, WorkflowContext> contexts = new(StringComparer.Ordinal);
        List<WorkflowNode> nodes = new((int)count);
        foreach (BuildVariantV1 variant in recipe.Variants)
        {
            contexts.Add(variant.Name, RecipeRules.Adopted(variant.BuildDirectory, variant.Configuration));
            for (int targetIndex = 0; targetIndex < recipe.Targets.Length; targetIndex++)
            {
                nodes.Add(new WorkflowNode
                {
                    Id = $"build_{variant.Name}_{targetIndex + 1:D3}",
                    Kind = "build",
                    Context = variant.Name,
                    Target = recipe.Targets[targetIndex]
                });
            }
        }

        return new WorkflowDocument { SchemaVersion = 2, Contexts = contexts, Nodes = nodes };
    }
}