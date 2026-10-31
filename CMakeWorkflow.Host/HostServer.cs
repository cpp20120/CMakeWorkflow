using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using CMakeWorkflow.Tooling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public static class HostServer
{
    public static async Task RunAsync(string endpoint, CancellationToken ct)
    {
        using var shutdownHost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = shutdownHost.Token;
        // Max 16 active clients; request lifetimes are tied to their connection.
        var clients = new ConcurrentDictionary<long, Task>();
        using var slots = new SemaphoreSlim(16);
        using var fixes = new FixCoordinator();
        using var executions = new DurableExecutionCoordinator(ct);
        using var downloads = VerifiedCMakeInstaller.CreateDownloadClient();
        var installer = new VerifiedCMakeInstaller(downloads);
        long next = 0;
        async Task ServeAsync(Stream stream, long id)
        {
            try
            {
                using (stream)
                using (var disconnected = CancellationTokenSource.CreateLinkedTokenSource(ct))
                using (var rpc = HostConnection.Create(stream))
                {
                    var session = new DoctorSession(workspace => new ToolingService(new SystemToolingRuntime(workspace), installer),
                        value => rpc.NotifyAsync("tooling/progress", value), disconnected.Token,
                        fixes, value => rpc.NotifyAsync("tooling/fixProgress", value), durableExecutions: executions);
                    rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IHostRpc>(), session, null);
                    rpc.Disconnected += (_, _) => disconnected.Cancel();
                    using var shutdown = ct.Register(rpc.Dispose);
                    rpc.StartListening();
                    try { await rpc.Completion; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    { Console.Error.WriteLine($"[Host] Connection ended: {error}"); }
                    finally { disconnected.Cancel(); await session.DrainAsync(); }
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { Console.Error.WriteLine($"[Host] Session failed: {error}"); }
            finally { clients.TryRemove(id, out _); slots.Release(); }
        }
        void Accept(Stream stream)
        {
            var id = Interlocked.Increment(ref next);
            // Task.Run prevents a synchronously completed connection from being
            // removed before it is registered below.
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            clients[id] = Task.Run(async () => { await start.Task; await ServeAsync(stream, id); });
            start.SetResult();
        }
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var name = LocalTransport.PipeName(endpoint);
                // Exclusive process lock prevents another Host joining this pipe's server pool.
                var lockDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CMakeWorkflow", "host");
                Directory.CreateDirectory(lockDirectory);
                using var ownership = new FileStream(Path.Combine(lockDirectory, name + ".lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var announced = false;
                while (!ct.IsCancellationRequested)
                {
                    await slots.WaitAsync(ct);
                    var pipe = new NamedPipeServerStream(name, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    if (!announced) { Console.Error.WriteLine($"[Host] Listening: {endpoint}"); announced = true; }
                    try { await pipe.WaitForConnectionAsync(ct); Accept(pipe); }
                    catch { pipe.Dispose(); slots.Release(); throw; }
                }
            }
            else
            {
                LocalTransport.ValidateUnixEndpoint(endpoint);
                using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(endpoint));
                try
                {
                    File.SetUnixFileMode(endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    listener.Listen(16);
                    Console.Error.WriteLine($"[Host] Listening: {endpoint}");
                    while (!ct.IsCancellationRequested)
                    {
                        await slots.WaitAsync(ct);
                        try { Accept(new NetworkStream(await listener.AcceptAsync(ct), ownsSocket: true)); }
                        catch { slots.Release(); throw; }
                    }
                }
                finally { File.Delete(endpoint); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { shutdownHost.Cancel(); await Task.WhenAll(clients.Values); await executions.DrainAsync(); }
    }
}
