using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CMakeWorkflow.Host.Transport;
using CMakeWorkflow.Host.Workflows;
using StreamJsonRpc;

namespace CMakeWorkflow.Host.Tests.Durability;

internal static class DurableCancellationWireTests
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
        // Cancellation during an active external process, initiated by another RPC session.
        Fixture fixture = await Fixture.CreateAsync(parent, "cancel", 12, ct);
        string operationId;
        using (Stream wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (JsonRpc rpc = HostConnection.Create(wire))
        {
            Protocol.IHostRpc client = rpc.Attach<Protocol.IHostRpc>();
            rpc.StartListening();
            _ = await client.InitializeAsync(new(1, fixture.Workspace), ct);
            operationId = (await client.WorkflowStartAsync(fixture.Request, ct)).OperationId;
            await WaitForAsync(() => fixture.Lines().Contains("started"), TimeSpan.FromSeconds(15), ct);
            Check(!fixture.Lines().Contains("finished"), "Cancellation fixture completed before cancel.");
            // The initiating client disconnects while the process is running.
        }

        using (Stream wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (JsonRpc rpc = HostConnection.Create(wire))
        {
            Protocol.IHostRpc client = rpc.Attach<Protocol.IHostRpc>();
            rpc.StartListening();
            _ = await client.InitializeAsync(new(1, fixture.Workspace), ct);
            OperationCancelResult decision = await client.OperationCancelAsync(new(operationId, 0), ct);
            Check(decision.Requested && decision.Revision == 1 && decision.State == "cancel_requested",
                "Cancellation intent was not accepted with revision 1.");
            OperationCancelResult retry = await client.OperationCancelAsync(new(operationId, 1), ct);
            // The process may finalize between the two RPC calls. A repeated
            // cancellation request remains idempotent both while pending and after
            // the durable terminal outcome is committed; terminal cancellation
            // returns Requested=false because it does not create a new intent.
            Check(retry.Revision == 1 &&
                  (retry is { State: "cancel_requested", Requested: true } or
                  { State: "canceled", Requested: false }),
                "Cancellation intent was not idempotent across finalization.");
            OperationReference found = await client.OperationFindAsync(new(fixture.Request.ClientRequestId), ct);
            Check(found.OperationId == operationId && found.State is "cancel_requested" or "canceled",
                "Cancellation was not visible through operations/find.");
            // Disconnect the cancelling client too. Neither RPC connection owns execution.
        }

        using (Stream wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (JsonRpc rpc = HostConnection.Create(wire))
        {
            Protocol.IHostRpc client = rpc.Attach<Protocol.IHostRpc>();
            rpc.StartListening();
            _ = await client.InitializeAsync(new(1, fixture.Workspace), ct);
            await WaitForAsync(async () =>
                    (await client.OperationGetAsync(new(operationId), ct)).State == "canceled",
                TimeSpan.FromSeconds(20), ct);
            OperationReference terminal = await client.OperationGetAsync(new(operationId), ct);
            Check(terminal.State == "canceled", "External process did not reach terminal canceled.");
            OperationCancelResult after = await client.OperationCancelAsync(new(operationId, 1), ct);
            Check(after.State == "canceled" && !after.Requested && after.Revision == 1,
                "Cancel after terminal outcome must preserve finalization.");
            OperationReference replay = await client.WorkflowStartAsync(fixture.Request, ct);
            Check(replay.OperationId == operationId && replay.State == "canceled",
                "Retry after cancellation created an execution or hid terminal state.");
        }

        string[] before = fixture.Lines();
        Check(before.SequenceEqual(["started"]),
            "Cancellation did not terminate the external effect before its finished marker.");
        using (DurableAcceptanceStore store = new(fixture.Workspace, readOnly: true))
        {
            Check(store.Get(operationId)?.State == "canceled" &&
                  store.GetFinalization(operationId) is { State: "canceled", ExitCode: 130 },
                "Canceled report and durable finalization diverged.");
        }

        await Task.Delay(250, ct);
        Check(fixture.Lines().SequenceEqual(before), "Process continued producing effects after terminal cancel.");

        // Race between cancel and normal finalization: exactly one consistent terminal outcome.
        Fixture race = await Fixture.CreateAsync(parent, "race", 1, ct);
        using (Stream wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (JsonRpc rpc = HostConnection.Create(wire))
        {
            Protocol.IHostRpc client = rpc.Attach<Protocol.IHostRpc>();
            rpc.StartListening();
            _ = await client.InitializeAsync(new(1, race.Workspace), ct);
            OperationReference accepted = await client.WorkflowStartAsync(race.Request, ct);
            await WaitForAsync(() => race.Lines().Contains("started"), TimeSpan.FromSeconds(15), ct);
            // A short delay puts cancellation close to the external effect's completion.
            await Task.Delay(700, ct);
            OperationCancelResult decision = await client.OperationCancelAsync(new(accepted.OperationId, 0), ct);
            Check(decision.State is "cancel_requested" or "succeeded" or "canceled",
                "Cancellation/finalization race produced an invalid response.");
            await WaitForAsync(async () =>
            {
                string state = (await client.OperationGetAsync(new(accepted.OperationId), ct)).State;
                return state is "succeeded" or "canceled";
            }, TimeSpan.FromSeconds(20), ct);
            OperationReference terminal = await client.OperationGetAsync(new(accepted.OperationId), ct);
            using DurableAcceptanceStore store = new(race.Workspace, readOnly: true);
            DurableAcceptanceStore.Finalization? finalization = store.GetFinalization(accepted.OperationId);
            Check(finalization is not null && finalization.State == terminal.State &&
                  (terminal.State == "succeeded" ? finalization.ExitCode == 0 : finalization.ExitCode == 130),
                "Cancellation/finalization race has inconsistent terminal report.");
            OperationCancelResult again = await client.OperationCancelAsync(new(accepted.OperationId, decision.Revision), ct);
            Check(!again.Requested && again.State == terminal.State,
                "Cancellation changed a terminal result.");
            Check(race.Lines().Count(line => line == "started") == 1,
                "Cancellation race dispatched an external command more than once.");
        }

        // Cancellation after an already succeeded workflow is a no-op, even at revision zero.
        Fixture success = await Fixture.CreateAsync(parent, "completed", 0, ct);
        using (Stream wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (JsonRpc rpc = HostConnection.Create(wire))
        {
            Protocol.IHostRpc client = rpc.Attach<Protocol.IHostRpc>();
            rpc.StartListening();
            _ = await client.InitializeAsync(new(1, success.Workspace), ct);
            OperationReference accepted = await client.WorkflowStartAsync(success.Request, ct);
            await WaitForAsync(async () =>
                    (await client.OperationGetAsync(new(accepted.OperationId), ct)).State == "succeeded",
                TimeSpan.FromSeconds(15), ct);
            OperationCancelResult noOp = await client.OperationCancelAsync(new(accepted.OperationId, 0), ct);
            Check(noOp.State == "succeeded" && noOp.Revision == 0 && !noOp.Requested,
                "Cancellation overwrote succeeded outcome.");
            Check(success.Lines().SequenceEqual(["started", "finished"]),
                "Completed workflow effects changed after cancel.");
        }

        Console.WriteLine(
            "Operations/cancel JSON-RPC suite passed: live process termination, reconnect, CAS retry, durable canceled, finalization race and terminal no-op.");
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        while (!predicate())
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(30, deadline.Token);
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> predicate, TimeSpan timeout, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        while (!await predicate())
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(30, deadline.Token);
        }
    }

    private sealed record Fixture(string Workspace, string Marker, WorkflowStartRequest Request)
    {
        public string[] Lines()
        {
            // CMake file(APPEND) opens the marker for writing. On Windows the
            // writer may briefly deny readers even though the file exists.
            // Retrying only sharing/lock violations preserves all assertions
            // about post-cancellation effects and exactly-once dispatch.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using FileStream stream = new(Marker, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using StreamReader reader = new(stream);
                    return [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'))];
                }
                catch (FileNotFoundException)
                {
                    return [];
                }
                catch (IOException error) when (OperatingSystem.IsWindows()
                                                && ((error.HResult & 0xffff) is 32 or 33) && attempt < 25)
                {
                    Thread.Sleep(20);
                }
            }
        }

        public static async Task<Fixture> CreateAsync(string parent, string name, int delaySeconds,
            CancellationToken ct)
        {
            string workspace = Path.Combine(parent, "cancel wire " + name + " " + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(workspace, "source");
            string build = Path.Combine(workspace, "build");
            _ = Directory.CreateDirectory(source);
            _ = Directory.CreateDirectory(build);
            string marker = Path.Combine(source, "effects.txt");
            await File.WriteAllTextAsync(Path.Combine(source, "effect.cmake"),
                "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"started\\n\")\n" +
                $"execute_process(COMMAND \"${{CMAKE_COMMAND}}\" -E sleep {delaySeconds})\n" +
                "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"finished\\n\")\n", CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"),
                "cmake_minimum_required(VERSION 3.26)\nproject(cancel_wire NONE)\n" +
                "add_custom_target(effect COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake\")\n", CancellationToken.None);
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

            using (Process process = Process.Start(start) ?? throw new UnreachableException("Cannot configure cancellation fixture."))
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
                Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0)
                {
                    throw new UnreachableException("Cancellation fixture configuration failed: " + await stdout + await stderr);
                }

                _ = await Task.WhenAll(stdout, stderr);
            }

            JsonNode document = JsonNode.Parse("""
                                               {"schemaVersion":1,"contexts":{"w":{"buildDirectory":"placeholder","mode":"adopt"}},
                                                "nodes":[{"id":"effect","kind":"invoke","context":"w","target":"effect"}]}
                                               """)!;
            document["contexts"]!["w"]!["buildDirectory"] = build;
            string manifest = Path.Combine(workspace, "workflow.json");
            byte[] bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
            await File.WriteAllBytesAsync(manifest, bytes, ct);
            WorkflowStartRequest request = new("cancel-" + name, manifest,
                Convert.ToHexString(SHA256.HashData(bytes)), 1, Path.Combine(workspace, "execution"));
            return new(workspace, marker, request);
        }
    }
}