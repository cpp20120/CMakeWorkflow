using System.Diagnostics;
using System.Text.Json;
using CMakeWorkflow;
using CMakeWorkflow.Host;
using CMakeWorkflow.Client;

internal static class ProfileWorkflowWireTests
{
    public static async Task RunAsync(string endpoint, string parent, CancellationToken cancellationToken)
    {
        var workspace = Path.Combine(parent, "profile-wire-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(workspace, "source");
        var build = Path.Combine(workspace, "build");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(source, "CMakeLists.txt"),
            "cmake_minimum_required(VERSION 3.26)\nproject(profile_wire LANGUAGES CXX)\nadd_executable(worker worker.cpp)\n");
        File.WriteAllText(Path.Combine(source, "worker.cpp"), "int main() { return 0; }\n");
        var query = Path.Combine(build, ".cmake", "api", "v1", "query");
        Directory.CreateDirectory(query);
        File.WriteAllText(Path.Combine(query, "codemodel-v2"), "");
        var start = new ProcessStartInfo("cmake")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug" })
            start.ArgumentList.Add(arg);
        using (var process = Process.Start(start) ?? throw new Exception("Cannot start CMake."))
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new Exception("Profile wire fixture configure failed: " + await stdout + await stderr);
            await Task.WhenAll(stdout, stderr);
        }

        using var wire = await LocalTransport.ConnectAsync(endpoint, cancellationToken);
        using var rpc = HostConnection.Create(wire);
        var client = rpc.Attach<IHostRpc>();
        rpc.StartListening();
        await client.InitializeAsync(new(1, workspace), cancellationToken);
        // The platform-incompatible backend makes capture fail deterministically
        // after Host dispatches it. The timing executable must still complete.
        var backend = OperatingSystem.IsWindows() ? "perf" : "etw";
        var prepared = await client.ProfilePrepareAsync(new(
            Guid.NewGuid().ToString("N"), Path.Combine(workspace, "definitions"),
            "worker", [new("main", build)], Repeats: 1, TimeoutSeconds: 5,
            Backend: backend), cancellationToken);
        var planned = await client.WorkflowPlanAsync(new(prepared.Manifest), cancellationToken);
        if (!planned.Valid || !planned.Nodes.Any(node => node.Kind == "run") ||
            !planned.Nodes.Any(node => node.Kind == "profile"))
            throw new Exception("Profile preparation did not produce a valid timing/capture workflow.");
        var runDirectory = Path.Combine(workspace, "run");
        var accepted = await client.WorkflowStartAsync(new(
            "profile-wire-start", prepared.Manifest, prepared.ManifestSha256, 1, runDirectory), cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        OperationReference current;
        do
        {
            current = await client.OperationGetAsync(new(accepted.OperationId), deadline.Token);
            if (current.State is "failed" or "succeeded" or "canceled") break;
            await Task.Delay(50, deadline.Token);
        } while (true);

        if (current.State != "failed")
            throw new Exception("Profile capture fixture did not reach expected terminal failure.");
        var report = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(
                             Path.Combine(runDirectory, "run.json"), cancellationToken),
                         WorkflowJson.Default.WorkflowReport)
                     ?? throw new Exception("Missing profile workflow report.");
        var timing = report.Nodes.Single(node => node.Id == "timing_main_1");
        var capture = report.Nodes.Single(node => node.Id == "capture_main");
        if (timing.Status != "succeeded" || capture.Status != "failed" ||
            capture.Error is null || !capture.Error.Contains("Profiling backend", StringComparison.Ordinal))
            throw new Exception("Host did not route profile capture through ProfileWorkflowExecutor.");
        // Native target UI uses typed prepare + the SAME durable workflow/start.
        var targetReady = await client.TargetPrepareAsync(new(Guid.NewGuid().ToString("N"),
            Path.Combine(workspace, "target-definitions"), "run", build, "worker"), cancellationToken);
        var targetPlan = await client.WorkflowPlanAsync(new(targetReady.Manifest), cancellationToken);
        if (!targetPlan.Valid || targetPlan.Nodes.Count != 2
                              || targetPlan.Nodes[0].Kind != "build" || targetPlan.Nodes[1].Kind != "run")
            throw new Exception("Target preparation failed to produce a build/run DAG.");
        var targetOutput = Path.Combine(workspace, "target-run");
        var targetAccepted = await client.WorkflowStartAsync(new(Guid.NewGuid().ToString("N"),
            targetReady.Manifest, targetReady.ManifestSha256, 1, targetOutput), cancellationToken);
        using var targetDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        targetDeadline.CancelAfter(TimeSpan.FromSeconds(20));
        OperationReference targetState;
        do
        {
            targetState = await client.OperationGetAsync(new(targetAccepted.OperationId), targetDeadline.Token);
            if (targetState.State is "failed" or "succeeded" or "canceled") break;
            await Task.Delay(50, targetDeadline.Token);
        } while (true);

