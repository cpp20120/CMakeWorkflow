using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CMakeWorkflow.Host;

internal static class DurableWorkflowWireTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task RunAsync(string endpoint, string parent, CancellationToken ct)
    {
        var workspace = Path.Combine(parent, "wire durable " + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(workspace, "source");
        var build = Path.Combine(workspace, "build");
        var output = Path.Combine(workspace, "execution");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(build);
        var marker = Path.Combine(source, "effects.txt");
        File.WriteAllText(Path.Combine(source, "effect.cmake"),
            "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"started\\n\")\n" +
            "execute_process(COMMAND \"${CMAKE_COMMAND}\" -E sleep 2)\n" +
            "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"finished\\n\")\n");
        File.WriteAllText(Path.Combine(source, "CMakeLists.txt"),
            "cmake_minimum_required(VERSION 3.26)\nproject(durable_wire NONE)\n" +
            "add_custom_target(effect COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake\")\n");
        var query = Path.Combine(build, ".cmake", "api", "v1", "query");
        Directory.CreateDirectory(query);
        File.WriteAllText(Path.Combine(query, "codemodel-v2"), "");
        await ConfigureAsync(source, build, ct);

        var document = JsonNode.Parse("""
            {"schemaVersion":1,"contexts":{"w":{"buildDirectory":"placeholder","mode":"adopt"}},
             "nodes":[{"id":"effect","kind":"invoke","context":"w","target":"effect"}]}
            """)!;
        document["contexts"]!["w"]!["buildDirectory"] = build;
        var manifest = Path.Combine(workspace, "workflow.json");
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        await File.WriteAllBytesAsync(manifest, bytes, ct);
        var request = new WorkflowStartRequest("wire-client-id", manifest,
            Convert.ToHexString(SHA256.HashData(bytes)), 1, output);

        string operationId;
        // Connection A goes away while the external command is executing.
        using (var wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (var rpc = HostConnection.Create(wire))
        {
            var client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            await client.InitializeAsync(new(1, workspace), ct);
            var accepted = await client.WorkflowStartAsync(request, ct);
            operationId = accepted.OperationId;
            Check(accepted.ClientRequestId == request.ClientRequestId, "First RPC lost request identity.");
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker) || !File.ReadAllText(marker).Contains("started", StringComparison.Ordinal))
            {
                await Task.Delay(25, wait.Token);
            }
            Check(!File.ReadAllText(marker).Contains("finished", StringComparison.Ordinal),
                "Workflow completed before initiator disconnected; test is not exercising detached execution.");
        }

        // Connection B has a separate RPC session, bound to the same workspace.
        using (var wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (var rpc = HostConnection.Create(wire))
        {
            var client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            await client.InitializeAsync(new(1, workspace), ct);
            var retry = await client.WorkflowStartAsync(request, ct);
            Check(retry.OperationId == operationId, "Reconnected retry created a second operation.");
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            OperationReference current;
            do
            {
                current = await client.OperationGetAsync(new(operationId), wait.Token);
                Check(current.OperationId == operationId, "Operation identity changed after disconnect.");
                if (current.State == "succeeded") break;
                Check(current.State is "accepted_not_dispatched" or "dispatch_authorized",
                    "Unexpected nonterminal state: " + current.State);
                await Task.Delay(50, wait.Token);
            } while (true);
            var found = await client.OperationFindAsync(new(request.ClientRequestId), ct);
            var terminalRetry = await client.WorkflowStartAsync(request, ct);
            Check(found.State == "succeeded" && terminalRetry.State == "succeeded"
                && terminalRetry.OperationId == operationId, "Terminal RPC retry/lookup inconsistency.");
        }

        var lines = File.ReadAllLines(marker);
        Check(lines.SequenceEqual(["started", "finished"]), "Detached execution duplicated or lost the external effect.");
        Check(File.Exists(Path.Combine(output, "run.json")), "Terminal runner report missing.");
        using (var store = new DurableAcceptanceStore(workspace, readOnly: true))
        {
            Check(store.Get(operationId)?.State == "succeeded" &&
                store.GetFinalization(operationId) is { State: "succeeded", ExitCode: 0 },
                "RPC terminal state was not durable.");
        }
        Console.WriteLine("Workflow/start JSON-RPC lifecycle checks passed: disconnect, reconnect, terminal lookup and single dispatch.");
    }

    private static async Task ConfigureAsync(string source, string build, CancellationToken ct)
    {
        var start = new ProcessStartInfo("cmake")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new Exception("Cannot start CMake configure.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new Exception("Wire fixture CMake configure failed: " + await stdout + await stderr);
        await Task.WhenAll(stdout, stderr);
    }
}
