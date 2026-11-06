using System.Text.Json;
using System.Text.Json.Nodes;
using CMakeWorkflow.IntegrationTests.Support;
using static CMakeWorkflow.IntegrationTests.Support.TestHarness;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.IntegrationTests.Recipes;
#pragma warning restore IDE0130

internal static class TypedRecipeIntegrationTests
{
    private static readonly string[] BuildTargets =
    [
    "first"
];
    private static readonly string[] MatrixArguments =
    [
    "sleep",
    "40"
];
    private static readonly string[] MatrixCaseValues =
    [
    "chain",
    "noop"
];
    private static readonly string[] ProfileArguments =
    [
    "args",
    "a b"
];
    internal static void Run()
    {
        using TempTree temp = new();
        string source = Path.Combine(temp.Path, "source");
        CopyFixture("workflow", source);
        File.AppendAllText(Path.Combine(source, "CMakeLists.txt"), """

                                                                   add_executable(third first.cpp)
                                                                   add_test(NAME unit_case COMMAND "${CMAKE_COMMAND}" -E echo UNIT_MARKER)
                                                                   set_tests_properties(unit_case PROPERTIES LABELS unit)
                                                                   add_test(NAME integration_case COMMAND "${CMAKE_COMMAND}" -E echo INTEGRATION_MARKER)
                                                                   set_tests_properties(integration_case PROPERTIES LABELS integration)
                                                                   """);
        string firstBuild = Path.Combine(temp.Path, "variant one");
        string secondBuild = Path.Combine(temp.Path, "variant two");
        Configure(source, firstBuild);
        Configure(source, secondBuild);
        string definitions = Path.Combine(temp.Path, "definitions");
        _ = Directory.CreateDirectory(definitions);

        string testManifest = Path.Combine(temp.Path, "test-matrix.json");
        _ = RunCli("workflow", "generate", "test-matrix", "--build-dir", firstBuild,
            "--target", "first", "--config", "Debug", "--label", "unit",
            "--label", "integration", "--order", "sequential",
            "--timeout-seconds", "30", "--output", testManifest);
        Check(RunCli("workflow", "plan", testManifest, "--json").Contains("ctest_integration",
            StringComparison.Ordinal), "Test Matrix manifest rejected by planner");
        (JsonObject? tests, string? testDirectory) = Workflow(temp.Path, J(File.ReadAllText(testManifest)),
            name: "test-matrix", options: ["--jobs", "8"]);
        AllSucceeded(tests);
        Check(Log(tests, testDirectory, "ctest_unit").Contains("unit_case", StringComparison.Ordinal)
              && !Log(tests, testDirectory, "ctest_unit").Contains("integration_case", StringComparison.Ordinal)
              && Log(tests, testDirectory, "ctest_integration").Contains("integration_case", StringComparison.Ordinal),
            "CTest groups selected the wrong tests");
        Check(DateTimeOffset.Parse(S(Item(tests, "ctest_unit")["finishedAt"]), System.Globalization.CultureInfo.CurrentCulture)
              <= DateTimeOffset.Parse(S(Item(tests, "ctest_integration")["startedAt"]), System.Globalization.CultureInfo.CurrentCulture),
            "Serialized CTest groups overlapped");

        string buildManifest = Path.Combine(temp.Path, "build-matrix.json");
        _ = RunCli("workflow", "generate", "build-matrix", "--variant", "one=" + firstBuild,
            "--variant", "two=" + secondBuild, "--target", "first", "--target", "third",
            "--config", "Debug", "--output", buildManifest);
        // Treat the CLI plan as the canonical path contract. The test project
        // intentionally does not link CMakeWorkflow.CMake: this is black-box E2E.
        JsonObject buildPlan = J(RunCli("workflow", "plan", buildManifest, "--json"));
        JsonArray planned = buildPlan["nodes"]!.AsArray();
        Check(planned.Count == 4 && planned.Any(n => S(n?["id"]) == "build_two_002"),
            "Build Matrix manifest rejected by planner");
        JsonNode manifestContexts = J(File.ReadAllText(buildManifest))["contexts"]!;
        Check(S(manifestContexts["one"]?["buildDirectory"]) == firstBuild
              && S(manifestContexts["two"]?["buildDirectory"]) == secondBuild,
            "Build Matrix recipe did not preserve input build directories");
        string PlannedDirectory(string id) => S(planned.Single(n => S(n?["id"]) == id)?["buildDirectory"]);
        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        string firstPlannedDirectory = PlannedDirectory("build_one_001");
        string secondPlannedDirectory = PlannedDirectory("build_two_001");
        (JsonObject? builds, string _) = Workflow(temp.Path, J(File.ReadAllText(buildManifest)),
            name: "build-matrix", options: ["--jobs", "8"]);
        AllSucceeded(builds);
        JsonArray actualNodes = builds["nodes"]!.AsArray();
        Check(actualNodes.Count == 4
              && !pathComparer.Equals(firstPlannedDirectory, secondPlannedDirectory)
              && actualNodes.All(n => pathComparer.Equals(
                  S(n?["buildDirectory"]), PlannedDirectory(S(n?["id"])))),
            "Build Matrix mixed build contexts: plan and run disagree on canonical build directories");

        string testRecipeFile = Path.Combine(definitions, "tests.json");
        File.WriteAllText(testRecipeFile, JsonSerializer.Serialize(new
        {
            kind = "test-matrix",
            schemaVersion = 1,
            buildDirectory = Path.GetRelativePath(definitions, firstBuild),
            target = "first",
            configuration = "Debug",
            groups = new[] { new { name = "unit", labelRegex = "unit" } },
            executionOrder = "parallel",
            timeoutSeconds = 30
        }));
        string testFromFile = Path.Combine(temp.Path, "test-from-file.json");
        _ = RunCli("workflow", "generate", "test-matrix", "--recipe", testRecipeFile,
            "--output", testFromFile);
        Check(S(J(File.ReadAllText(testFromFile))["contexts"]?["tests"]?["buildDirectory"])
              == firstBuild, "Test Matrix recipe-file path policy");
        string buildRecipeFile = Path.Combine(definitions, "builds.json");
        File.WriteAllText(buildRecipeFile, JsonSerializer.Serialize(new
        {
            kind = "build-matrix",
            schemaVersion = 1,
            variants = new[]
            {
                new
                {
                    name = "one", buildDirectory = Path.GetRelativePath(definitions, firstBuild),
                    configuration = "Debug"
                }
            },
            targets = BuildTargets
        }));
        string buildFromFile = Path.Combine(temp.Path, "build-from-file.json");
        _ = RunCli("workflow", "generate", "build-matrix", "--recipe", buildRecipeFile,
            "--output", buildFromFile);
        Check(S(J(File.ReadAllText(buildFromFile))["contexts"]?["one"]?["buildDirectory"])
              == firstBuild, "Build Matrix recipe-file path policy");

        string matrixRecipe = Path.Combine(definitions, "benchmark-matrix.json");
        File.WriteAllText(matrixRecipe, JsonSerializer.Serialize(new
        {
            kind = "benchmark-matrix",
            schemaVersion = 1,
            variants = new[]
            {
                new
                {
                    name = "one", buildDirectory = Path.GetRelativePath(definitions, firstBuild),
                    configuration = "Debug"
                },
                new
                {
                    name = "two", buildDirectory = Path.GetRelativePath(definitions, secondBuild),
                    configuration = "Debug"
                }
            },
            target = "first",
            repeats = 2,
            timeoutSeconds = 30,
            fixedArguments = MatrixArguments,
            axes = new[] { new { name = "case", flag = "--case", values = MatrixCaseValues } },
            executionOrder = "round-robin"
        }));
        string matrixManifest = Path.Combine(temp.Path, "benchmark-matrix.json");
        _ = RunCli("workflow", "generate", "benchmark-matrix", "--recipe", matrixRecipe,
            "--output", matrixManifest);
        JsonObject matrixDocument = J(File.ReadAllText(matrixManifest));
        Check(S(matrixDocument["contexts"]?["one"]?["buildDirectory"]) == firstBuild
              && matrixDocument["nodes"]!.AsArray().Count == 10,
            "Benchmark Matrix recipe-relative paths or expansion changed");
        Check(RunCli("workflow", "plan", matrixManifest, "--json").Contains("timing_two_0002_002",
            StringComparison.Ordinal), "Benchmark Matrix manifest rejected by planner");
        (JsonObject? matrix, string _) = Workflow(temp.Path, matrixDocument, name: "benchmark-matrix",
            options: ["--jobs", "8"]);
        AllSucceeded(matrix);
        JsonNode?[] timings = [.. matrix["nodes"]!.AsArray().Where(n => S(n?["kind"]) == "run")];
        Check(timings.Length == 8 && File.ReadAllLines(Path.Combine(firstBuild, "sleep-invocations.txt")).Length == 4
                                  && File.ReadAllLines(Path.Combine(secondBuild, "sleep-invocations.txt")).Length == 4,
            "Benchmark Matrix invocation count changed");
        for (int i = 1; i < timings.Length; i++)
        {
            Check(DateTimeOffset.Parse(S(timings[i - 1]?["finishedAt"]), System.Globalization.CultureInfo.CurrentCulture)
                  <= DateTimeOffset.Parse(S(timings[i]?["startedAt"]), System.Globalization.CultureInfo.CurrentCulture),
                "Benchmark Matrix timing executions overlapped with --jobs 8");
        }

        string invalid = Path.Combine(temp.Path, "oversized.json");
        File.WriteAllText(invalid, JsonSerializer.Serialize(new
        {
            kind = "benchmark-matrix",
            schemaVersion = 1,
            variants = new[] { new { name = "one", buildDirectory = firstBuild } },
            target = "first",
            repeats = 1000,
            timeoutSeconds = 30,
            fixedArguments = Array.Empty<string>(),
            axes = new[]
            {
                new
                {
                    name = "huge", flag = "--huge", values = Enumerable.Range(0, 32)
                        .Select(i => i.ToString(System.Globalization.CultureInfo.CurrentCulture)).ToArray()
                }
            },
            executionOrder = "sequential"
        }));
        string rejectedOutput = Path.Combine(temp.Path, "rejected-matrix.json");
        Check(RunCli(1, "workflow", "generate", "benchmark-matrix", "--recipe", invalid,
                  "--output", rejectedOutput).Contains("expands to", StringComparison.Ordinal)
              && !File.Exists(rejectedOutput), "Oversized matrix left an output file");
        string malformed = Path.Combine(temp.Path, "malformed-recipe.json");
        File.WriteAllText(malformed, /*lang=json,strict*/ """
                                     {"kind":"test-matrix","schemaVersion":2,"buildDirectory":"x","target":"first",
                                      "groups":[{"name":"unit","labelRegex":"unit"}],"executionOrder":"parallel",
                                      "timeoutSeconds":30,"unknown":true}
                                     """);
        string malformedOutput = Path.Combine(temp.Path, "malformed-output.json");
        _ = RunCli(1, "workflow", "generate", "test-matrix", "--recipe", malformed,
            "--output", malformedOutput);
        Check(!File.Exists(malformedOutput), "Malformed typed recipe wrote a final manifest");
        _ = RunCli(1, "workflow", "generate", "test-matrix", "--build-dir", firstBuild,
            "--target", "first", "--target", "first", "--label", "unit",
            "--output", malformedOutput);
        Check(!File.Exists(malformedOutput), "Duplicate CLI option wrote a final manifest");

        string incompatiblePlatform = OperatingSystem.IsWindows() ? "macos" : "windows";
        string profileRecipeFile = Path.Combine(definitions, "profile.json");
        File.WriteAllText(profileRecipeFile, JsonSerializer.Serialize(new
        {
            kind = "profile",
            schemaVersion = 1,
            variants = new[] { new { name = "main", buildDirectory = Path.GetRelativePath(definitions, firstBuild) } },
            target = "first",
            configuration = "Debug",
            repeats = 1,
            timeoutSeconds = 10,
            arguments = ProfileArguments,
            backend = "auto",
            mode = "cpu",
            platform = incompatiblePlatform
        }));
        string profileFromFile = Path.Combine(temp.Path, "profile-from-file.json");
        _ = RunCli("workflow", "generate", "profile", "--recipe", profileRecipeFile,
            "--output", profileFromFile);
        Check(S(J(File.ReadAllText(profileFromFile))["contexts"]?["main"]?["buildDirectory"])
              == firstBuild, "Profile recipe-file path policy");
        string profileManifest = Path.Combine(temp.Path, "profile.json");
        _ = RunCli("workflow", "generate", "profile", "--build-dir", firstBuild,
            "--target", "first", "--config", "Debug", "--platform", incompatiblePlatform,
            "--repeats", "1", "--backend", "auto", "--mode", "cpu",
            "--timeout-seconds", "10", "--output", profileManifest,
            "--", "args", "a b");
        Check(RunCli("workflow", "plan", profileManifest, "--json").Contains("capture_main",
            StringComparison.Ordinal), "Profile recipe manifest rejected by planner");
        (JsonObject? profile, string _) = Workflow(temp.Path, J(File.ReadAllText(profileManifest)),
            expected: 1, name: "profile-recipe", options: ["--jobs", "8"]);
        Check(Status(profile, "build_main") == "succeeded"
              && Status(profile, "timing_main_1") == "succeeded"
              && Status(profile, "capture_main") == "failed",
            "Profile capture failure did not preserve timing/report semantics");
        File.AppendAllText(Path.Combine(source, "first.cpp"), "\nthis_is_intentionally_invalid_cpp\n");
        (JsonObject? blocked, string _) = Workflow(temp.Path, J(File.ReadAllText(testManifest)),
            expected: 1, name: "test-matrix-failed-build", options: ["--jobs", "8"]);
        Check(Status(blocked, "build_tests") == "failed"
              && Status(blocked, "ctest_unit") != "succeeded"
              && Status(blocked, "ctest_integration") != "succeeded",
            "Failed build did not block generated CTest groups");
        Pass("typed Profile, Test Matrix, Build Matrix and Benchmark Matrix generation and execution");
    }
}