        if (targetState.State != "succeeded")
            throw new Exception("Typed target build/run did not complete: " + targetState.State);
        var benchmarkRecipe = new CMakeWorkflow.BenchmarkRecipeV1("benchmark", 1, build, "worker", "Debug",
            2, 5, ["one argument", ""]);
        var benchmarkReady = await client.BenchmarkPrepareAsync(new(Guid.NewGuid().ToString("N"),
                Path.Combine(workspace, "benchmark definitions", "benchmark.json"), benchmarkRecipe),
            cancellationToken);
        var benchmarkPlan = await client.WorkflowPlanAsync(new(benchmarkReady.Manifest), cancellationToken);
        if (!benchmarkPlan.Valid || benchmarkPlan.Nodes.Count != 3)
            throw new Exception("Typed Benchmark preparation did not produce the expected DAG.");
        var benchmarkRun = Path.Combine(workspace, "benchmark-run");
        var benchmarkAccepted = await client.WorkflowStartAsync(new(Guid.NewGuid().ToString("N"),
            benchmarkReady.Manifest, benchmarkReady.ManifestSha256, 8, benchmarkRun), cancellationToken);
        using var benchmarkDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        benchmarkDeadline.CancelAfter(TimeSpan.FromSeconds(20));
        OperationReference benchmarkState;
        do
        {
            benchmarkState = await client.OperationGetAsync(new(benchmarkAccepted.OperationId),
                benchmarkDeadline.Token);
            if (benchmarkState.State is "failed" or "succeeded" or "canceled") break;
            await Task.Delay(50, benchmarkDeadline.Token);
        } while (true);

