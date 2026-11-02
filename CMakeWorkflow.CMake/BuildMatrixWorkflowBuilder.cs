using System.Text.Json.Serialization;

namespace CMakeWorkflow;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BuildVariantV1(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string BuildDirectory,
    string? Configuration = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BuildMatrixRecipeV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] BuildVariantV1[] Variants,
    [property: JsonRequired] string[] Targets);

public static class BuildMatrixWorkflowBuilder
{
    public const int MaxNodes = 512;

    public static WorkflowDocument Build(BuildMatrixRecipeV1 recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        RecipeRules.Version(recipe.Kind, recipe.SchemaVersion, "build-matrix");
        if (recipe.Variants is null || recipe.Variants.Length is < 1 or > 32)
            throw new ArgumentException("Build matrix requires 1..32 variants.");
        if (recipe.Targets is null || recipe.Targets.Length is < 1 or > MaxNodes)
            throw new ArgumentException($"Build matrix requires 1..{MaxNodes} targets.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in recipe.Variants)
        {
            if (variant is null) throw new ArgumentException("Build variant cannot be null.");
            RecipeRules.Id(variant.Name, "variant name");
            RecipeRules.Directory(variant.BuildDirectory, "buildDirectory");
            RecipeRules.Configuration(variant.Configuration);
            if (!names.Add(variant.Name)) throw new ArgumentException($"Duplicate build variant: {variant.Name}.");
        }
        RecipeRules.DistinctDirectories(recipe.Variants.Select(v => v.BuildDirectory), "Build variants");
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in recipe.Targets)
        {
            RecipeRules.Name(target, "target");
            if (!targets.Add(target)) throw new ArgumentException($"Duplicate build target: {target}.");
        }
        var count = checked((long)recipe.Variants.Length * recipe.Targets.Length);
        if (count > MaxNodes)
            throw new ArgumentException($"Build matrix expands to {count} nodes; limit is {MaxNodes}.");
        var contexts = new Dictionary<string, WorkflowContext>(StringComparer.Ordinal);
        var nodes = new List<WorkflowNode>((int)count);
        foreach (var variant in recipe.Variants)
        {
            contexts.Add(variant.Name, RecipeRules.Adopted(variant.BuildDirectory, variant.Configuration));
            for (var targetIndex = 0; targetIndex < recipe.Targets.Length; targetIndex++)
                nodes.Add(new WorkflowNode { Id = $"build_{variant.Name}_{targetIndex + 1:D3}",
                    Kind = "build", Context = variant.Name, Target = recipe.Targets[targetIndex] });
        }
        return new WorkflowDocument { SchemaVersion = 2, Contexts = contexts, Nodes = nodes };
    }
}
