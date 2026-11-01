using System.Diagnostics;
using System.Text.Json;
using CMakeWorkflow;
using CMakeWorkflow.Host;

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
        Console.WriteLine("Profile Host RPC checks passed: typed prepare, plan, durable dispatch and executor routing.");
    }
}
