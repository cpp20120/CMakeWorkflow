using CMakeWorkflow.Client;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;

if (args.Length == 4 && args[0] == "--verify-package")
    return PackageAudit.Run(args[1], args[2], args[3]);

var inFlight = args.Length == 3 && args[2] == "--inflight";
if (args.Length != 2 && !inFlight)
{
    Console.Error.WriteLine("usage: CMakeWorkflow.Client.AotSmoke ENDPOINT WORKSPACE [--inflight]");
    return 2;
}
try
{
    var workspace = Path.GetFullPath(args[1]);
    if (!Directory.Exists(workspace)) throw new DirectoryNotFoundException("Smoke workspace must already exist: " + workspace);
    if (JsonSerializer.IsReflectionEnabledByDefault)
        throw new InvalidOperationException("Smoke must run with reflection-based JSON serialization disabled.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var ct = timeout.Token;
    int hostPid;
    await using (var client = await CMakeWorkflowClient.ConnectAsync(new(args[0], workspace), ct))
    {
        hostPid = client.Host.ProcessId;
        if (client.Host.ProtocolVersion != 1 || hostPid <= 0)
            throw new InvalidOperationException("Invalid initialize response.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(client.Host.WorkspaceDirectory, workspace, comparison))
            throw new InvalidOperationException("Host bound a different workspace.");
        foreach (var capability in new[] { "workflow/plan", "workflow/start", "operations/get", "operations/find", "operations/cancel", "operations/events", "workflow/recoveryInspect" })
            if (!client.Host.Capabilities.Contains(capability, StringComparer.Ordinal))
                throw new InvalidOperationException("Host lacks " + capability);
        Console.WriteLine($"PASS NativeAOT initialize: Host {hostPid}; workspace={workspace}");
        if (inFlight) await InFlightCancellationSmoke.RunAsync(client, workspace, ct);

        // Never execute a repository workflow. All fixtures belong to this smoke.
        var fixture = Path.Combine(workspace, "sdk-aot-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var manifest = Path.Combine(fixture, "workflow.json");
        var bytes = Encoding.UTF8.GetBytes("""
            {"schemaVersion":2,"contexts":{"probe":{"buildDirectory":"unconfigured","mode":"observe"}},
             "nodes":[{"id":"inspect","kind":"inspect","context":"probe"}]}
            """);
        await File.WriteAllBytesAsync(manifest, bytes, ct);
        var plan = await client.Workflows.PlanAsync(manifest, ct);
        if (!plan.GetProperty("valid").GetBoolean() || plan.GetProperty("nodes").GetArrayLength() != 1)
            throw new InvalidOperationException("Read-only smoke plan is invalid.");
        var requestId = "aot-smoke-" + Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var options = new StartWorkflowOptions(requestId, manifest, hash, 1, Path.Combine(fixture, "results"));
        var operation = await client.Workflows.StartAsync(options, ct);
        var repeated = await client.Workflows.StartAsync(options, ct);
        if (repeated.OperationId != operation.OperationId) throw new InvalidOperationException("Repeated request created another operation.");
        var terminal = await operation.WaitForCompletionAsync(ct: ct);
        if (terminal.State != "succeeded") throw new InvalidOperationException("Inspect operation finished as " + terminal.State);
        var found = await client.Operations.FindAsync(requestId, ct);
        if (found.OperationId != operation.OperationId || found.ClientRequestId != requestId
            || found.ManifestSha256 != hash || found.State != "succeeded")
            throw new InvalidOperationException("Typed OperationSnapshot did not round-trip.");
        Console.WriteLine($"PASS NativeAOT typed start/get/find: operation={found.OperationId}; state={found.State}");
        var page = await operation.ReadEventsAsync(0, 100, ct);
        if (page.OperationId != found.OperationId || page.Events.Count == 0 || page.NextSequence <= 0)
            throw new InvalidOperationException("Typed EventPage is empty or inconsistent.");
        var next = await operation.ReadEventsAsync(page.NextSequence, 100, ct);
        if (next.Events.Count != 0) throw new InvalidOperationException("Event cursor repeated events.");
        var recovery = await operation.InspectRecoveryAsync(ct);
        if (recovery.OperationId != found.OperationId || recovery.TerminalState != "succeeded")
            throw new InvalidOperationException("Typed RecoverySnapshot did not round-trip.");
        Console.WriteLine($"PASS NativeAOT typed events/recovery: events={page.Events.Count}; revision={recovery.Revision}");

        try
        {
            await client.Operations.GetAsync("missing-" + Guid.NewGuid().ToString("N"), ct);
            throw new InvalidOperationException("Missing operation was accepted.");
        }
        catch (RemoteInvocationException error)
        {
            if (error.ErrorCode != 1001 || error.DeserializedErrorData is not RpcErrorData
                { Code: "not_found", Message.Length: > 0 })
                throw new InvalidOperationException("ApplicationError did not deserialize into RpcErrorData.", error);
        }
        // An application error must not tear down the connection.
        if ((await operation.GetSnapshotAsync(ct)).State != "succeeded")
            throw new InvalidOperationException("Connection did not survive ApplicationError.");
        Console.WriteLine("PASS NativeAOT ApplicationError: 1001/not_found, typed data; connection remains usable.");

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try
            {
                await operation.GetSnapshotAsync(canceled.Token);
                throw new InvalidOperationException("Pre-canceled request completed successfully.");
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        }
        var lateCancel = await operation.RequestCancellationAsync(recovery.Revision, ct);
        if (lateCancel.OperationId != operation.OperationId || lateCancel.Requested
            || lateCancel.State != "succeeded" || lateCancel.Revision != recovery.Revision)
            throw new InvalidOperationException("Late cancellation changed a terminal outcome.");
        Console.WriteLine("PASS NativeAOT request cancellation and terminal cancel no-op.");
        await CancellationSmoke.RunAsync(client, fixture, ct);
    }
    // ConnectAsync must not stop a foreign Host when the consumer disconnects.
    await using var reconnected = await CMakeWorkflowClient.ConnectAsync(new(args[0], workspace), ct);
    if (reconnected.Host.ProcessId != hostPid) throw new InvalidOperationException("Host ownership changed on disconnect.");
    Console.WriteLine("PASS NativeAOT SDK smoke: source-generated RPC, reconnect; JSON reflection disabled.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("NativeAOT SDK smoke failed: " + error);
    return 1;
}
