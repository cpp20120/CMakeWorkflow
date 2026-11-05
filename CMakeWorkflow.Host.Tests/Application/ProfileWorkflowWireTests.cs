using System.Diagnostics;
using System.Text.Json;
using CMakeWorkflow;
using CMakeWorkflow.Host;
using CMakeWorkflow.Client;
using StreamJsonRpc;

internal static class ProfileWorkflowWireTests
{
    public static async Task RunAsync(string endpoint, string parent, CancellationToken cancellationToken)
    {
        string workspace = Path.Combine(parent, "profile-wire-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(workspace, "source");
        string build = Path.Combine(workspace, "build");
        _ = Directory.CreateDirectory(source);
        _ = Directory.CreateDirectory(build);
        await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"),
            "cmake_minimum_required(VERSION 3.26)\nproject(profile_wire LANGUAGES CXX)\nadd_executable(worker worker.cpp)\n", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(source, "worker.cpp"), "int main() { return 0; }\n", CancellationToken.None);
        string query = Path.Combine(build, ".cmake", "api", "v1", "query");
        _ = Directory.CreateDirectory(query);
        await File.WriteAllTextAsync(Path.Combine(query, "codemodel-v2"), "", CancellationToken.None);
        ProcessStartInfo start = new("cmake")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string? arg in new[] { "-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug" })
        {
            start.ArgumentList.Add(arg);
        }

