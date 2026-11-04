using System.Diagnostics;
using CMakeWorkflow.Client;
using CMakeWorkflow.Host;
using Nerdbank.Streams;

internal static class BridgeTests
{
    public static async Task RunAsync()
    {
        await RunUnitAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        await using var host = new TransportTestHost();
        await host.ReadyAsync(ct);
        using var child = Start(host.Endpoint);
        var errors = child.StandardError.ReadToEndAsync(ct);
        try
        {
            using (var rpc = HostConnection.Create(child.StandardOutput.BaseStream, child.StandardInput.BaseStream))
            {
                var proxy = rpc.Attach<IHostRpc>();
                rpc.StartListening();
                for (var i = 0; i < 3; i++)
                {
                    var info = await proxy.InitializeAsync(new(1, host.Root), ct);
                    if (info.ProcessId != Environment.ProcessId) throw new Exception("Wrong persistent Host.");
                }
            }

            await child.WaitForExitAsync(ct);
            if (child.ExitCode != 0) throw new Exception("Bridge failed: " + await errors);
            await using var reconnected = await CMakeWorkflowClient.ConnectAsync(new(host.Endpoint, host.Root), ct);
            if (reconnected.Host.ProcessId != Environment.ProcessId) throw new Exception("Bridge stopped Host.");
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(CancellationToken.None);
        }

        Console.WriteLine("Bridge: bidirectional bytes, EOF, cancellation, endpoint validation, sequential RPCs and Host survival passed.");
    }

    public static async Task RunUnitAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        await CopyAsync(false, false, ct);
        await CopyAsync(false, true, ct);
        await CopyAsync(true, false, ct);
        try
        {
            await StdioBridge.RunAsync("invalid endpoint", Stream.Null, Stream.Null, ct);
            throw new Exception("Malformed endpoint accepted.");
        }
        catch (ArgumentException) { }
        try
        {
            await HostCommand.ExecuteAsync(["bridge", "--endpoint", "unused"], ct);
            throw new Exception("Missing --stdio accepted.");
        }
        catch (ArgumentException) { }
        Console.WriteLine("Bridge unit checks passed: binary transfer, client EOF, Host EOF, cancellation, invalid endpoint/options.");
    }

    private static async Task CopyAsync(bool cancel, bool hostEof, CancellationToken ct)
    {
        var (client, bridgeInput) = FullDuplexStream.CreatePair();
        var (bridgeHost, host) = FullDuplexStream.CreatePair();
        using (client)
        using (bridgeInput)
        using (bridgeHost)
        using (host)
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var forwarding = StdioBridge.ForwardAsync(bridgeInput, bridgeInput, bridgeHost, stop.Token);
            var payload = Enumerable.Range(0, 65537).Select(i => (byte)i).ToArray();
            var receive = new byte[payload.Length];
            var read = host.ReadExactlyAsync(receive, ct).AsTask();
            await client.WriteAsync(payload, ct);
            await read;
            if (!payload.SequenceEqual(receive)) throw new Exception("Upstream bytes changed.");
            read = client.ReadExactlyAsync(receive, ct).AsTask();
            await host.WriteAsync(payload, ct);
            await read;
            if (!payload.SequenceEqual(receive)) throw new Exception("Downstream bytes changed.");
            if (cancel) await stop.CancelAsync();
            else if (hostEof) host.Dispose();
            else client.Dispose();
            try
            {
                await forwarding.WaitAsync(ct);
                if (cancel) throw new Exception("Cancellation was not propagated.");
            }
            catch (OperationCanceledException) when (cancel && !ct.IsCancellationRequested) { }
        }
    }

    private static Process Start(string endpoint)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { typeof(BridgeTests).Assembly.Location, "--bridge-worker", "--stdio", "--endpoint", endpoint })
            start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("Cannot start bridge worker.");
    }
}
