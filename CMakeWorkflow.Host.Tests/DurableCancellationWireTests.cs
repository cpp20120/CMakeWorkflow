using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CMakeWorkflow.Host;

internal static class DurableCancellationWireTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task RunAsync(string endpoint, string parent, CancellationToken ct)
    {
        // Cancellation during an active external process, initiated by another RPC session.
        var fixture = await Fixture.CreateAsync(parent, "cancel", 12, ct);
        string operationId;
        using (var wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (var rpc = HostConnection.Create(wire))
        {
            var client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            await client.InitializeAsync(new(1, fixture.Workspace), ct);
            operationId = (await client.WorkflowStartAsync(fixture.Request, ct)).OperationId;
            await WaitForAsync(() => fixture.Lines().Contains("started"), TimeSpan.FromSeconds(15), ct);
            Check(!fixture.Lines().Contains("finished"), "Cancellation fixture completed before cancel.");
            // The initiating client disconnects while the process is running.
        }

        using (var wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (var rpc = HostConnection.Create(wire))
        {
            var client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            await client.InitializeAsync(new(1, fixture.Workspace), ct);
            var decision = await client.OperationCancelAsync(new(operationId, 0), ct);
            Check(decision.Requested && decision.Revision == 1 && decision.State == "cancel_requested",
                "Cancellation intent was not accepted with revision 1.");
            var retry = await client.OperationCancelAsync(new(operationId, 1), ct);
            Check(retry.Revision == 1 && retry.Requested, "Cancellation intent was not idempotent.");
            var found = await client.OperationFindAsync(new(fixture.Request.ClientRequestId), ct);
            Check(found.OperationId == operationId && found.State is "cancel_requested" or "canceled",
                "Cancellation was not visible through operations/find.");
            // Disconnect the cancelling client too. Neither RPC connection owns execution.
        }

        using (var wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (var rpc = HostConnection.Create(wire))
        {
            var client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            await client.InitializeAsync(new(1, fixture.Workspace), ct);
            await WaitForAsync(async () =>
                (await client.OperationGetAsync(new(operationId), ct)).State == "canceled",
                TimeSpan.FromSeconds(20), ct);
            var terminal = await client.OperationGetAsync(new(operationId), ct);
            Check(terminal.State == "canceled", "External process did not reach terminal canceled.");
            var after = await client.OperationCancelAsync(new(operationId, 1), ct);
            Check(after.State == "canceled" && !after.Requested && after.Revision == 1,
                "Cancel after terminal outcome must preserve finalization.");
            var replay = await client.WorkflowStartAsync(fixture.Request, ct);
            Check(replay.OperationId == operationId && replay.State == "canceled",
                "Retry after cancellation created an execution or hid terminal state.");
        }
        var before = fixture.Lines();
        Check(before.SequenceEqual(["started"]),
            "Cancellation did not terminate the external effect before its finished marker.");
        using (var store = new DurableAcceptanceStore(fixture.Workspace, readOnly: true))
        {
            Check(store.Get(operationId)?.State == "canceled" &&
                store.GetFinalization(operationId) is { State: "canceled", ExitCode: 130 },
                "Canceled report and durable finalization diverged.");
        }
        await Task.Delay(250, ct);
        Check(fixture.Lines().SequenceEqual(before), "Process continued producing effects after terminal cancel.");

        // Race between cancel and normal finalization: exactly one consistent terminal outcome.
        var race = await Fixture.CreateAsync(parent, "race", 1, ct);
        using (var wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (var rpc = HostConnection.Create(wire))
        {
            var client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            await client.InitializeAsync(new(1, race.Workspace), ct);
            var accepted = await client.WorkflowStartAsync(race.Request, ct);
            await WaitForAsync(() => race.Lines().Contains("started"), TimeSpan.FromSeconds(15), ct);
            // A short delay puts cancellation close to the external effect's completion.
            await Task.Delay(700, ct);
            var decision = await client.OperationCancelAsync(new(accepted.OperationId, 0), ct);
            Check(decision.State is "cancel_requested" or "succeeded" or "canceled",
                "Cancellation/finalization race produced an invalid response.");
            await WaitForAsync(async () =>
            {
                var state = (await client.OperationGetAsync(new(accepted.OperationId), ct)).State;
                return state is "succeeded" or "canceled";
            }, TimeSpan.FromSeconds(20), ct);
            var terminal = await client.OperationGetAsync(new(accepted.OperationId), ct);
            using var store = new DurableAcceptanceStore(race.Workspace, readOnly: true);
            var finalization = store.GetFinalization(accepted.OperationId);
            Check(finalization is not null && finalization.State == terminal.State &&
                (terminal.State == "succeeded" ? finalization.ExitCode == 0 : finalization.ExitCode == 130),
                "Cancellation/finalization race has inconsistent terminal report.");
            var again = await client.OperationCancelAsync(new(accepted.OperationId, decision.Revision), ct);
            Check(!again.Requested && again.State == terminal.State,
                "Cancellation changed a terminal result.");
            Check(race.Lines().Count(line => line == "started") == 1,
                "Cancellation race dispatched an external command more than once.");
        }

        // Cancellation after an already succeeded workflow is a no-op, even at revision zero.
        var success = await Fixture.CreateAsync(parent, "completed", 0, ct);
        using (var wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (var rpc = HostConnection.Create(wire))
        {
            var client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            await client.InitializeAsync(new(1, success.Workspace), ct);
            var accepted = await client.WorkflowStartAsync(success.Request, ct);
            await WaitForAsync(async () =>
                (await client.OperationGetAsync(new(accepted.OperationId), ct)).State == "succeeded",
                TimeSpan.FromSeconds(15), ct);
            var noOp = await client.OperationCancelAsync(new(accepted.OperationId, 0), ct);
            Check(noOp.State == "succeeded" && noOp.Revision == 0 && !noOp.Requested,
                "Cancellation overwrote succeeded outcome.");
            Check(success.Lines().SequenceEqual(["started", "finished"]),
                "Completed workflow effects changed after cancel.");
        }
        Console.WriteLine("Operations/cancel JSON-RPC suite passed: live process termination, reconnect, CAS retry, durable canceled, finalization race and terminal no-op.");
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        while (!predicate())
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(30, deadline.Token);
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> predicate, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        while (!await predicate())
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(30, deadline.Token);
        }
    }

    private sealed record Fixture(string Workspace, string Marker, WorkflowStartRequest Request)
    {
        public string[] Lines() => File.Exists(Marker) ? File.ReadAllLines(Marker) : [];

        public static async Task<Fixture> CreateAsync(string parent, string name, int delaySeconds, CancellationToken ct)
        {
            var workspace = Path.Combine(parent, "cancel wire " + name + " " + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(workspace, "source");
            var build = Path.Combine(workspace, "build");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(build);
            var marker = Path.Combine(source, "effects.txt");
            File.WriteAllText(Path.Combine(source, "effect.cmake"),
                "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"started\\n\")\n" +
                $"execute_process(COMMAND \"${{CMAKE_COMMAND}}\" -E sleep {delaySeconds})\n" +
                "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"finished\\n\")\n");
            File.WriteAllText(Path.Combine(source, "CMakeLists.txt"),
                "cmake_minimum_required(VERSION 3.26)\nproject(cancel_wire NONE)\n" +
                "add_custom_target(effect COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake\")\n");
            var query = Path.Combine(build, ".cmake", "api", "v1", "query");
            Directory.CreateDirectory(query);
            File.WriteAllText(Path.Combine(query, "codemodel-v2"), "");
            var start = new ProcessStartInfo("cmake")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[] { "-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug" })
                start.ArgumentList.Add(arg);
            using (var process = Process.Start(start) ?? throw new Exception("Cannot configure cancellation fixture."))
            {
                var stdout = process.StandardOutput.ReadToEndAsync(ct);
                var stderr = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0)
                    throw new Exception("Cancellation fixture configuration failed: " + await stdout + await stderr);
                await Task.WhenAll(stdout, stderr);
            }
            var document = JsonNode.Parse("""
                {"schemaVersion":1,"contexts":{"w":{"buildDirectory":"placeholder","mode":"adopt"}},
                 "nodes":[{"id":"effect","kind":"invoke","context":"w","target":"effect"}]}
                """)!;
            document["contexts"]!["w"]!["buildDirectory"] = build;
            var manifest = Path.Combine(workspace, "workflow.json");
            var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
            await File.WriteAllBytesAsync(manifest, bytes, ct);
            var request = new WorkflowStartRequest("cancel-" + name, manifest,
                Convert.ToHexString(SHA256.HashData(bytes)), 1, Path.Combine(workspace, "execution"));
            return new(workspace, marker, request);
        }
    }
}
