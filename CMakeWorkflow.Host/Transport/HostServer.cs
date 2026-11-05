using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using CMakeWorkflow.Tooling;
using StreamJsonRpc;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

public static class HostServer
{
    /// <summary>Serve one private Host directly on a binary stdin/stdout pair.</summary>
    public static Task RunStdioAsync(Stream input, Stream output, CancellationToken ct) =>
        StdioHostServer.RunAsync(input, output, ct);

    public static async Task RunAsync(string endpoint, CancellationToken ct)
    {
        using CancellationTokenSource shutdownHost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = shutdownHost.Token;
        // Max 16 active clients; request lifetimes are tied to their connection.
        ConcurrentDictionary<long, Task> clients = new();
        using SemaphoreSlim slots = new(16);
        using FixCoordinator fixes = new();
        using DurableExecutionCoordinator executions = new(ct);
        using HttpClient downloads = VerifiedCMakeInstaller.CreateDownloadClient();
        VerifiedCMakeInstaller installer = new(downloads);
        long next = 0;

        async Task ServeAsync(Stream stream, long id)
        {
            try
            {
                using (stream)
                using (CancellationTokenSource disconnected = CancellationTokenSource.CreateLinkedTokenSource(ct))
                using (JsonRpc rpc = HostConnection.Create(stream))
                {
                    DoctorSession session = new(
                        workspace => new ToolingService(new SystemToolingRuntime(workspace), installer),
                        value => rpc.NotifyAsync("tooling/progress", value), disconnected.Token,
                        fixes, value => rpc.NotifyAsync("tooling/fixProgress", value), durableExecutions: executions);
                    rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IHostRpc>(), session, null);
                    rpc.Disconnected += (_, _) => disconnected.Cancel();
                    using CancellationTokenRegistration shutdown = ct.Register(rpc.Dispose);
                    rpc.StartListening();
                    try
                    {
                        await rpc.Completion;
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        await Console.Error.WriteLineAsync($"[Host] Connection ended: {error}");
                    }
                    finally
                    {
                        await disconnected.CancelAsync();
                        await session.DrainAsync();
                    }
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                await Console.Error.WriteLineAsync($"[Host] Session failed: {error}");
            }
            finally
            {
                _ = clients.TryRemove(id, out _);
                _ = slots.Release();
            }
        }

        void Accept(Stream stream)
        {
            long id = Interlocked.Increment(ref next);
            // Task.Run prevents a synchronously completed connection from being
            // removed before it is registered below.
            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            clients[id] = Task.Run(async () =>
            {
                await start.Task;
                await ServeAsync(stream, id);
            }, ct);
            start.SetResult();
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                string name = LocalTransport.PipeName(endpoint);
                // Exclusive process lock prevents another Host joining this pipe's server pool.
                string lockDirectory =
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "CMakeWorkflow", "host");
                _ = Directory.CreateDirectory(lockDirectory);
                using FileStream ownership = new(Path.Combine(lockDirectory, name + ".lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                bool announced = false;
                while (!ct.IsCancellationRequested)
                {
                    await slots.WaitAsync(ct);

#pragma warning disable CA2000 // Ownership transfers to the returned object or connection task; surrounding using/catch/finally paths release failures.
                    NamedPipeServerStream pipe = new(name, PipeDirection.InOut,
                                            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                                            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#pragma warning restore CA2000

                    if (!announced)
                    {
                        await Console.Error.WriteLineAsync($"[Host] Listening: {endpoint}");
                        announced = true;
                    }

                    try
                    {
                        await pipe.WaitForConnectionAsync(ct);
                        Accept(pipe);
                    }
                    catch
                    {
                        await pipe.DisposeAsync();
                        _ = slots.Release();
                        throw;
                    }
                }
            }
            else
            {
                LocalTransport.ValidateUnixEndpoint(endpoint);
                using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(endpoint));
                try
                {
                    File.SetUnixFileMode(endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    listener.Listen(16);
                    await Console.Error.WriteLineAsync($"[Host] Listening: {endpoint}");
                    while (!ct.IsCancellationRequested)
                    {
                        await slots.WaitAsync(ct);
                        try
                        {

#pragma warning disable CA2000 // Ownership transfers to the returned object or connection task; surrounding using/catch/finally paths release failures.
                            Accept(new NetworkStream(await listener.AcceptAsync(ct), ownsSocket: true));
#pragma warning restore CA2000

                        }
                        catch
                        {
                            _ = slots.Release();
                            throw;
                        }
                    }
                }
                finally
                {
                    File.Delete(endpoint);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            await shutdownHost.CancelAsync();
            await Task.WhenAll(clients.Values);
            await executions.DrainAsync();
        }
    }
}