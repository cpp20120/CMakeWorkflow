using System.Diagnostics;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Transport;
using Nerdbank.Streams;
using StreamJsonRpc;
using CMakeWorkflowClient = CMakeWorkflow.Client.CMakeWorkflowClient;

namespace CMakeWorkflow.Host.Tests.Transport;

internal static class BridgeTests
{
    public static async Task RunAsync()
    {
        await RunUnitAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
        CancellationToken ct = deadline.Token;
        await using TransportTestHost host = new();
        await host.ReadyAsync(ct);
        using Process child = Start(host.Endpoint);
        Task<string> errors = child.StandardError.ReadToEndAsync(ct);
        try
        {
            using (JsonRpc rpc = HostConnection.Create(child.StandardOutput.BaseStream, child.StandardInput.BaseStream))
            {
                IHostRpc proxy = rpc.Attach<IHostRpc>();
                rpc.StartListening();
                for (int i = 0; i < 3; i++)
                {
                    HostInfo info = await proxy.InitializeAsync(new(1, host.Root), ct);
                    if (info.ProcessId != Environment.ProcessId)
                    {
                        throw new UnreachableException("Wrong persistent Host.");
                    }
                }
            }

            await child.WaitForExitAsync(ct);
            if (child.ExitCode != 0)
            {
                throw new UnreachableException("Bridge failed: " + await errors);
            }

            await using CMakeWorkflowClient reconnected = await CMakeWorkflowClient.ConnectAsync(new(host.Endpoint, host.Root), ct);
            if (reconnected.Host.ProcessId != Environment.ProcessId)
            {
                throw new UnreachableException("Bridge stopped Host.");
            }
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            await child.WaitForExitAsync(CancellationToken.None);
        }

        Console.WriteLine("Bridge: bidirectional bytes, EOF, cancellation, endpoint validation, sequential RPCs and Host survival passed.");
    }

    public static async Task RunUnitAsync()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        CancellationToken ct = deadline.Token;
        await CopyAsync(false, false, ct);
        await CopyAsync(false, true, ct);
        await CopyAsync(true, false, ct);
        try
        {
            await StdioBridge.RunAsync("invalid endpoint", Stream.Null, Stream.Null, ct);
            throw new UnreachableException("Malformed endpoint accepted.");
        }
        catch (ArgumentException) { }
        try
        {
            _ = await HostCommand.ExecuteAsync(["bridge", "--endpoint", "unused"], ct);
            throw new UnreachableException("Missing --stdio accepted.");
        }
        catch (ArgumentException) { }
        Console.WriteLine("Bridge unit checks passed: binary transfer, client EOF, Host EOF, cancellation, invalid endpoint/options.");
    }

    private static async Task CopyAsync(bool cancel, bool hostEof, CancellationToken ct)
    {
        (Stream? client, Stream? bridgeInput) = FullDuplexStream.CreatePair();
        (Stream? bridgeHost, Stream? host) = FullDuplexStream.CreatePair();
        using (client)
        using (bridgeInput)
        using (bridgeHost)
        using (host)
        using (CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {

            async Task ForwardUntilStoppedAsync() =>
                await StdioBridge.ForwardAsync(bridgeInput, bridgeInput, bridgeHost, stop.Token);
            Task forwarding = ForwardUntilStoppedAsync();
            try
            {
                byte[] payload = [.. Enumerable.Range(0, 65537).Select(i => (byte)i)];
                byte[] receive = new byte[payload.Length];
                Task read = host.ReadExactlyAsync(receive, ct).AsTask();
                await client.WriteAsync(payload, ct);
                await read;
                if (!payload.SequenceEqual(receive))
                {
                    throw new UnreachableException("Upstream bytes changed.");
                }

                read = client.ReadExactlyAsync(receive, ct).AsTask();
                await host.WriteAsync(payload, ct);
                await read;
                if (!payload.SequenceEqual(receive))
                {
                    throw new UnreachableException("Downstream bytes changed.");
                }

                if (cancel)
                {
                    await stop.CancelAsync();
                }
                else if (hostEof)
                {
                    await host.DisposeAsync();
                }
                else
                {
                    await client.DisposeAsync();
                }

                try
                {
                    await forwarding.WaitAsync(ct);
                    if (cancel)
                    {
                        throw new UnreachableException("Cancellation was not propagated.");
                    }
                }
                catch (OperationCanceledException) when (cancel && !ct.IsCancellationRequested) { }
            }
            finally
            {
                await stop.CancelAsync();
                try
                {
                    await forwarding.WaitAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
                    await forwarding; // Join before disposing any captured stream.
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
    }

    private static Process Start(string endpoint)
    {
        ProcessStartInfo start = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string? arg in new[] { typeof(BridgeTests).Assembly.Location, "--bridge-worker", "--stdio", "--endpoint", endpoint })
        {
            start.ArgumentList.Add(arg);
        }

        return Process.Start(start) ?? throw new IOException("Cannot start bridge worker.");
    }
}