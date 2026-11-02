using CMakeWorkflow;
using CMakeWorkflow.Profiling;
using CMakeWorkflow.Host;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

internal static class TypedRecipeTests
{
    private static void Check(bool valid, string message)
    { if (!valid) throw new Exception(message); }

    private static void Reject(Func<WorkflowDocument> build)
    {
        try { _ = build(); }
        catch (ArgumentException) { return; }
        throw new Exception("Invalid recipe was accepted.");
    }

    private static void RoundTrip<T>(T request, JsonTypeInfo<T> metadata)
    {
        var json = JsonSerializer.Serialize(request, metadata);
        var parsed = JsonSerializer.Deserialize(json, metadata);
        Check(parsed is not null && JsonSerializer.Serialize(parsed, metadata) == json,
            "Typed Host recipe JSON source generation mismatch");
    }

    public static void Run(string root)
    {
        var a = Path.Combine(root, "recipe-a");
        var b = Path.Combine(root, "recipe-b");
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var profile = new ProfileRecipeV1("profile", 1, [new("a", a), new("b", b)],
            "worker", "Debug", 2, 10, ["--case", "a b"], "auto", "diagnostic", platform);
        var profileGraph = ProfileRecipeBuilder.Build(profile);
        Check(profileGraph.SchemaVersion == 2 && profileGraph.Nodes.Count(n => n.Kind == "build") == 2
            && profileGraph.Nodes.Count(n => n.Kind == "run") == 4
            && profileGraph.Nodes.Any(n => n.Kind == "profile")
            && profileGraph.Nodes.Where(n => n.Kind == "run").All(n => n.Resources.Contains("measurement-host")),
            "Profile recipe topology/resources");
        Check(profileGraph.Nodes.Where(n => n.Kind == "run").Select(n => n.Id).SequenceEqual(
            ["timing_a_1", "timing_b_1", "timing_b_2", "timing_a_2"]),
            "Profile alternating variant order");
        Check(WorkflowManifestBytes.Serialize(profileGraph).SequenceEqual(WorkflowManifestBytes.Serialize(
            ProfileRecipeBuilder.Build(profile))), "Profile graph nondeterministic");
        var otherPlatform = platform == "linux" ? "windows" : "linux";
        var remoteGraph = ProfileRecipeBuilder.Build(profile with { Platform = otherPlatform, Mode = "cpu" });
        Check(remoteGraph.Nodes.Where(n => n.Kind == "profile").All(n =>
            n.Options["backend"] == (otherPlatform == "linux" ? "perf" : "etw")),
            "Profile auto backend depended on generator host OS");
        Reject(() => ProfileRecipeBuilder.Build(profile with { Platform = "other" }));
        Reject(() => ProfileRecipeBuilder.Build(profile with { Backend = platform == "linux" ? "etw" : "perf" }));
        Reject(() => ProfileRecipeBuilder.Build(profile with { Variants = [new("a", a), new("a", b)] }));

        var tests = new TestMatrixRecipeV1("test-matrix", 1, a, "worker", "Debug",
            [new("unit", LabelRegex: "unit"), new("stress", TestRegex: "stress_.*")], "parallel", 30);
        var testGraph = TestMatrixWorkflowBuilder.Build(tests);
        Check(testGraph.Nodes.Count == 3 && testGraph.Nodes[0].Kind == "build"
            && testGraph.Nodes.Skip(1).All(n => n.Kind == "ctest"
                && n.DependsOn.SequenceEqual(["build_tests"]))
            && testGraph.Nodes[1].LabelRegex == "unit"
            && testGraph.Nodes[2].TestRegex == "stress_.*", "Test Matrix parallel graph");
        var serial = TestMatrixWorkflowBuilder.Build(tests with { ExecutionOrder = "sequential" });
        Check(serial.Nodes[2].DependsOn.SequenceEqual(["ctest_unit"]), "Test Matrix serial graph");
        Reject(() => TestMatrixWorkflowBuilder.Build(tests with { Groups = [] }));
        Reject(() => TestMatrixWorkflowBuilder.Build(tests with { Groups = [new("x", "foo", "bar")] }));
        Reject(() => TestMatrixWorkflowBuilder.Build(tests with { Groups = [new("x", TestRegex: "[")] }));
        Reject(() => TestMatrixWorkflowBuilder.Build(tests with { Groups = [new("x", LabelRegex: "a"), new("x", LabelRegex: "b")] }));
        Reject(() => TestMatrixWorkflowBuilder.Build(tests with { Groups = Enumerable.Range(0, 65)
            .Select(i => new TestMatrixGroupV1("g" + i, LabelRegex: "x")).ToArray() }));

        var variants = new[] { new BuildVariantV1("debug", a, "Debug"),
            new BuildVariantV1("release", b, "Release") };
        var build = new BuildMatrixRecipeV1("build-matrix", 1, variants, ["worker", "other"]);
        var buildGraph = BuildMatrixWorkflowBuilder.Build(build);
        Check(buildGraph.Contexts.Count == 2 && buildGraph.Nodes.Count == 4
            && buildGraph.Nodes.All(n => n.Kind == "build" && n.DependsOn.Count == 0)
            && buildGraph.Nodes.Select(n => n.Id).SequenceEqual(
                ["build_debug_001", "build_debug_002", "build_release_001", "build_release_002"]),
            "Build Matrix contexts/topology");
        Reject(() => BuildMatrixWorkflowBuilder.Build(build with
        { Variants = [variants[0], variants[1] with { BuildDirectory = a }] }));
        Reject(() => BuildMatrixWorkflowBuilder.Build(build with { Targets = ["worker", "worker"] }));
        Reject(() => BuildMatrixWorkflowBuilder.Build(build with
        { Targets = Enumerable.Range(0, 300).Select(i => "t" + i).ToArray() }));

        var matrix = new BenchmarkMatrixRecipeV1("benchmark-matrix", 1, variants, "worker", 2, 30,
            ["--literal", "a b"], [new("case", "--case", ["chain", "noop"]),
                new("workers", "--workers", ["1", "2"])], "round-robin");
        var graph = BenchmarkMatrixWorkflowBuilder.Build(matrix);
        var timing = graph.Nodes.Where(n => n.Kind == "run").ToArray();
        Check(graph.SchemaVersion == 2 && graph.Nodes.Count == 18 && timing.Length == 16
            && timing.All(n => n.Resources.SequenceEqual(["measurement-host"])
                && n.Arguments.Take(2).SequenceEqual(["--literal", "a b"]))
            && timing[0].Id == "timing_debug_0001_001"
            && timing[1].Id == "timing_release_0001_001"
            && timing[8].Id == "timing_release_0001_002"
            && timing.Skip(1).Select((n, i) => n.DependsOn.SequenceEqual([timing[i].Id])).All(x => x),
            "Benchmark Matrix order, isolation or argv");
        Check(WorkflowManifestBytes.Serialize(graph).SequenceEqual(WorkflowManifestBytes.Serialize(
            BenchmarkMatrixWorkflowBuilder.Build(matrix))), "Benchmark Matrix graph nondeterministic");
        var sequential = BenchmarkMatrixWorkflowBuilder.Build(matrix with { ExecutionOrder = "sequential" });
        Check(sequential.Nodes[2].Id == "timing_debug_0001_001"
            && sequential.Nodes[3].Id == "timing_debug_0001_002", "Benchmark Matrix sequential order");
        Reject(() => BenchmarkMatrixWorkflowBuilder.Build(matrix with { Repeats = 0 }));
        Reject(() => BenchmarkMatrixWorkflowBuilder.Build(matrix with { Axes = [new("bad", "workers", ["1"])] }));
        Reject(() => BenchmarkMatrixWorkflowBuilder.Build(matrix with
        { Axes = [new("huge", "--huge", Enumerable.Range(0, 32).Select(i => i.ToString()).ToArray())],
          Repeats = 100 }));
        var id = Guid.NewGuid().ToString("N");
        RoundTrip(new ProfileRecipePrepareRequest(id, Path.Combine(root, "p.json"), profile),
            HostJson.Default.ProfileRecipePrepareRequest);
        RoundTrip(new TestMatrixPrepareRequest(id, Path.Combine(root, "t.json"), tests),
            HostJson.Default.TestMatrixPrepareRequest);
        RoundTrip(new BuildMatrixPrepareRequest(id, Path.Combine(root, "b.json"), build),
            HostJson.Default.BuildMatrixPrepareRequest);
        RoundTrip(new BenchmarkMatrixPrepareRequest(id, Path.Combine(root, "m.json"), matrix),
            HostJson.Default.BenchmarkMatrixPrepareRequest);
        Check(!Directory.Exists(a) && !Directory.Exists(b), "Pure recipe builders performed filesystem I/O");
        Console.WriteLine("Typed recipe builder checks passed.");
    }
}
