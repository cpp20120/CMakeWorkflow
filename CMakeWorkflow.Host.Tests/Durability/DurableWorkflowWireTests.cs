using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CMakeWorkflow.Host;
using StreamJsonRpc;

internal static class DurableWorkflowWireTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new UnreachableException(message);
        }
    }

    public static async Task RunAsync(string endpoint, string parent, CancellationToken ct)
    {
        string workspace = Path.Combine(parent, "wire durable " + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(workspace, "source");
        string build = Path.Combine(workspace, "build");
        string output = Path.Combine(workspace, "execution");
        _ = Directory.CreateDirectory(source);
        _ = Directory.CreateDirectory(build);
        string marker = Path.Combine(source, "effects.txt");
        await File.WriteAllTextAsync(Path.Combine(source, "effect.cmake"),
            "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"started\\n\")\n" +
            "execute_process(COMMAND \"${CMAKE_COMMAND}\" -E sleep 2)\n" +
            "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"finished\\n\")\n", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"),
            "cmake_minimum_required(VERSION 3.26)\nproject(durable_wire NONE)\n" +
            "add_custom_target(effect COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake\")\n", CancellationToken.None);
        string query = Path.Combine(build, ".cmake", "api", "v1", "query");
        _ = Directory.CreateDirectory(query);
        await File.WriteAllTextAsync(Path.Combine(query, "codemodel-v2"), "", CancellationToken.None);
        await ConfigureAsync(source, build, ct);

        JsonNode document = JsonNode.Parse("""
                                      {"schemaVersion":1,"contexts":{"w":{"buildDirectory":"placeholder","mode":"adopt"}},
                                       "nodes":[{"id":"effect","kind":"invoke","context":"w","target":"effect"}]}
                                      """)!;
        document["contexts"]!["w"]!["buildDirectory"] = build;
        string manifest = Path.Combine(workspace, "workflow.json");
        byte[] bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        await File.WriteAllBytesAsync(manifest, bytes, ct);
        WorkflowStartRequest request = new("wire-client-id", manifest,
            Convert.ToHexString(SHA256.HashData(bytes)), 1, output);

        string operationId;
        // Connection A goes away while the external command is executing.
        using (Stream wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (JsonRpc rpc = HostConnection.Create(wire))
        {
            IHostRpc client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            _ = await client.InitializeAsync(new(1, workspace), ct);
            OperationReference accepted = await client.WorkflowStartAsync(request, ct);
            operationId = accepted.OperationId;
            // The domain-qualified entry point must share the original durable
            // CMake identity and must not redispatch the same request.
            OperationReference composed = await client.ExecutionStartAsync(new ExecutionStartRequest(
                "cmake", request.ClientRequestId, request.Manifest, request.ManifestSha256,
                request.Jobs, request.ResultsDirectory), ct);
            Check(composed.OperationId == operationId && composed.Kind == "workflow",
                "Neutral Host composition changed CMake operation identity.");
            Check(accepted.ClientRequestId == request.ClientRequestId, "First RPC lost request identity.");
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker) || !(await ReadMarkerAsync(marker, wait.Token)).Contains("started"))
            {
                await Task.Delay(25, wait.Token);
            }

            Check(!(await ReadMarkerAsync(marker, wait.Token)).Contains("finished"),
                "Workflow completed before initiator disconnected; test is not exercising detached execution.");
        }

        // Connection B has a separate RPC session, bound to the same workspace.
        using (Stream wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (JsonRpc rpc = HostConnection.Create(wire))
        {
            IHostRpc client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            _ = await client.InitializeAsync(new(1, workspace), ct);
            OperationReference retry = await client.WorkflowStartAsync(request, ct);
            Check(retry.OperationId == operationId, "Reconnected retry created a second operation.");
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            OperationReference current;
            do
            {
                current = await client.OperationGetAsync(new(operationId), wait.Token);
                Check(current.OperationId == operationId, "Operation identity changed after disconnect.");
                if (current.State == "succeeded")
                {
                    break;
                }

                Check(current.State is "accepted_not_dispatched" or "dispatch_authorized",
                    "Unexpected nonterminal state: " + current.State);
                await Task.Delay(50, wait.Token);
            } while (true);

            OperationOverview overview = await client.OperationOverviewAsync(new(operationId), ct);
            Check(overview.IsFinalized && overview.Operation.Kind == "workflow" &&
                  overview.Nodes.Count == 1 && overview.Nodes[0].Id == "effect" &&
                  overview.Nodes[0].Status == "succeeded" && overview.ReportSha256 is { Length: 64 },
                "CMake domain-neutral terminal overview missing verified node state.");
            OperationReference found = await client.OperationFindAsync(new(request.ClientRequestId), ct);
            OperationReference terminalRetry = await client.WorkflowStartAsync(request, ct);
            Check(found.State == "succeeded" && terminalRetry.State == "succeeded"
                                             && terminalRetry.OperationId == operationId,
                "Terminal RPC retry/lookup inconsistency.");

            string[] lines = await ReadMarkerAsync(marker, wait.Token);
            Check(lines.SequenceEqual(["started", "finished"]),
                "Detached execution duplicated or lost the external effect.");
        }

        Check(File.Exists(Path.Combine(output, "run.json")), "Terminal runner report missing.");
        using (DurableAcceptanceStore store = new(workspace, readOnly: true))
        {
            Check(store.Get(operationId)?.State == "succeeded" &&
                  store.GetFinalization(operationId) is { State: "succeeded", ExitCode: 0 },
                "RPC terminal state was not durable.");
        }

        Console.WriteLine(
            "Workflow/start JSON-RPC lifecycle checks passed: disconnect, reconnect, terminal lookup and single dispatch.");
    }

    private static async Task<string[]> ReadMarkerAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await File.ReadAllLinesAsync(path, ct);
            }
            // CMake file(APPEND) may briefly deny concurrent readers on Windows.
            // Only ERROR_SHARING_VIOLATION is transient; all other failures propagate.
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 32)
            {
                await Task.Delay(25, ct);
            }
        }
    }

    private static async Task ConfigureAsync(string source, string build, CancellationToken ct)
    {
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

        using Process process = Process.Start(start) ?? throw new UnreachableException("Cannot start CMake configure.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            throw new UnreachableException("Wire fixture CMake configure failed: " + await stdout + await stderr);
        }

        _ = await Task.WhenAll(stdout, stderr);
    }
}
