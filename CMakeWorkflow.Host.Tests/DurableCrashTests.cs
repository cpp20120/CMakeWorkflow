using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CMakeWorkflow.Host;

internal static class DurableCrashTests
{
    // Called in a separate OS process. This barrier is injected into the executor,
    // not implemented using timing or polling of an internal state transition.
    public static async Task WorkerAsync(string[] args)
    {
        if (args.Length != 9) throw new ArgumentException("Invalid fault worker arguments.");
        var workspace = args[1];
        var operationId = args[2];
        var requestId = args[3];
        var manifest = args[4];
        var hash = args[5];
        var output = args[6];
        var barrier = args[7];
        var signal = args[8];
        await DurableWorkflowExecutor.ExecuteAcceptedAsync(workspace, operationId,
            requestId, manifest, hash, 1, output, CancellationToken.None,
            async (stage, ct) =>
            {
                if (stage != barrier) return;
                await File.WriteAllTextAsync(signal, stage, ct);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });
        throw new Exception("Fault worker unexpectedly completed.");
    }

    public static async Task RunAsync(string parent)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("Durable SIGKILL checks skipped (Linux only).");
            return;
        }
        foreach (var stage in new[] { "authorized", "checkpoint", "effect" })
        {
            var workspace = Path.Combine(parent, "durable-sigkill-" + stage + "-" + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(workspace, "src");
            var build = Path.Combine(workspace, "build");
            var output = Path.Combine(workspace, "results");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(build);
            var marker = Path.Combine(source, "effects.txt");
            await File.WriteAllTextAsync(Path.Combine(source, "effect.cmake"),
                "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"effect\\n\")\n" +
                (stage == "effect" ? "execute_process(COMMAND \"${CMAKE_COMMAND}\" -E sleep 20)\n" : ""));
            await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"),
                "cmake_minimum_required(VERSION 3.26)\nproject(crash NONE)\n" +
                "add_custom_target(effect COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake\")\n");
            var query = Path.Combine(build, ".cmake", "api", "v1", "query");
            Directory.CreateDirectory(query);
            await File.WriteAllTextAsync(Path.Combine(query, "codemodel-v2"), "");
            await ProcessOk("cmake", ["-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug"]);
            var manifest = Path.Combine(workspace, "workflow.json");
            var doc = JsonNode.Parse("""
                {"schemaVersion":1,"contexts":{"w":{"buildDirectory":"placeholder","mode":"adopt"}},
                 "nodes":[{"id":"effect","kind":"invoke","context":"w","target":"effect"}]}
                """)!;
            doc["contexts"]!["w"]!["buildDirectory"] = build;
            var bytes = Encoding.UTF8.GetBytes(doc.ToJsonString());
            await File.WriteAllBytesAsync(manifest, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var requestId = "fault-" + stage;
            string operationId;
            using (var store = new DurableAcceptanceStore(workspace))
                operationId = store.Accept(requestId, manifest, hash, 1, output).OperationId;
            var signal = stage == "effect" ? marker : Path.Combine(workspace, "fault-signal");
            var testAssembly = typeof(DurableCrashTests).Assembly.Location;
            var psi = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[] { testAssembly, "--durable-fault-worker", workspace, operationId,
                requestId, manifest, hash, output, stage, signal }) psi.ArgumentList.Add(arg);
            using var child = Process.Start(psi) ?? throw new Exception("Cannot start fault worker.");
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!File.Exists(signal))
                {
                    if (child.HasExited)
                        throw new Exception($"Fault worker exited {child.ExitCode}: {await child.StandardError.ReadToEndAsync()}");
                    await Task.Delay(25, deadline.Token);
                }
                if (stage != "effect" && await File.ReadAllTextAsync(signal) != stage)
                    throw new Exception("Wrong fault boundary reached.");
                child.Kill(entireProcessTree: true); // SIGKILL on Linux, no graceful disposal
                await child.WaitForExitAsync();
                using (var reopened = new DurableAcceptanceStore(workspace, readOnly: true))
                {
                    var state = reopened.Get(operationId)?.State;
                    if (state != "dispatch_authorized") throw new Exception($"Crash lost authorization: {state}");
                    var checkpoint = reopened.GetDispatchCheckpoint(operationId);
                    if ((checkpoint is not null) != (stage != "authorized"))
                        throw new Exception("Checkpoint visibility violates crash boundary.");
                    if (reopened.Find(requestId)?.OperationId != operationId)
                        throw new Exception("Crash lost deduplication identity.");
                }
                if (File.Exists(marker) != (stage == "effect")) throw new Exception("Unexpected external effect at fault boundary.");
                try
                {
                    await DurableWorkflowExecutor.ExecuteAcceptedAsync(workspace, operationId, requestId,
                        manifest, hash, 1, output, CancellationToken.None);
                    throw new Exception("Restart caused duplicate dispatch.");
                }
                catch (InvalidOperationException e) when (e.Message.StartsWith("operation_already_authorized", StringComparison.Ordinal)) { }
                if (stage == "effect" ? File.ReadAllLines(marker).Length != 1 : File.Exists(marker))
                    throw new Exception("Retry executed external effect.");
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            }
        }
        Console.WriteLine("Durable SIGKILL checks passed: authorization/checkpoint/external-effect crash, persistence, deduplication and no implicit redispatch.");
    }

    private static async Task ProcessOk(string executable, string[] args)
    {
        var psi = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var child = Process.Start(psi) ?? throw new Exception("Unable to configure fixture.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
        if (child.ExitCode != 0) throw new Exception($"Configure failed: {await stdout} {await stderr}");
        await Task.WhenAll(stdout, stderr);
    }
}
