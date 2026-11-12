using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using CMakeWorkflow.Host.Provisioning;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Tooling.Tools;
using StreamJsonRpc;

namespace CMakeWorkflow.Host.Transport;

/// <summary>Hosts initialized RPC sessions and shared durable execution coordinators.</summary>
public static class HostServer
{
    /// <summary>Serves one private Host over binary standard input/output.</summary>
    /// <remarks>
    /// The private coordinator belongs to the channel: EOF/disconnect cancels execution and drains cleanup before exit.
    /// Durable records can survive, but active execution does not outlive this private Host.
    /// </remarks>
    public static Task RunStdioAsync(Stream input, Stream output, CancellationToken ct) =>
        StdioHostServer.RunAsync(input, output, ct);

    /// <summary>Serves a persistent local Host with at most 16 simultaneous clients.</summary>
    /// <remarks>
    /// Each connection owns and drains its request/session work on disconnect. Durable execution belongs to the shared Host
    /// coordinator and survives an individual client disconnect. Host shutdown cancels that coordinator and drains its tasks.
    /// The server owns listener/client streams and endpoint cleanup; ledger records and results remain for recovery.
    /// </remarks>
    /// <param name="endpoint">Validated current-user named pipe or absolute Unix socket endpoint.</param>
    /// <param name="ct">Host lifetime token, distinct from request and connection cancellation.</param>
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
                    using DoctorSession session = new(
                        workspace => new ToolingService(new SystemToolingRuntime(workspace), installer),
                        value => rpc.NotifyAsync("tooling/progress", value), disconnected.Token,
                        fixes, value => rpc.NotifyAsync("tooling/fixProgress", value), durableExecutions: executions);
                    rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<Protocol.IHostRpc>(), session, null);
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

        // The accept loop owns a pending stream until registration succeeds.
        // Clearing the ref transfers it to ServeAsync, which releases it on disconnect.
        void Accept<TStream>(ref TStream? pending) where TStream : Stream
        {
            TStream stream = pending ?? throw new ArgumentNullException(nameof(pending));
            long id = Interlocked.Increment(ref next);
            // The gate prevents synchronous completion before the task is registered.
            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            clients[id] = Task.Run(async () =>
            {
                await start.Task;
                await ServeAsync(stream, id);
            }, CancellationToken.None);
            pending = null;
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
                    try
                    {
                        if (!announced)
                        {
                            await Console.Error.WriteLineAsync($"[Host] Listening: {endpoint}");
                            announced = true;
                        }

                        NamedPipeServerStream? pipe = await WaitForClientPipeAsync(name, ct);
                        try
                        {
                            Accept(ref pipe); // Transfer only after the worker is registered.
                        }
                        finally
                        {
                            if (pipe is not null)
                            {
                                await pipe.DisposeAsync();
                            }
                        }
                    }
                    catch
                    {
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
                            NetworkStream? stream = await AcceptSocketAsync(listener, ct);
                            try
                            {
                                Accept(ref stream); // Transfer only after the worker is registered.
                            }
                            finally
                            {
                                if (stream is not null)
                                {
                                    await stream.DisposeAsync();
                                }
                            }
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

    // These factories dispose an unaccepted resource on failure and transfer it only on success.
    // The caller transfers the successful result to ServeAsync, which owns its disposal.
    private static async Task<NamedPipeServerStream> WaitForClientPipeAsync(string name, CancellationToken ct)
    {
        NamedPipeServerStream pipe = new(name, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.WaitForConnectionAsync(ct);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private static async Task<NetworkStream> AcceptSocketAsync(Socket listener, CancellationToken ct)
    {
        Socket socket = await listener.AcceptAsync(ct);
        try
        {
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
