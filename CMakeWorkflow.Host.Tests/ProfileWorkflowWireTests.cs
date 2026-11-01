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
        var start = new ProcessStartInfo("cmake") { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
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
        if (current.State != "failed") throw new Exception("Profile capture fixture did not reach expected terminal failure.");
        var report = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(
            Path.Combine(runDirectory, "run.json"), cancellationToken), WorkflowJson.Default.WorkflowReport)
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
            var seed = await sdk.Campaigns.FuzzSeedAsync(new FuzzSeedCampaignOptions("exceptions",
                Path.Combine(workspace, "sdk-seeds")), cancellationToken);
            if (seed.Status != "succeeded") throw new Exception("Typed .NET SDK campaign call failed.");
        }
        Console.WriteLine("Typed .NET SDK checks passed: target preparation, plan and fuzz seed.");
        Console.WriteLine("Typed target Host RPC checks passed: prepare, plan, start, run and terminal state.");
        Console.WriteLine("Profile Host RPC checks passed: typed prepare, plan, durable dispatch and executor routing.");
    }
}
