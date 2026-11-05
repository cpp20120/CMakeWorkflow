using System.Text.Json;
using CMakeWorkflow;
using CMakeWorkflow.Host;

internal static class BenchmarkRecipeTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException(name);
        }
    }

    private static void Reject(BenchmarkRecipeV1 recipe)
    {
        try
        {
            _ = BenchmarkWorkflowBuilder.Build(recipe);
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new System.Diagnostics.UnreachableException("Invalid benchmark recipe accepted: " + recipe);
    }

    public static void Run(string root)
    {
        string build = Path.Combine(root, "pure benchmark build path");
        BenchmarkRecipeV1 recipe = new("benchmark", 1, build, "worker", "Release", 1,
            300, ["--name", "a b", "", "--literal"]);
        WorkflowDocument one = BenchmarkWorkflowBuilder.Build(recipe);
        Check(!Directory.Exists(build), "pure builder created a build tree");
        Check(one.SchemaVersion == 2 && one.Contexts.Single().Value.Mode == "adopt"
                                     && one.Contexts.Single().Value.ProvenancePolicy == "allowUnverified"
                                     && one.Nodes.Count == 2 && one.Nodes.Count(n => n.Kind == "build") == 1,
            "one-repeat benchmark topology");
        Check(one.Nodes[1].Id == "timing_001" && one.Nodes[1].From == "build"
                                              && one.Nodes[1].DependsOn.SequenceEqual(["build"])
                                              && one.Nodes[1].Arguments.SequenceEqual(recipe.Arguments),
            "first timing node or argument preservation");
        WorkflowDocument ten = BenchmarkWorkflowBuilder.Build(recipe with { Repeats = 10 });
        Check(ten.Nodes.Count == 11 && ten.Nodes.Select(n => n.Id).Distinct().Count() == 11
                                    && ten.Nodes.Last().Id == "timing_010", "ten deterministic timing IDs");
        for (int i = 1; i <= 10; i++)
        {
            WorkflowNode node = ten.Nodes[i];
            Check(node.Kind == "run" && node.From == "build"
                                     && node.DependsOn.SequenceEqual([i == 1 ? "build" : $"timing_{i - 1:D3}"])
                                     && node.Resources.SequenceEqual(["measurement-host"])
                                     && node.Arguments.SequenceEqual(recipe.Arguments),
                "sequential benchmark chain " + i);
        }

        Check(WorkflowManifestBytes.Serialize(ten).SequenceEqual(WorkflowManifestBytes.Serialize(
                BenchmarkWorkflowBuilder.Build(recipe with { Repeats = 10 }))),
            "builder serialization is not deterministic");
        Reject(recipe with { Kind = "profile" });
        Reject(recipe with { SchemaVersion = 2 });
        Reject(recipe with { BuildDirectory = "relative" });
        Reject(recipe with { BuildDirectory = "" });
        Reject(recipe with { Target = "" });
        Reject(recipe with { Target = "bad target" });
        Reject(recipe with { Configuration = "" });
        Reject(recipe with { Repeats = 0 });
        Reject(recipe with { Repeats = 1001 });
        Reject(recipe with { TimeoutSeconds = 0 });
        Reject(recipe with { TimeoutSeconds = double.NaN });
        Reject(recipe with { Arguments = ["bad\0argument"] });
        foreach (string? json in new[]
                 {
            /*lang=json,strict*/
                                          """{"kind":"benchmark","schemaVersion":2,"buildDirectory":"/a","target":"x","repeats":1,"timeoutSeconds":1,"arguments":[]}""",
            /*lang=json,strict*/
                                          """{"kind":"benchmark","schemaVersion":1,"buildDirectory":"/a","target":"x","repeats":"ten","timeoutSeconds":1,"arguments":[]}""",
            /*lang=json,strict*/
                                          """{"kind":"benchmark","schemaVersion":1,"buildDirectory":"/a","target":"x","repeats":1,"timeoutSeconds":1,"arguments":[],"unknown":true}""",
            /*lang=json,strict*/
                                          """{"kind":"benchmark","schemaVersion":1,"buildDirectory":"/a","repeats":1,"timeoutSeconds":1,"arguments":[]}"""
                 })
        {
            try
            {
                BenchmarkRecipeV1 parsed = JsonSerializer.Deserialize(json, HostJson.Default.BenchmarkRecipeV1)!;
                _ = BenchmarkWorkflowBuilder.Build(parsed);
                throw new System.Diagnostics.UnreachableException("Malformed recipe accepted: " + json);
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            {
            }
        }

        Check(!Directory.Exists(build), "pure builder performed filesystem I/O");
        Console.WriteLine("Benchmark recipe builder checks passed.");
    }
}