        using (Process process = Process.Start(start) ?? throw new UnreachableException("Cannot start CMake."))
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new UnreachableException("Profile wire fixture configure failed: " + await stdout + await stderr);
            }

            _ = await Task.WhenAll(stdout, stderr);
        }

        using Stream wire = await LocalTransport.ConnectAsync(endpoint, cancellationToken);
        using JsonRpc rpc = HostConnection.Create(wire);
        IHostRpc client = rpc.Attach<IHostRpc>();
        rpc.StartListening();
        _ = await client.InitializeAsync(new(1, workspace), cancellationToken);
        // The platform-incompatible backend makes capture fail deterministically
        // after Host dispatches it. The timing executable must still complete.
        string backend = OperatingSystem.IsWindows() ? "perf" : "etw";
        ProfilePreparation prepared = await client.ProfilePrepareAsync(new(
            Guid.NewGuid().ToString("N"), Path.Combine(workspace, "definitions"),
            "worker", [new("main", build)], Repeats: 1, TimeoutSeconds: 5,
            Backend: backend), cancellationToken);
        WorkflowPlanView planned = await client.WorkflowPlanAsync(new(prepared.Manifest), cancellationToken);
        if (!planned.Valid || !planned.Nodes.Any(node => node.Kind == "run") ||
            !planned.Nodes.Any(node => node.Kind == "profile"))
        {
            throw new UnreachableException("Profile preparation did not produce a valid timing/capture workflow.");
        }

        string runDirectory = Path.Combine(workspace, "run");
        OperationReference accepted = await client.WorkflowStartAsync(new(
            "profile-wire-start", prepared.Manifest, prepared.ManifestSha256, 1, runDirectory), cancellationToken);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        OperationReference current;
        do
        {
            current = await client.OperationGetAsync(new(accepted.OperationId), deadline.Token);
            if (current.State is "failed" or "succeeded" or "canceled")
            {
                break;
            }

            await Task.Delay(50, deadline.Token);
        } while (true);

        if (current.State != "failed")
        {
            throw new UnreachableException("Profile capture fixture did not reach expected terminal failure.");
        }

        WorkflowReport report = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(
                             Path.Combine(runDirectory, "run.json"), cancellationToken),
                         WorkflowJson.Default.WorkflowReport)
                     ?? throw new UnreachableException("Missing profile workflow report.");
        NodeResult timing = report.Nodes.Single(node => node.Id == "timing_main_1");
        NodeResult capture = report.Nodes.Single(node => node.Id == "capture_main");
        if (timing.Status != "succeeded" || capture.Status != "failed" ||
            capture.Error is null || !capture.Error.Contains("Profiling backend", StringComparison.Ordinal))
        {
            throw new UnreachableException("Host did not route profile capture through ProfileWorkflowExecutor.");
        }
        // Native target UI uses typed prepare + the SAME durable workflow/start.
        TargetPreparation targetReady = await client.TargetPrepareAsync(new(Guid.NewGuid().ToString("N"),
            Path.Combine(workspace, "target-definitions"), "run", build, "worker"), cancellationToken);
        WorkflowPlanView targetPlan = await client.WorkflowPlanAsync(new(targetReady.Manifest), cancellationToken);
        if (!targetPlan.Valid || targetPlan.Nodes.Count != 2
                              || targetPlan.Nodes[0].Kind != "build" || targetPlan.Nodes[1].Kind != "run")
        {
            throw new UnreachableException("Target preparation failed to produce a build/run DAG.");
        }

        string targetOutput = Path.Combine(workspace, "target-run");
        OperationReference targetAccepted = await client.WorkflowStartAsync(new(Guid.NewGuid().ToString("N"),
            targetReady.Manifest, targetReady.ManifestSha256, 1, targetOutput), cancellationToken);
        using CancellationTokenSource targetDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        targetDeadline.CancelAfter(TimeSpan.FromSeconds(20));
        OperationReference targetState;
        do
        {
            targetState = await client.OperationGetAsync(new(targetAccepted.OperationId), targetDeadline.Token);
            if (targetState.State is "failed" or "succeeded" or "canceled")
            {
                break;
            }

            await Task.Delay(50, targetDeadline.Token);
        } while (true);

        if (targetState.State != "succeeded")
        {
            throw new UnreachableException("Typed target build/run did not complete: " + targetState.State);
        }

        BenchmarkRecipeV1 benchmarkRecipe = new("benchmark", 1, build, "worker", "Debug",
            2, 5, ["one argument", ""]);
        BenchmarkPreparation benchmarkReady = await client.BenchmarkPrepareAsync(new(Guid.NewGuid().ToString("N"),
                Path.Combine(workspace, "benchmark definitions", "benchmark.json"), benchmarkRecipe),
            cancellationToken);
        WorkflowPlanView benchmarkPlan = await client.WorkflowPlanAsync(new(benchmarkReady.Manifest), cancellationToken);
        if (!benchmarkPlan.Valid || benchmarkPlan.Nodes.Count != 3)
        {
            throw new UnreachableException("Typed Benchmark preparation did not produce the expected DAG.");
        }

        string benchmarkRun = Path.Combine(workspace, "benchmark-run");
        OperationReference benchmarkAccepted = await client.WorkflowStartAsync(new(Guid.NewGuid().ToString("N"),
            benchmarkReady.Manifest, benchmarkReady.ManifestSha256, 8, benchmarkRun), cancellationToken);
        using CancellationTokenSource benchmarkDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        benchmarkDeadline.CancelAfter(TimeSpan.FromSeconds(20));
        OperationReference benchmarkState;
        do
        {
            benchmarkState = await client.OperationGetAsync(new(benchmarkAccepted.OperationId),
                benchmarkDeadline.Token);
            if (benchmarkState.State is "failed" or "succeeded" or "canceled")
            {
                break;
            }

            await Task.Delay(50, benchmarkDeadline.Token);
        } while (true);

        WorkflowReport benchmarkReport = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(
            Path.Combine(benchmarkRun, "run.json"), cancellationToken), WorkflowJson.Default.WorkflowReport)!;
        if (benchmarkState.State != "succeeded" || benchmarkReport.Nodes.Count != 3
                                                || benchmarkReport.Nodes.Count(n =>
                                                    n.Kind == "run" && n.Status == "succeeded") != 2)
        {
            throw new UnreachableException("Prepared Benchmark did not execute through the durable workflow runner.");
        }
        // The independently distributed .NET SDK uses its own source-generated
        // DTOs and must agree with the Host wire contract (including NativeAOT).
        await using (CMakeWorkflowClient sdk = await CMakeWorkflowClient.ConnectAsync(
                         new ClientOptions(endpoint, workspace), cancellationToken))
        {
            PreparedWorkflow sdkReady = await sdk.Targets.PrepareAsync(new PrepareTargetOptions(
                Guid.NewGuid().ToString("N"), Path.Combine(workspace, "sdk-definitions"),
                "build", build, "worker"), cancellationToken);
            if (sdkReady.ManifestSha256.Length != 64
                || (await sdk.Workflows.PlanAsync(sdkReady.Manifest, cancellationToken))
                .GetProperty("valid").ValueKind != JsonValueKind.True)
            {
                throw new UnreachableException("Typed .NET SDK target preparation/plan mismatch.");
            }

            PreparedBenchmark sdkBenchmark = await sdk.Benchmarks.PrepareAsync(new PrepareBenchmarkOptions(
                Guid.NewGuid().ToString("N"), Path.Combine(workspace, "sdk-benchmark.json"),
                new BenchmarkRecipeV1("benchmark", 1, build,
                    "worker", "Debug", 2, 5, ["one argument", ""])), cancellationToken);
            if (sdkBenchmark.ManifestSha256.Length != 64
                || (await sdk.Workflows.PlanAsync(sdkBenchmark.Manifest, cancellationToken))
                .GetProperty("valid").ValueKind != JsonValueKind.True)
            {
                throw new UnreachableException("Typed .NET SDK Benchmark preparation/plan mismatch.");
            }

            string sdkPlatform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
            PreparedRecipe sdkProfileRecipe = await sdk.Recipes.PrepareProfileAsync(new PrepareProfileRecipeOptions(
                    Guid.NewGuid().ToString("N"), Path.Combine(workspace, "sdk-profile-recipe.json"),
                    new CMakeWorkflow.Profiling.ProfileRecipeV1("profile", 1,
                        [new("main", build)], "worker", "Debug", 1, 5, [], "auto", "cpu", sdkPlatform)),
                cancellationToken);
            PreparedRecipe sdkTestMatrix = await sdk.Recipes.PrepareTestMatrixAsync(new PrepareTestMatrixOptions(
                Guid.NewGuid().ToString("N"), Path.Combine(workspace, "sdk-test-matrix.json"),
                new TestMatrixRecipeV1("test-matrix", 1, build, "worker", "Debug",
                    [new("unit", TestRegex: "worker")], "parallel", 5)), cancellationToken);
            BuildVariantV1[] sdkVariants = [new BuildVariantV1("main", build, "Debug")];
            PreparedRecipe sdkBuildMatrix = await sdk.Recipes.PrepareBuildMatrixAsync(new PrepareBuildMatrixOptions(
                Guid.NewGuid().ToString("N"), Path.Combine(workspace, "sdk-build-matrix.json"),
                new BuildMatrixRecipeV1("build-matrix", 1, sdkVariants,
                    ["worker"])), cancellationToken);
            PreparedRecipe sdkBenchmarkMatrix = await sdk.Recipes.PrepareBenchmarkMatrixAsync(
                new PrepareBenchmarkMatrixOptions(Guid.NewGuid().ToString("N"),
                    Path.Combine(workspace, "sdk-benchmark-matrix.json"),
                    new BenchmarkMatrixRecipeV1("benchmark-matrix", 1,
                        sdkVariants, "worker", 2, 5, [], [new("case", "--case", ["x"])],
                        "round-robin")), cancellationToken);
            foreach (PreparedRecipe? preparedRecipe in new[]
                         { sdkProfileRecipe, sdkTestMatrix, sdkBuildMatrix, sdkBenchmarkMatrix })
            {
                if (preparedRecipe.SchemaVersion != 1 || preparedRecipe.ManifestSha256.Length != 64
                                                      || (await sdk.Workflows.PlanAsync(preparedRecipe.Manifest,
                                                          cancellationToken))
                                                      .GetProperty("valid").ValueKind != JsonValueKind.True)
                {
                    throw new UnreachableException("Typed .NET SDK recipe preparation/plan mismatch.");
                }
            }

            CampaignResult seed = await sdk.Campaigns.FuzzSeedAsync(new FuzzSeedCampaignOptions("exceptions",
                Path.Combine(workspace, "sdk-seeds")), cancellationToken);
            if (seed.Status != "succeeded")
            {
                throw new UnreachableException("Typed .NET SDK campaign call failed.");
            }
        }

        Console.WriteLine("Typed .NET SDK checks passed: target preparation, plan and fuzz seed.");
        Console.WriteLine("Typed target Host RPC checks passed: prepare, plan, start, run and terminal state.");
        Console.WriteLine(
            "Profile Host RPC checks passed: typed prepare, plan, durable dispatch and executor routing.");
    }
}