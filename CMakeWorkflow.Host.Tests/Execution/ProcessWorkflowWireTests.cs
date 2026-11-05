using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Host;
using CMakeWorkflow.Client;

internal static class ProcessWorkflowWireTests
{
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
    }

    internal static async Task RunAsync(string endpoint, string parent, CancellationToken ct)
    {
        var workspace = Path.Combine(parent, "process-domain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var marker = Path.Combine(workspace, "effect.txt");
        var output = Path.Combine(workspace, "results");
        var testAssembly = typeof(WorkflowProcessTestWorker).Assembly.Location;
        var dotnet = Environment.ProcessPath ?? throw new Exception("Missing .NET test host executable.");
        var doc = new
        {
            schemaVersion = 1,
            domain = "process",
            nodes = new[]
            {
                new
                {
                    id = "effect", executable = dotnet,
                    arguments = new[] { testAssembly, "--workflow-process-worker", "append-delay", marker },
                    dependencies = Array.Empty<string>(), resources = new[] { "test-file" },
                    timeoutSeconds = 15
                }
            }
        };
        var path = Path.Combine(workspace, "process-plan.json");
        var manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc));
        await File.WriteAllBytesAsync(path, manifest, ct);
        var request = new ExecutionStartRequest("process", "process-wire-1", path,
            Convert.ToHexString(SHA256.HashData(manifest)), 2, output);

        string operationId;
        using (var wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (var rpc = HostConnection.Create(wire))
        {
            var client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            await client.InitializeAsync(new(1, workspace), ct);
            var operation = await client.ExecutionStartAsync(request, ct);
            Check(operation.Kind == "process", "Wrong process operation kind.");
            operationId = operation.OperationId;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            while (!File.Exists(marker) ||
                   !(await ReadMarkerAsync(marker, deadline.Token)).Contains("started"))
                await Task.Delay(20, deadline.Token);
        }

        using (var wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (var rpc = HostConnection.Create(wire))
        {
            var client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            await client.InitializeAsync(new(1, workspace), ct);
            var retried = await client.ExecutionStartAsync(request, ct);
            Check(retried.OperationId == operationId, "Retry created a different operation.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            OperationReference current;
            do
            {
                current = await client.OperationGetAsync(new(operationId), deadline.Token);
                Check(current.Kind == "process", "Operation kind was lost across RPC sessions.");
                if (current.State == "succeeded") break;
                Check(current.State is "accepted_not_dispatched" or "dispatch_authorized",
                    "Process domain did not complete: " + current.State);
                await Task.Delay(40, deadline.Token);
            } while (true);

            var found = await client.OperationFindAsync(new(request.ClientRequestId), ct);
            var terminal = await client.ExecutionStartAsync(request, ct);
            Check(found.OperationId == operationId && terminal.OperationId == operationId &&
                  terminal.State == "succeeded", "Lost-reply recovery/terminal retry failed.");
            var report = await client.ExecutionReportAsync(new(operationId), ct);
            Check(report.GetProperty("status").GetString() == "succeeded" &&
                  report.GetProperty("nodes")[0].GetProperty("status").GetString() == "succeeded",
                "Process report did not contain verified domain result.");
            var page = await client.OperationEventsAsync(new(operationId, 0, 100), ct);
            Check(page.Events.Count > 0, "Process event replay was empty.");
            var artifacts = await client.OperationArtifactListAsync(new(operationId), ct);
            var outputLog = artifacts.Artifacts.Single(a => a.ArtifactId == "stdout:effect");
            var chunk = await client.OperationArtifactReadAsync(new(operationId,
                outputLog.ArtifactId, 0, 1024, outputLog.Size, outputLog.LastWriteUtcTicks), ct);
            Check(chunk.TotalSize == outputLog.Size && chunk.ArtifactId == outputLog.ArtifactId,
                "Verified process artifact chunk unavailable.");
        }

        // Use the public SDK rather than only the raw Host RPC proxy.
        await using (var sdk = await CMakeWorkflowClient.ConnectAsync(new(endpoint, workspace), ct))
        {
            var handle = await sdk.Executions.StartAsync(new ExecutionStartOptions("process",
                request.ClientRequestId, request.Manifest, request.ManifestSha256,
                request.Jobs, request.ResultsDirectory), ct);
            Check(handle.OperationId == operationId, "SDK retry returned a new operation identity.");
            Check((await handle.GetSnapshotAsync(ct)).Kind == "process", "SDK lost domain kind.");
            Check((await sdk.Executions.GetReportAsync(operationId, ct)).GetProperty("status")
                  .GetString() == "succeeded", "SDK could not retrieve verified process report.");
            Check((await handle.ListArtifactsAsync(ct)).Artifacts.Any(x => x.ArtifactId == "report"),
                "SDK artifact listing is not domain-neutral.");
        }

        Check((await ReadMarkerAsync(marker, ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r')).SequenceEqual(["started", "finished"]),
            "Non-CMake process was dispatched more than once or incompletely.");
        using (var restarted = new DurableAcceptanceStore(workspace, readOnly: true))
        {
            Check(restarted.GetDomainKind(operationId) == "process" &&
                  restarted.GetFinalization(operationId) is { State: "succeeded", ExitCode: 0 },
                "Process domain identity/finalization was not durable.");
        }
        Console.WriteLine("Process domain wire checks passed: durable RPC, reconnect, single effect, events, report and artifacts.");
    }

    private static async Task<string> ReadMarkerAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            try { return await File.ReadAllTextAsync(path, ct); }
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 32)
            { await Task.Delay(20, ct); }
        }
    }
}