        var benchmarkReport = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(
            Path.Combine(benchmarkRun, "run.json"), cancellationToken), WorkflowJson.Default.WorkflowReport)!;
        if (benchmarkState.State != "succeeded" || benchmarkReport.Nodes.Count != 3
                                                || benchmarkReport.Nodes.Count(n =>
                                                    n.Kind == "run" && n.Status == "succeeded") != 2)
            throw new Exception("Prepared Benchmark did not execute through the durable workflow runner.");
        // The independently distributed .NET SDK uses its own source-generated
        // DTOs and must agree with the Host wire contract (including NativeAOT).
        await using (var sdk = await CMakeWorkflowClient.ConnectAsync(
                         new ClientOptions(endpoint, workspace), cancellationToken))
        {
            var sdkReady = await sdk.Targets.PrepareAsync(new PrepareTargetOptions(
                Guid.NewGuid().ToString("N"), Path.Combine(workspace, "sdk-definitions"),
                "build", build, "worker"), cancellationToken);
            if (sdkReady.ManifestSha256.Length != 64
                || (await sdk.Workflows.PlanAsync(sdkReady.Manifest, cancellationToken))
                .GetProperty("valid").ValueKind != JsonValueKind.True)
                throw new Exception("Typed .NET SDK target preparation/plan mismatch.");
            var sdkBenchmark = await sdk.Benchmarks.PrepareAsync(new PrepareBenchmarkOptions(
                Guid.NewGuid().ToString("N"), Path.Combine(workspace, "sdk-benchmark.json"),
                new CMakeWorkflow.BenchmarkRecipeV1("benchmark", 1, build,
                    "worker", "Debug", 2, 5, ["one argument", ""])), cancellationToken);
            if (sdkBenchmark.ManifestSha256.Length != 64
                || (await sdk.Workflows.PlanAsync(sdkBenchmark.Manifest, cancellationToken))
                .GetProperty("valid").ValueKind != JsonValueKind.True)
                throw new Exception("Typed .NET SDK Benchmark preparation/plan mismatch.");
            var sdkPlatform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
            var sdkProfileRecipe = await sdk.Recipes.PrepareProfileAsync(new PrepareProfileRecipeOptions(
                    Guid.NewGuid().ToString("N"), Path.Combine(workspace, "sdk-profile-recipe.json"),
                    new CMakeWorkflow.Profiling.ProfileRecipeV1("profile", 1,
                        [new("main", build)], "worker", "Debug", 1, 5, [], "auto", "cpu", sdkPlatform)),
                cancellationToken);
            var sdkTestMatrix = await sdk.Recipes.PrepareTestMatrixAsync(new PrepareTestMatrixOptions(
                Guid.NewGuid().ToString("N"), Path.Combine(workspace, "sdk-test-matrix.json"),
                new CMakeWorkflow.TestMatrixRecipeV1("test-matrix", 1, build, "worker", "Debug",
                    [new("unit", TestRegex: "worker")], "parallel", 5)), cancellationToken);
            var sdkVariants = new[] { new CMakeWorkflow.BuildVariantV1("main", build, "Debug") };
            var sdkBuildMatrix = await sdk.Recipes.PrepareBuildMatrixAsync(new PrepareBuildMatrixOptions(
                Guid.NewGuid().ToString("N"), Path.Combine(workspace, "sdk-build-matrix.json"),
                new CMakeWorkflow.BuildMatrixRecipeV1("build-matrix", 1, sdkVariants,
                    ["worker"])), cancellationToken);
            var sdkBenchmarkMatrix = await sdk.Recipes.PrepareBenchmarkMatrixAsync(
                new PrepareBenchmarkMatrixOptions(Guid.NewGuid().ToString("N"),
                    Path.Combine(workspace, "sdk-benchmark-matrix.json"),
                    new CMakeWorkflow.BenchmarkMatrixRecipeV1("benchmark-matrix", 1,
                        sdkVariants, "worker", 2, 5, [], [new("case", "--case", ["x"])],
                        "round-robin")), cancellationToken);
            foreach (var preparedRecipe in new[]
                         { sdkProfileRecipe, sdkTestMatrix, sdkBuildMatrix, sdkBenchmarkMatrix })
                if (preparedRecipe.SchemaVersion != 1 || preparedRecipe.ManifestSha256.Length != 64
                                                      || (await sdk.Workflows.PlanAsync(preparedRecipe.Manifest,
                                                          cancellationToken))
                                                      .GetProperty("valid").ValueKind != JsonValueKind.True)
                    throw new Exception("Typed .NET SDK recipe preparation/plan mismatch.");
            var seed = await sdk.Campaigns.FuzzSeedAsync(new FuzzSeedCampaignOptions("exceptions",
                Path.Combine(workspace, "sdk-seeds")), cancellationToken);
            if (seed.Status != "succeeded") throw new Exception("Typed .NET SDK campaign call failed.");
        }

        Console.WriteLine("Typed .NET SDK checks passed: target preparation, plan and fuzz seed.");
        Console.WriteLine("Typed target Host RPC checks passed: prepare, plan, start, run and terminal state.");
        Console.WriteLine(
            "Profile Host RPC checks passed: typed prepare, plan, durable dispatch and executor routing.");
    }
}