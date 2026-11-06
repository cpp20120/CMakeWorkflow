using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CMakeWorkflow.Host.Workflows;

// Exercises the production bridge and a real CMake child process. Deliberately
// does not inject a crash: the existing CLI recovery suite covers OS process
// death, while host-owned crash windows still need a separate subprocess suite.
namespace CMakeWorkflow.Host.Tests.Durability;

internal static class DurableExecutionProcessTests
{
    public static async Task RunAsync(string parent)
    {
        string workspace = Path.Combine(parent, "durable-process-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(workspace, "source");
        string build = Path.Combine(workspace, "build");
        string output = Path.Combine(workspace, "run");
        _ = Directory.CreateDirectory(source);
        _ = Directory.CreateDirectory(build);
        // An actual CMake custom target writes exactly one line per invocation.
        // Use a CMake script rather than shell syntax so Windows and Linux agree.
        string marker = Path.Combine(source, "effects.txt");
        await File.WriteAllTextAsync(Path.Combine(source, "effect.cmake"),
            "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"effect\\n\")\n", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"),
            "cmake_minimum_required(VERSION 3.26)\nproject(durable_effect NONE)\n" +
            "add_custom_target(effect COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake\")\n", CancellationToken.None);
        // Request codemodel before configure; preflight intentionally requires
        // observed configuration identity (not just a successful CMakeCache).
        string query = Path.Combine(build, ".cmake", "api", "v1", "query");
        _ = Directory.CreateDirectory(query);
        await File.WriteAllTextAsync(Path.Combine(query, "codemodel-v2"), "", CancellationToken.None);
        await ProcessOk("cmake", ["-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug"]);
        string manifest = Path.Combine(workspace, "workflow.json");
        // Absolute build directory avoids platform-specific JSON escaping.
        JsonNode document = JsonNode.Parse("""
                                           {"schemaVersion":1,"contexts":{"w":{"buildDirectory":"placeholder","mode":"adopt"}},
                                            "nodes":[{"id":"effect","kind":"invoke","context":"w","target":"effect"}]}
                                           """)!;
        document["contexts"]!["w"]!["buildDirectory"] = build;
        string json = document.ToJsonString();
        byte[] original = Encoding.UTF8.GetBytes(json);
        await File.WriteAllBytesAsync(manifest, original);
        string digest = Convert.ToHexString(SHA256.HashData(original));
        string id;
        using (DurableAcceptanceStore store = new(workspace))
        {
            id = store.Accept("one", manifest, digest, 1, output).OperationId;
        }
        // Source JSON can change after acceptance: execution uses the snapshot.
        await File.WriteAllTextAsync(manifest, "{invalid-current-manifest}");
        using (DurableExecutionCoordinator coordinator = new(CancellationToken.None))
        {
            WorkflowStartRequest start = new("one", manifest, digest, 1, output);
            OperationReference first = coordinator.Start(workspace, start);
            OperationReference retry = coordinator.Start(workspace, start);
            if (first.OperationId != id || retry.OperationId != id)
            {
                throw new UnreachableException("Host-owned start lost durable request identity.");
            }

            await coordinator.DrainAsync();
            OperationReference after = coordinator.Start(workspace, start);
            if (after.OperationId != id || after.State != "succeeded")
            {
                throw new UnreachableException("Host-owned retry did not return existing admitted operation.");
            }
        }

        if (!File.Exists(marker) || (await File.ReadAllLinesAsync(marker, CancellationToken.None)).Length != 1)
        {
            throw new UnreachableException("Durable executor did not run CMake target exactly once.");
        }

        if (!File.Exists(Path.Combine(output, "run.json")))
        {
            throw new UnreachableException("Runner did not persist its report.");
        }

        using (DurableAcceptanceStore reopened = new(workspace, readOnly: true))
        {
            if (reopened.Get(id)?.State != "succeeded" ||
                reopened.GetDispatchCheckpoint(id) is null ||
                reopened.GetFinalization(id) is not { State: "succeeded", ExitCode: 0 })
            {
                throw new UnreachableException("Durable checkpoint binding missing after real dispatch.");
            }
        }

        using (DurableAcceptanceStore completion = new(workspace))
        {
            DurableAcceptanceStore.Finalization repeated = completion.FinalizeExecution(id, output, 0);
            if (repeated.State != "succeeded" || completion.Find("one")?.State != "succeeded" ||
                repeated.ReportSha256.Length != 64)
            {
                throw new UnreachableException("Finalization not idempotent after restart.");
            }
        }

        try
        {
            _ = await DurableWorkflowExecutor.ExecuteAcceptedAsync(workspace, id, "one", manifest,
                digest, 1, output, CancellationToken.None);
            throw new UnreachableException("Duplicate external dispatch succeeded.");
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("operation_already_authorized",
                                                      StringComparison.Ordinal))
        {
        }

        if ((await File.ReadAllLinesAsync(marker, CancellationToken.None)).Length != 1)
        {
            throw new UnreachableException("Repeat changed external state.");
        }
        // Two distinct accepted requests may target the same directory, but only
        // the first one may reserve it. The reservation survives process restart.
        await File.WriteAllBytesAsync(manifest, original);
        string other;
        using (DurableAcceptanceStore store = new(workspace))
        {
            other = store.Accept("two", manifest, digest, 1, output).OperationId;
            try
            {
                store.ReserveResultsDirectory(other, output);
                throw new UnreachableException("Second operation stole the results directory.");
            }
            catch (InvalidOperationException e) when (e.Message == "results_directory_reserved")
            {
            }
        }

        using (DurableAcceptanceStore reopened = new(workspace))
        {
            reopened.ReserveResultsDirectory(id, output); // idempotent
            try
            {
                reopened.ReserveResultsDirectory(other, output);
                throw new UnreachableException("Reservation disappeared after restart.");
            }
            catch (InvalidOperationException e) when (e.Message == "results_directory_reserved")
            {
            }
        }

        Console.WriteLine("Durable finalization checks passed: terminal report, restart, idempotence and lookup.");
        Console.WriteLine("Durable result ownership checks passed: collision, idempotence and restart.");
        Console.WriteLine(
            "Durable executor real-process checks passed: snapshot, CMake invocation, checkpoint and no duplicate dispatch.");
    }

    private static async Task ProcessOk(string executable, IEnumerable<string> args)
    {
        ProcessStartInfo start = new(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("CMake could not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new UnreachableException($"CMake configure failed: {await stdout} {await stderr}");
        }

        _ = await Task.WhenAll(stdout, stderr);
    }
}