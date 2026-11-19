using System.Text.Json.Nodes;
using CMakeWorkflow.IntegrationTests.Support;
using static CMakeWorkflow.IntegrationTests.Support.TestHarness;

namespace CMakeWorkflow.IntegrationTests.Recipes;

internal static class BenchmarkRecipeTests
{
    internal static void Run()
    {
        using TempTree temp = new();
        string source = Path.Combine(temp.Path, "source");
        CopyFixture("workflow", source);
        string build = Path.Combine(temp.Path, "build with spaces");
        Configure(source, build);
        string manifest = Path.Combine(temp.Path, "generated workflows", "chain with spaces.json");
        string[] options =
        [
            "workflow", "generate", "benchmark", "--build-dir", build,
            "--target", "first", "--config", "Debug", "--repeats", "10",
            "--timeout-seconds", "30", "--output", manifest,
            "--", "sleep", "1", "hello world", "", "--literal", "--recipe"
        ];
        Check(RunCli(options).Contains(manifest, StringComparison.Ordinal) && File.Exists(manifest),
            "benchmark CLI did not generate the selected manifest");
        JsonObject generated = J(File.ReadAllText(manifest));
        Check(S(generated["schemaVersion"]) == "2"
              && S(generated["contexts"]?["benchmark"]?["buildDirectory"]) == build
              && S(generated["nodes"]?[0]?["kind"]) == "build"
              && generated["nodes"]!.AsArray().Count == 11,
            "benchmark CLI generated an invalid workflow document");
        Check(generated["nodes"]![1]!["arguments"]!.AsArray()
                .Select(S).SequenceEqual(["sleep", "1", "hello world", "", "--literal", "--recipe"]),
            "program arguments after -- changed");
        byte[] original = File.ReadAllBytes(manifest);
        Check(RunCli(1, options).Contains("already exists", StringComparison.Ordinal)
              && File.ReadAllBytes(manifest).SequenceEqual(original),
            "existing manifest was overwritten");
        foreach (string[]? broken in new[]
                 {
                     new[]
                     {
                         "workflow", "generate", "benchmark", "--target", "first",
                         "--repeats", "10", "--timeout-seconds", "30", "--output", manifest + ".missing"
                     },
                     [
                         "workflow", "generate", "benchmark", "--build-dir", build,
                         "--target", "first", "--target", "first", "--repeats", "10",
                         "--timeout-seconds", "30", "--output", manifest + ".duplicate"
                     ],
                     [
                         "workflow", "generate", "benchmark", "--build-dir", build,
                         "--target", "bad target", "--repeats", "10",
                         "--timeout-seconds", "30", "--output", manifest + ".invalid"
                     ],
                     [
                         "workflow", "generate", "benchmark", "--build-dir", build,
                         "--target", "first", "--repeats", "0",
                         "--timeout-seconds", "30", "--output", manifest + ".repeats"
                     ]
                 })
        {
            _ = RunCli(1, broken);
            Check(!File.Exists(broken[^1]), "invalid generation wrote a final manifest");
        }

        string relative = Path.Combine("out", "benchmark-recipe-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            _ = RunCli("workflow", "generate", "benchmark", "--build-dir",
                Path.GetRelativePath(Directory.GetCurrentDirectory(), build), "--target", "first",
                "--repeats", "1", "--timeout-seconds", "30", "--output", relative);
            Check(S(J(File.ReadAllText(relative))["contexts"]?["benchmark"]?["buildDirectory"])
                  == build, "CLI-relative build directory was not resolved from cwd");
        }
        finally
        {
            if (File.Exists(relative))
            {
                File.Delete(relative);
            }
        }

        Check(RunCli("workflow", "plan", manifest, "--json").Contains("timing_010",
            StringComparison.Ordinal), "existing planner rejected generated benchmark workflow");
        (JsonObject? report, string? runDirectory) = Workflow(temp.Path, generated, name: "benchmark-generated",
            options: ["--jobs", "8"]);
        AllSucceeded(report);
        JsonNode?[] timing = [.. report["nodes"]!.AsArray().Where(n => S(n?["kind"]) == "run")];
        Check(timing.Length == 10 && report["nodes"]!.AsArray().Count(n => S(n?["kind"]) == "build") == 1,
            "generated workflow did not build once and run ten times");
        string[] invocations = File.ReadAllLines(Path.Combine(build, "sleep-invocations.txt"));
        Check(invocations.Length == 10, "benchmark executable invocation count differs from repeats");
        for (int i = 0; i < timing.Length; i++)
        {
            JsonNode node = timing[i]!;
            Check(S(node["id"]) == $"timing_{i + 1:D3}"
                  && Log(report, runDirectory, $"timing_{i + 1:D3}")
                      .Contains("[hello world][][--literal][--recipe]", StringComparison.Ordinal),
                "timing execution order or arguments changed");
            if (i > 0)
            {
                Check(DateTimeOffset.Parse(S(timing[i - 1]?["finishedAt"]), System.Globalization.CultureInfo.CurrentCulture)
                      <= DateTimeOffset.Parse(S(node["startedAt"]), System.Globalization.CultureInfo.CurrentCulture),
                    "timing nodes overlapped with --jobs 8");
            }
        }

