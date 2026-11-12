using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CMakeWorkflow.Host.Workflows;

namespace CMakeWorkflow.Host.Tests.Durability;

internal static class DurableCrashTests
{
    // Called in a separate OS process. This barrier is injected into the executor,
    // not implemented using timing or polling of an internal state transition.
    public static async Task WorkerAsync(string[] args)
    {
        if (args.Length != 9)
        {
            throw new ArgumentException("Invalid fault worker arguments.");
        }

        string workspace = args[1];
        string operationId = args[2];
        string requestId = args[3];
        string manifest = args[4];
        string hash = args[5];
        string output = args[6];
        string barrier = args[7];
        string signal = args[8];
        _ = await DurableWorkflowExecutor.ExecuteAcceptedAsync(workspace, operationId,
            requestId, manifest, hash, 1, output, CancellationToken.None,
            async (stage, ct) =>
            {
                if (stage != barrier)
                {
                    return;
                }

                // Publish a complete signal atomically, never expose a partially written file.
                string pending = signal + ".pending";
                await File.WriteAllTextAsync(pending, stage, ct);
                File.Move(pending, signal);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });
        throw new UnreachableException("Fault worker unexpectedly completed.");
    }

    public static async Task RunAsync(string parent)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("Durable SIGKILL checks skipped (Linux only).");
            return;
        }

        foreach (string? stage in new[] { "authorized", "checkpoint", "effect" })
        {
            string workspace = Path.Combine(parent, "durable-sigkill-" + stage + "-" + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(workspace, "src");
            string build = Path.Combine(workspace, "build");
            string output = Path.Combine(workspace, "results");
            _ = Directory.CreateDirectory(source);
            _ = Directory.CreateDirectory(build);
            string marker = Path.Combine(source, "effects.txt");
            await File.WriteAllTextAsync(Path.Combine(source, "effect.cmake"),
                "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"effect\\n\")\n" +
                (stage == "effect" ? "execute_process(COMMAND \"${CMAKE_COMMAND}\" -E sleep 20)\n" : ""));
            await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"),
                "cmake_minimum_required(VERSION 3.26)\nproject(crash NONE)\n" +
                "add_custom_target(effect COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake\")\n");
            string query = Path.Combine(build, ".cmake", "api", "v1", "query");
            _ = Directory.CreateDirectory(query);
            await File.WriteAllTextAsync(Path.Combine(query, "codemodel-v2"), "");
            await ProcessOk("cmake", ["-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug"]);
            string manifest = Path.Combine(workspace, "workflow.json");
            JsonNode doc = JsonNode.Parse("""
                                          {"schemaVersion":1,"contexts":{"w":{"buildDirectory":"placeholder","mode":"adopt"}},
                                           "nodes":[{"id":"effect","kind":"invoke","context":"w","target":"effect"}]}
                                          """)!;
            doc["contexts"]!["w"]!["buildDirectory"] = build;
            byte[] bytes = Encoding.UTF8.GetBytes(doc.ToJsonString());
            await File.WriteAllBytesAsync(manifest, bytes);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            string requestId = "fault-" + stage;
            string operationId;
            using (DurableAcceptanceStore store = new(workspace))
            {
                operationId = store.Accept(requestId, manifest, hash, 1, output).OperationId;
            }

            string signal = stage == "effect" ? marker : Path.Combine(workspace, "fault-signal");
            string testAssembly = typeof(DurableCrashTests).Assembly.Location;
            ProcessStartInfo psi = new("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string? arg in new[]
                     {
                         testAssembly, "--durable-fault-worker", workspace, operationId,
                         requestId, manifest, hash, output, stage, signal
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using Process child = Process.Start(psi) ?? throw new UnreachableException("Cannot start fault worker.");
            try
            {
                await WaitForSignalAsync(child, signal);

                if (stage != "effect" && await File.ReadAllTextAsync(signal) != stage)
                {
                    throw new UnreachableException("Wrong fault boundary reached.");
                }

                child.Kill(entireProcessTree: true); // SIGKILL on Linux, no graceful disposal
                await child.WaitForExitAsync();
                using (DurableAcceptanceStore reopened = new(workspace, readOnly: true))
                {
                    string? state = reopened.Get(operationId)?.State;
                    if (state != "dispatch_authorized")
                    {
                        throw new UnreachableException($"Crash lost authorization: {state}");
                    }

                    (string RunDirectory, string CheckpointSha256)? checkpoint =
                        reopened.GetDispatchCheckpoint(operationId);
                    if ((checkpoint is not null) != (stage != "authorized"))
                    {
                        throw new UnreachableException("Checkpoint visibility violates crash boundary.");
                    }

                    if (reopened.Find(requestId)?.OperationId != operationId)
                    {
                        throw new UnreachableException("Crash lost deduplication identity.");
                    }
                }

                if (File.Exists(marker) != (stage == "effect"))
                {
                    throw new UnreachableException("Unexpected external effect at fault boundary.");
                }

                try
                {
                    _ = await DurableWorkflowExecutor.ExecuteAcceptedAsync(workspace, operationId, requestId,
                        manifest, hash, 1, output, CancellationToken.None);
                    throw new UnreachableException("Restart caused duplicate dispatch.");
                }
                catch (InvalidOperationException e) when (e.Message.StartsWith("operation_already_authorized",
                                                              StringComparison.Ordinal))
                {
                }

                if (stage == "effect"
                        ? (await File.ReadAllLinesAsync(marker, CancellationToken.None)).Length != 1
                        : File.Exists(marker))
                {
                    throw new UnreachableException("Retry executed external effect.");
                }
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
            }
        }

        Console.WriteLine(
            "Durable SIGKILL checks passed: authorization/checkpoint/external-effect crash, persistence, deduplication and no implicit redispatch.");
    }

    private static async Task WaitForSignalAsync(Process child, string signal)
    {
        CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        try
        {
            while (!File.Exists(signal))
            {
                if (child.HasExited)
                {
                    throw new UnreachableException(
                        $"Fault worker exited {child.ExitCode}: {await child.StandardError.ReadToEndAsync()}");
                }

                await Task.Delay(25, deadline.Token);
            }
        }
        finally
        {
            deadline.Dispose();
        }
    }

    private static async Task ProcessOk(string executable, string[] args)
    {
        ProcessStartInfo psi = new(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process child = Process.Start(psi) ?? throw new UnreachableException("Unable to configure fixture.");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            throw;
        }

        if (child.ExitCode != 0)
        {
            throw new UnreachableException($"Configure failed: {await stdout} {await stderr}");
        }

        _ = await Task.WhenAll(stdout, stderr);
    }
}