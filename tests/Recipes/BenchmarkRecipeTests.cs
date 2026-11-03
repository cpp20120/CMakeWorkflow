using System.Text.Json.Nodes;
using static CMakeWorkflow.IntegrationTests.TestHarness;

namespace CMakeWorkflow.IntegrationTests;

internal static class BenchmarkRecipeTests
{
    internal static void Run()
    {
        using var temp = new TempTree();
        var source = Path.Combine(temp.Path, "source");
        CopyFixture("workflow", source);
        var build = Path.Combine(temp.Path, "build with spaces");
        Configure(source, build);
        var manifest = Path.Combine(temp.Path, "generated workflows", "chain with spaces.json");
        var options = new[]
        {
            "workflow", "generate", "benchmark", "--build-dir", build,
            "--target", "first", "--config", "Debug", "--repeats", "10",
            "--timeout-seconds", "30", "--output", manifest,
            "--", "sleep", "1", "hello world", "", "--literal", "--recipe"
        };
        Check(RunCli(options).Contains(manifest, StringComparison.Ordinal) && File.Exists(manifest),
            "benchmark CLI did not generate the selected manifest");
        var generated = J(File.ReadAllText(manifest));
        Check(S(generated["schemaVersion"]) == "2"
              && S(generated["contexts"]?["benchmark"]?["buildDirectory"]) == build
              && S(generated["nodes"]?[0]?["kind"]) == "build"
              && generated["nodes"]!.AsArray().Count == 11,
            "benchmark CLI generated an invalid workflow document");
        Check(generated["nodes"]![1]!["arguments"]!.AsArray()
                .Select(S).SequenceEqual(["sleep", "1", "hello world", "", "--literal", "--recipe"]),
            "program arguments after -- changed");
        var original = File.ReadAllBytes(manifest);
        Check(RunCli(1, options).Contains("already exists", StringComparison.Ordinal)
              && File.ReadAllBytes(manifest).SequenceEqual(original),
            "existing manifest was overwritten");
        foreach (var broken in new[]
                 {
                     new[]
                     {
                         "workflow", "generate", "benchmark", "--target", "first",
                         "--repeats", "10", "--timeout-seconds", "30", "--output", manifest + ".missing"
                     },
                     new[]
                     {
                         "workflow", "generate", "benchmark", "--build-dir", build,
                         "--target", "first", "--target", "first", "--repeats", "10",
                         "--timeout-seconds", "30", "--output", manifest + ".duplicate"
                     },
                     new[]
                     {
                         "workflow", "generate", "benchmark", "--build-dir", build,
                         "--target", "bad target", "--repeats", "10",
                         "--timeout-seconds", "30", "--output", manifest + ".invalid"
                     },
                     new[]
                     {
                         "workflow", "generate", "benchmark", "--build-dir", build,
                         "--target", "first", "--repeats", "0",
                         "--timeout-seconds", "30", "--output", manifest + ".repeats"
                     }
                 })
        {
            RunCli(1, broken);
            Check(!File.Exists(broken[^1]), "invalid generation wrote a final manifest");
        }

        var relative = Path.Combine("out", "benchmark-recipe-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            RunCli("workflow", "generate", "benchmark", "--build-dir",
                Path.GetRelativePath(Directory.GetCurrentDirectory(), build), "--target", "first",
                "--repeats", "1", "--timeout-seconds", "30", "--output", relative);
            Check(S(J(File.ReadAllText(relative))["contexts"]?["benchmark"]?["buildDirectory"])
                  == build, "CLI-relative build directory was not resolved from cwd");
        }
        finally
        {
            if (File.Exists(relative)) File.Delete(relative);
        }

        Check(RunCli("workflow", "plan", manifest, "--json").Contains("timing_010",
            StringComparison.Ordinal), "existing planner rejected generated benchmark workflow");
        var (report, runDirectory) = Workflow(temp.Path, generated, name: "benchmark-generated",
            options: ["--jobs", "8"]);
        AllSucceeded(report);
        var timing = report["nodes"]!.AsArray().Where(n => S(n?["kind"]) == "run").ToArray();
        Check(timing.Length == 10 && report["nodes"]!.AsArray().Count(n => S(n?["kind"]) == "build") == 1,
            "generated workflow did not build once and run ten times");
        var invocations = File.ReadAllLines(Path.Combine(build, "sleep-invocations.txt"));
        Check(invocations.Length == 10, "benchmark executable invocation count differs from repeats");
        for (var i = 0; i < timing.Length; i++)
        {
            var node = timing[i]!;
            Check(S(node["id"]) == $"timing_{i + 1:D3}"
                  && Log(report, runDirectory, $"timing_{i + 1:D3}")
                      .Contains("[hello world][][--literal][--recipe]", StringComparison.Ordinal),
                "timing execution order or arguments changed");
            if (i > 0)
                Check(DateTimeOffset.Parse(S(timing[i - 1]?["finishedAt"]))
                      <= DateTimeOffset.Parse(S(node["startedAt"])),
                    "timing nodes overlapped with --jobs 8");
        }

        Pass("typed benchmark generation, planner and ten sequential native executions");
    }
}