        Pass("typed benchmark generation, planner and ten sequential native executions");
        RunMetrics(temp.Path, build);
    }

    private static void RunMetrics(string root, string build)
    {
        JsonObject recipe = J("""
            {"kind":"benchmark","schemaVersion":1,"target":"first","configuration":"Debug",
             "repeats":2,"timeoutSeconds":30,"arguments":["metrics"],
             "metrics":{"source":"stdout","format":"json","fields":[
               {"path":"run_p50_us","name":"runtime_p50","unit":"us","direction":"lower-is-better"},
               {"path":"stats.rate","name":"rate","unit":"tasks/s","direction":"higher-is-better"}]}}
            """);
        recipe["buildDirectory"] = build;
        string recipeFile = Write(Path.Combine(root, "metrics-recipe.json"), recipe.ToJsonString());
        string manifest = Path.Combine(root, "metrics-generated.json");
        _ = RunCli("workflow", "generate", "benchmark", "--recipe", recipeFile, "--output", manifest);
        JsonObject workflow = J(File.ReadAllText(manifest));
        string database = Path.Combine(root, "benchmark-metrics.sqlite");
        (JsonObject report, string directory) = Workflow(root, workflow, name: "metrics",
            options: ["--history-db", database]);
        AllSucceeded(report);
        Check(Item(report, "timing_001")["metrics"]?[1]?["value"]?.GetValue<double>() == 800,
            "nested stdout metric is missing from run.json");
        JsonArray measurements = JsonNode.Parse(RunCli("history", "query", "--metric", "runtime_p50",
            "--db", database, "--json"))!.AsArray();
        Check(measurements.Count == 2 && measurements.All(row => row?["value"]?.GetValue<double>() == 12.5
            && S(row?["unit"]) == "us" && S(row?["direction"]) == "lower-is-better")
            && measurements.Select(row => row?["sampleIndex"]?.GetValue<int>()).SequenceEqual([0, 1]),
            "automatic History import lost repeated stdout metrics or their metadata");
        string rebuilt = Path.Combine(root, "benchmark-reimport.sqlite");
        // Rebuilding the index uses checkpointed values even if a log is edited.
        File.WriteAllText(Path.Combine(directory, S(Item(report, "timing_001")["stdout"])), "{}");
        _ = RunCli("history", "import", directory, "--db", rebuilt);
        Check(JsonNode.Parse(RunCli("history", "query", "--metric", "runtime_p50",
                  "--db", rebuilt, "--json"))!.AsArray().Count == 2
              && RunCli("history", "import", directory, "--db", rebuilt).Contains("unchanged 1", StringComparison.Ordinal),
            "explicit History import did not recover durable metrics idempotently");

        workflow["nodes"]![1]!["metrics"]!["fields"]![1]!["path"] = "stats.missing";
        string failedDb = Path.Combine(root, "missing-metric.sqlite");
        (JsonObject failed, string _) = Workflow(root, workflow, expected: 1, name: "missing-metric",
            options: ["--history-db", failedDb]);
        Check(S(Item(failed, "timing_001")["error"]).Contains("stats.missing", StringComparison.Ordinal)
              && Item(failed, "timing_001")["metrics"] is null
              && JsonNode.Parse(RunCli("history", "query", "--metric", "runtime_p50", "--db",
                  failedDb, "--json"))!.AsArray().Count == 0,
            "failed extraction indexed partial samples");

        recipe["metrics"]!["fields"]![1]!["name"] = "runtime_p50";
        _ = Write(recipeFile, recipe.ToJsonString());
        string rejected = Path.Combine(root, "duplicate-metric.json");
        _ = RunCli(1, "workflow", "generate", "benchmark", "--recipe", recipeFile, "--output", rejected);
        Check(!File.Exists(rejected), "duplicate metric names left a manifest");
        Pass("benchmark stdout metrics extraction, recovery and History import");
    }
}
