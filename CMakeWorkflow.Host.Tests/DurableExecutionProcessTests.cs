using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CMakeWorkflow;
using CMakeWorkflow.Host;

// Exercises the production bridge and a real CMake child process. Deliberately
// does not inject a crash: the existing CLI recovery suite covers OS process
// death, while host-owned crash windows still need a separate subprocess suite.
internal static class DurableExecutionProcessTests
{
    public static async Task RunAsync(string parent)
    {
        var workspace = Path.Combine(parent, "durable-process-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(workspace, "source");
        var build = Path.Combine(workspace, "build");
        var output = Path.Combine(workspace, "run");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(build);
        // An actual CMake custom target writes exactly one line per invocation.
        // Use a CMake script rather than shell syntax so Windows and Linux agree.
        var marker = Path.Combine(source, "effects.txt");
        File.WriteAllText(Path.Combine(source, "effect.cmake"),
            "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"effect\\n\")\n");
        File.WriteAllText(Path.Combine(source, "CMakeLists.txt"),
            "cmake_minimum_required(VERSION 3.26)\nproject(durable_effect NONE)\n" +
            "add_custom_target(effect COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake\")\n");
        // Request codemodel before configure; preflight intentionally requires
        // observed configuration identity (not just a successful CMakeCache).
        var query = Path.Combine(build, ".cmake", "api", "v1", "query");
        Directory.CreateDirectory(query);
        File.WriteAllText(Path.Combine(query, "codemodel-v2"), "");
        await ProcessOk("cmake", ["-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug"]);
        var manifest = Path.Combine(workspace, "workflow.json");
        // Absolute build directory avoids platform-specific JSON escaping.
        var document = JsonNode.Parse("""
            {"schemaVersion":1,"contexts":{"w":{"buildDirectory":"placeholder","mode":"adopt"}},
             "nodes":[{"id":"effect","kind":"invoke","context":"w","target":"effect"}]}
            """)!;
        document["contexts"]!["w"]!["buildDirectory"] = build;
        var json = document.ToJsonString();
        var original = Encoding.UTF8.GetBytes(json);
        await File.WriteAllBytesAsync(manifest, original);
        var digest = Convert.ToHexString(SHA256.HashData(original));
        string id;
        using (var store = new DurableAcceptanceStore(workspace))
            id = store.Accept("one", manifest, digest, 1, output).OperationId;
        // Source JSON can change after acceptance: execution uses the snapshot.
        await File.WriteAllTextAsync(manifest, "{invalid-current-manifest}");
        using (var coordinator = new DurableExecutionCoordinator(CancellationToken.None))
        {
            var start = new WorkflowStartRequest("one", manifest, digest, 1, output);
            var first = coordinator.Start(workspace, start);
            var retry = coordinator.Start(workspace, start);
            if (first.OperationId != id || retry.OperationId != id)
                throw new Exception("Host-owned start lost durable request identity.");
            await coordinator.DrainAsync();
            var after = coordinator.Start(workspace, start);
            if (after.OperationId != id || after.State != "succeeded")
                throw new Exception("Host-owned retry did not return existing admitted operation.");
        }
        if (!File.Exists(marker) || File.ReadAllLines(marker).Length != 1)
            throw new Exception("Durable executor did not run CMake target exactly once.");
        if (!File.Exists(Path.Combine(output, "run.json")))
            throw new Exception("Runner did not persist its report.");
        using (var reopened = new DurableAcceptanceStore(workspace, readOnly: true))
        {
            if (reopened.Get(id)?.State != "succeeded" ||
                reopened.GetDispatchCheckpoint(id) is null ||
                reopened.GetFinalization(id) is not { State: "succeeded", ExitCode: 0 })
                throw new Exception("Durable checkpoint binding missing after real dispatch.");
        }
        using (var completion = new DurableAcceptanceStore(workspace))
        {
            var repeated = completion.FinalizeExecution(id, output, 0);
            if (repeated.State != "succeeded" || completion.Find("one")?.State != "succeeded" ||
                repeated.ReportSha256.Length != 64)
                throw new Exception("Finalization not idempotent after restart.");
        }
        try
        {
            await DurableWorkflowExecutor.ExecuteAcceptedAsync(workspace, id, "one", manifest,
                digest, 1, output, CancellationToken.None);
            throw new Exception("Duplicate external dispatch succeeded.");
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("operation_already_authorized", StringComparison.Ordinal)) { }
        if (File.ReadAllLines(marker).Length != 1)
            throw new Exception("Repeat changed external state.");
        // Two distinct accepted requests may target the same directory, but only
        // the first one may reserve it. The reservation survives process restart.
        await File.WriteAllBytesAsync(manifest, original);
        string other;
        using (var store = new DurableAcceptanceStore(workspace))
        {
            other = store.Accept("two", manifest, digest, 1, output).OperationId;
            try
            {
                store.ReserveResultsDirectory(other, output);
                throw new Exception("Second operation stole the results directory.");
            }
            catch (InvalidOperationException e) when (e.Message == "results_directory_reserved") { }
        }
        using (var reopened = new DurableAcceptanceStore(workspace))
        {
            reopened.ReserveResultsDirectory(id, output); // idempotent
            try
            {
                reopened.ReserveResultsDirectory(other, output);
                throw new Exception("Reservation disappeared after restart.");
            }
            catch (InvalidOperationException e) when (e.Message == "results_directory_reserved") { }
        }
        Console.WriteLine("Durable finalization checks passed: terminal report, restart, idempotence and lookup.");
        Console.WriteLine("Durable result ownership checks passed: collision, idempotence and restart.");
        Console.WriteLine("Durable executor real-process checks passed: snapshot, CMake invocation, checkpoint and no duplicate dispatch.");
    }

    private static async Task ProcessOk(string executable, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CMake could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0)
            throw new Exception($"CMake configure failed: {await stdout} {await stderr}");
        await Task.WhenAll(stdout, stderr);
    }
}
