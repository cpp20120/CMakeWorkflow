using CMakeWorkflow.Client;

internal static class InFlightCancellationSmoke
{
    public static async Task RunAsync(CMakeWorkflowClient client, string workspace, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var timeout = deadline.Token;
        var root = Path.Combine(workspace, "rpc-inflight", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var canceledFile = await CreateManifestAsync(Path.Combine(root, "canceled"), timeout);
        using var cancelRequest = new CancellationTokenSource();
        var pending = client.Workflows.PlanAsync(canceledFile, cancelRequest.Token);
        try
        {
            Console.Error.WriteLine("[RPC inflight] waiting for server started marker");
            await WaitForMarkerAsync(canceledFile + ".started", "started", timeout);
            Console.Error.WriteLine("[RPC inflight] server entered; canceling invocation");
            if (pending.IsCompleted) throw new InvalidOperationException("RPC completed before in-flight cancellation.");
            cancelRequest.Cancel();
            try
            {
                await pending.WaitAsync(timeout);
                throw new InvalidOperationException("In-flight RPC did not throw OperationCanceledException.");
            }
            catch (OperationCanceledException) when (pending.IsCanceled && !timeout.IsCancellationRequested) { }

            // The client-side exception alone proves nothing about server receipt.
            // Keep this connection open until its handler confirms cancellation.
            Console.Error.WriteLine("[RPC inflight] pendingCanceled=" + pending.IsCanceled + "; waiting for server cancellation marker");
            await WaitForMarkerAsync(canceledFile + ".canceled", "request-canceled", timeout);
            await WaitForMarkerAsync(canceledFile + ".exited", "exited", timeout);
            if (File.Exists(canceledFile + ".completed"))
                throw new InvalidOperationException("Canceled request continued through the gate.");

            // Control: another request on the SAME connection stays alive until
            // explicitly released, returns a real plan, and never reports cancel.
            var completedFile = await CreateManifestAsync(Path.Combine(root, "completed"), timeout);
            var next = client.Workflows.PlanAsync(completedFile, timeout);
            await WaitForMarkerAsync(completedFile + ".started", "started", timeout);
            if (next.IsCompleted) throw new InvalidOperationException("Control RPC bypassed the server gate.");
            await File.WriteAllTextAsync(completedFile + ".release", "release", timeout);
            var plan = await next.WaitAsync(timeout);
            if (!plan.GetProperty("valid").GetBoolean() || plan.GetProperty("nodes").GetArrayLength() != 1
                || File.Exists(completedFile + ".canceled"))
                throw new InvalidOperationException("Control RPC failed after in-flight cancellation.");
            await WaitForMarkerAsync(completedFile + ".completed", "completed", timeout);
            await WaitForMarkerAsync(completedFile + ".exited", "exited", timeout);
            Console.WriteLine("PASS NativeAOT in-flight $/cancelRequest: server started, RPC token canceled, handler exited; same-connection control plan succeeded.");
        }
        finally
        {
            // Bound cleanup if readiness/assertions fail. The caller disposes
            // the connection on failure; observe its pending request as well.
            cancelRequest.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            catch (Exception error) { Console.Error.WriteLine("In-flight request cleanup: " + error.Message); }
        }
    }

    private static async Task<string> CreateManifestAsync(string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "inflight-plan.json");
        await File.WriteAllTextAsync(path, """
            {"schemaVersion":2,"contexts":{"probe":{"buildDirectory":"unconfigured","mode":"observe"}},
             "nodes":[{"id":"inspect","kind":"inspect","context":"probe"}]}
            """, ct);
        return path;
    }

    private static async Task WaitForMarkerAsync(string path, string expected, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // On Windows the writer can still hold the marker open when
                // its directory entry first becomes visible.
                try { if (File.Exists(path) && await File.ReadAllTextAsync(path, ct) == expected) return; }
                catch (IOException) { }
                await Task.Delay(10, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new TimeoutException("Missing server acknowledgement: " + Path.GetFileName(path)
                + ". In-flight mode requires a RpcCancellationTesting=true Host and CMAKEWORKFLOW_RPC_TEST_ROOT.");
        }
    }
}
